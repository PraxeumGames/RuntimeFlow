using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RuntimeFlow.Events;
using RuntimeFlow.Initialization.Graph;
using VContainer;

namespace RuntimeFlow.Contexts
{
    public partial class GameContextBuilder
    {
        private (HashSet<Type> InitializedServices, Dictionary<Type, object> AvailableServices) CreateSeededInitializationState(
            params IGameContext?[] contexts)
        {
            var initializedServices = new HashSet<Type>();
            var availableServices = new Dictionary<Type, object>();

            foreach (var context in contexts)
            {
                SeedInitializedFromContext(context, initializedServices, availableServices);
            }

            return (initializedServices, availableServices);
        }

        private async Task<GameContext> CreateAndInitializeScopeContextAsync(
            GameContextType scope,
            IGameContext parentContext,
            IReadOnlyCollection<Action<IGameContext>> registrations,
            IReadOnlyCollection<ServiceDescriptor> autoServices,
            Action<IGameContext>? initializedCallback,
            ISet<Type> initializedServices,
            IDictionary<Type, object> availableServices,
            IInitializationProgressNotifier progressNotifier,
            long generation,
            CancellationToken cancellationToken,
            Type? scopeKey = null,
            bool skipActivation = false,
            ScopeEventBus? eventBus = null)
        {
            _logger.LogDebug("Building scope {Scope}", scope);
            SetScopeStateIfTracked(scope, ScopeLifecycleState.Loading, scopeKey);
            ThrowIfStaleGeneration(generation, cancellationToken);
            GameContext? context = null;
            var scopeStopwatch = Stopwatch.StartNew();
            try
            {
                context = CreateContext(parentContext, registrations, autoServices, initializedCallback, initialize: true, availableServices, eventBus);
                var totalServices = await ExecuteInitializersAsync(scope, context, initializedServices, progressNotifier, generation, cancellationToken, scopeKey).ConfigureAwait(false);
                ThrowIfStaleGeneration(generation, cancellationToken);
                if (scope != GameContextType.Global && !skipActivation)
                {
                    await ExecuteScopeActivationEnterAsync(scope, context, progressNotifier, totalServices, cancellationToken).ConfigureAwait(false);
                }

                progressNotifier.OnScopeCompleted(scope, totalServices);
                ThrowIfStaleGeneration(generation, cancellationToken);
                scopeStopwatch.Stop();
                _logger.LogInformation("Scope {Scope} initialized ({ServiceCount} services, {Duration}s)", scope, totalServices, scopeStopwatch.Elapsed.TotalSeconds.ToString("F2"));
            }
            catch (Exception ex)
            {
                var isStaleGenerationCancellation = IsStaleGenerationCancellation(ex, cancellationToken);
                SetScopeStateIfTracked(
                    scope,
                    isStaleGenerationCancellation ? ScopeLifecycleState.Deactivating : ScopeLifecycleState.Failed,
                    scopeKey);

                var cleanupCancellationToken = CreateFailureCleanupCancellationToken();
                var cleanupFailures = await CaptureCleanupFailuresAsync(
                        cleanupCancellationToken,
                        async () =>
                        {
                            await DisposeScopeContextAsync(scope, context, cleanupCancellationToken, scopeKey).ConfigureAwait(false);
                            context = null;
                        })
                    .ConfigureAwait(false);

                if (cleanupFailures.Count > 0)
                {
                    SetScopeStateIfTracked(scope, ScopeLifecycleState.Failed, scopeKey);
                    throw CreateCleanupAggregateException($"Initialize {scope} scope", ex, cleanupFailures);
                }

                if (isStaleGenerationCancellation)
                    SetScopeStateIfTracked(scope, ScopeLifecycleState.Disposed, scopeKey);

                throw;
            }

            SetScopeStateIfTracked(scope, skipActivation ? ScopeLifecycleState.Preloaded : ScopeLifecycleState.Active, scopeKey);
            return context;
        }

        private static bool IsStaleGenerationCancellation(Exception exception, CancellationToken cancellationToken)
        {
            return exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;
        }

        private void SeedInitializedFromContext(
            IGameContext? context,
            ISet<Type> initializedServices,
            IDictionary<Type, object> availableServices)
        {
            if (context is not GameContext gameContext)
                return;

            if (!TryResolveScopeIdentity(gameContext, out var scope, out var scopeKey))
                return;

            var initOrder = _scopeInitializationLedger.GetInitializationOrder(scope, scopeKey);
            if (initOrder == null)
                return;

            foreach (var initializer in initOrder)
            {
                initializedServices.Add(initializer.ServiceType);
                availableServices[initializer.ServiceType] = gameContext.Resolve(initializer);
            }
        }

        private bool TryResolveScopeIdentity(
            GameContext context,
            out GameContextType scope,
            out Type? scopeKey)
        {
            if (ReferenceEquals(context, _globalContext))
            {
                scope = GameContextType.Global;
                scopeKey = null;
                return true;
            }

            if (ReferenceEquals(context, _sessionContext))
            {
                scope = GameContextType.Session;
                scopeKey = null;
                return true;
            }

            if (ReferenceEquals(context, _sceneContext))
            {
                scope = GameContextType.Scene;
                scopeKey = _activeSceneScopeKey;
                return true;
            }

            if (ReferenceEquals(context, _moduleContext))
            {
                scope = GameContextType.Module;
                scopeKey = _activeModuleScopeKey;
                return true;
            }

            foreach (var kvp in _preloadedContexts)
            {
                if (ReferenceEquals(context, kvp.Value))
                {
                    scope = GameContextType.Scene;
                    scopeKey = kvp.Key;
                    return true;
                }
            }

            foreach (var kvp in _additiveModuleContexts)
            {
                if (ReferenceEquals(context, kvp.Value))
                {
                    scope = GameContextType.Module;
                    scopeKey = kvp.Key;
                    return true;
                }
            }

            scope = default;
            scopeKey = null;
            return false;
        }

        private static GameContext CreateContext(
            IGameContext? parent,
            IReadOnlyCollection<Action<IGameContext>> registrations,
            IReadOnlyCollection<ServiceDescriptor> autoServices,
            Action<IGameContext>? initializedCallback,
            bool initialize,
            IDictionary<Type, object> availableServices,
            ScopeEventBus? eventBus = null)
        {
            var context = new GameContext(parent);
            foreach (var registration in registrations)
                registration(context);

            if (eventBus != null)
            {
                context.RegisterInstance<IScopeEventBus>(eventBus);
                context.OnBeforeDispose += eventBus.Dispose;
            }

            RegisterAutoServices(context, autoServices, availableServices);

            if (initializedCallback != null)
                context.OnInitialized += () => initializedCallback(context);
            if (initialize)
                context.Initialize();
            return context;
        }

        private async Task<int> ExecuteInitializersAsync(
            GameContextType scope,
            GameContext context,
            ISet<Type> initializedServices,
            IInitializationProgressNotifier progressNotifier,
            long generation,
            CancellationToken cancellationToken,
            Type? scopeKey = null)
        {
            var startupPlan = CreateScopeStartupPlan(scope, context, initializedServices, scopeKey);
            var totalServices = startupPlan.TotalServiceCount;
            progressNotifier.OnScopeStarted(scope, totalServices);
            if (totalServices == 0)
            {
                return totalServices;
            }

            var initOrder = new List<ServiceInitializerBinding>();
            var completedServices = 0;
            void RecordInitializedForDisposal(ServiceInitializerBinding initializer)
            {
                RegisterInitializedServiceForScopeDisposal(scope, scopeKey, initializer);
            }

            void RecordSuccessfulInitializerTasks(
                IEnumerable<(Task task, ServiceInitializerBinding initializer)> initializerTasks)
            {
                foreach (var initializerTask in initializerTasks)
                {
                    if (initializerTask.task.Status == TaskStatus.RanToCompletion)
                        RecordInitializedForDisposal(initializerTask.initializer);
                }
            }

            if (startupPlan.EntryPoints != null)
            {
                ThrowIfStaleGeneration(generation, cancellationToken);
                progressNotifier.OnServiceStarted(scope, startupPlan.EntryPoints.ProgressServiceType, completedServices, totalServices);
                await ExecuteVContainerEntryPointInitializablesAsync(startupPlan.EntryPoints, cancellationToken)
                    .ConfigureAwait(false);
                ThrowIfStaleGeneration(generation, cancellationToken);
                foreach (var marker in startupPlan.EntryPoints.CompletedDependencyMarkers)
                    initializedServices.Add(marker);
                completedServices++;
                progressNotifier.OnServiceCompleted(scope, startupPlan.EntryPoints.ProgressServiceType, completedServices, totalServices);
            }

            if (startupPlan.GlobalBootstrapOperations.Count > 0)
            {
                completedServices = await ExecuteGlobalBootstrapOperationsAsync(
                        scope,
                        context,
                        startupPlan.GlobalBootstrapOperations,
                        progressNotifier,
                        completedServices,
                        totalServices,
                        generation,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (startupPlan.AsyncInitializers.Count == 0)
            {
                _scopeInitializationLedger.SetInitializationOrder(scope, scopeKey, initOrder);
                await StartVContainerStartablesAsync(startupPlan.EntryPoints, cancellationToken)
                    .ConfigureAwait(false);
                return totalServices;
            }

            var pending = startupPlan.AsyncInitializers.ToDictionary(initializer => initializer.ServiceType);
            ValidateDependencies(scope, context, pending, initializedServices);

            while (pending.Count > 0)
            {
                ThrowIfStaleGeneration(generation, cancellationToken);
                var ready = pending.Values
                    .Where(initializer => initializer.Dependencies.All(dependency =>
                        IsDependencyReadyForCurrentScope(dependency, pending, initializedServices)))
                    .ToArray();

                if (ready.Length == 0)
                {
                    var dependencyGraph = pending.ToDictionary(
                        kvp => kvp.Key,
                        kvp => (IReadOnlyCollection<Type>)kvp.Value.Dependencies
                            .Where(pending.ContainsKey)
                            .ToArray());

                    var cyclePath = DependencyCycleDetector.DetectCyclePath(dependencyGraph);
                    var unresolved = string.Join(", ", pending.Keys.Select(type => type.Name));
                    var cycleDescription = cyclePath != null
                        ? $"Cycle: {string.Join(" → ", cyclePath.Select(t => t.Name))}. "
                        : string.Empty;

                    throw new InvalidOperationException(
                        $"Initialization dependency cycle detected in scope {scope}. " +
                        $"{cycleDescription}" +
                        $"Remaining services: {unresolved}");
                }

                foreach (var initializer in ready)
                    progressNotifier.OnServiceStarted(scope, initializer.ServiceType, completedServices, totalServices);

                var uniqueImplementations = ready
                    .GroupBy(initializer => initializer.ImplementationType)
                    .Select(group => group.First())
                    .ToArray();

                var taskMap = uniqueImplementations
                    .Select(initializer => (
                        task: ExecuteInitializerWithHealthAsync(scope, context, initializer, progressNotifier, completedServices, totalServices, cancellationToken),
                        initializer))
                    .ToArray();

                var waveTask = Task.WhenAll(taskMap.Select(t => t.task));
                var waveStallTimeout = _healthSupervisor.Options.WaveStallTimeout;
                try
                {
                    if (_healthSupervisor.IsEnabled && waveStallTimeout > TimeSpan.Zero && waveStallTimeout != Timeout.InfiniteTimeSpan)
                    {
                        var completedFirst = await Task.WhenAny(waveTask, Task.Delay(waveStallTimeout, cancellationToken)).ConfigureAwait(false);
                        if (completedFirst != waveTask)
                        {
                            var stalledNames = taskMap
                                .Where(t => !t.task.IsCompleted)
                                .Select(t => t.initializer.ServiceType.Name)
                                .ToArray();

                            if (stalledNames.Length > 0)
                            {
                                _logger.LogWarning(
                                    "[RuntimeFlow] Wave stall detected in scope {Scope}: {Count} service(s) haven't completed after {Timeout:F0}s: {Services}",
                                    scope, stalledNames.Length, waveStallTimeout.TotalSeconds, string.Join(", ", stalledNames));
                            }

                            await waveTask.ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await waveTask.ConfigureAwait(false);
                    }
                }
                catch
                {
                    RecordSuccessfulInitializerTasks(taskMap);
                    throw;
                }

                foreach (var initializer in ready)
                    RecordInitializedForDisposal(initializer);
                ThrowIfStaleGeneration(generation, cancellationToken);
                foreach (var initializer in ready)
                {
                    pending.Remove(initializer.ServiceType);
                    initializedServices.Add(initializer.ServiceType);
                    initOrder.Add(initializer);
                    completedServices++;
                    progressNotifier.OnServiceCompleted(scope, initializer.ServiceType, completedServices, totalServices);
                }
            }

            _scopeInitializationLedger.SetInitializationOrder(scope, scopeKey, initOrder);
            await StartVContainerStartablesAsync(startupPlan.EntryPoints, cancellationToken)
                .ConfigureAwait(false);
            return totalServices;
        }

        private ScopeStartupPlan CreateScopeStartupPlan(
            GameContextType scope,
            GameContext context,
            ISet<Type> initializedServices,
            Type? scopeKey)
        {
            var initializers = DiscoverInitializers(context);

            var lazyBindings = initializers
                .Where(b => typeof(ILazyInitializableService).IsAssignableFrom(b.ImplementationType))
                .ToList();
            foreach (var lazy in lazyBindings)
            {
                initializers.Remove(lazy);
                _lazyInitialization.RegisterLazyBinding(lazy, context, scope, scopeKey);
            }

            return new ScopeStartupPlan(
                TryCreateVContainerEntryPointsStartupPlan(scope, context),
                scope == GameContextType.Global
                    ? DiscoverGlobalBootstrapOperations(context)
                    : Array.Empty<GlobalBootstrapOperationBinding>(),
                initializers.ToArray());
        }

        private static IReadOnlyList<GlobalBootstrapOperationBinding> DiscoverGlobalBootstrapOperations(
            GameContext context)
        {
            return context
                .GetRegistrationsForServiceType(typeof(IGlobalBootstrapOperation))
                .Where(registration => typeof(IGlobalBootstrapOperation).IsAssignableFrom(registration.ImplementationType))
                .GroupBy(registration => registration.ImplementationType)
                .Select(group => new GlobalBootstrapOperationBinding(group.Key, group.First()))
                .ToArray();
        }

        private VContainerEntryPointsStartupPlan? TryCreateVContainerEntryPointsStartupPlan(
            GameContextType scope,
            GameContext context)
        {
            var settingsRegistrations = context
                .GetRegistrationsForServiceType(typeof(RuntimeFlowVContainerEntryPointsSettings));
            if (settingsRegistrations.Count == 0)
                return null;

            var settings = MergeVContainerEntryPointSettings(settingsRegistrations
                .Select(registration => (RuntimeFlowVContainerEntryPointsSettings)context.Resolve(registration))
                .ToArray(), context);
            var scopeResolver = context.Resolver;
            var entryPointResolver = RuntimeFlowVContainerEntryPointPhaseRunner.ResolveEntryPointResolver(scope, scopeResolver);
            return new VContainerEntryPointsStartupPlan(
                scope,
                ResolveScopeName(scope),
                scopeResolver,
                entryPointResolver,
                settings,
                RuntimeFlowVContainerEntryPointPhaseRunner.GetScopeLocalRegistrations<VContainer.Unity.IInitializable>(entryPointResolver, settings),
                RuntimeFlowVContainerEntryPointPhaseRunner.GetScopeLocalRegistrations<VContainer.Unity.IStartable>(entryPointResolver, settings),
                ResolveEntryPointCompletedDependencyMarkers(scope),
                useSessionStageOrder: scope == GameContextType.Session);
        }

        private static RuntimeFlowVContainerEntryPointsSettings MergeVContainerEntryPointSettings(
            IReadOnlyList<RuntimeFlowVContainerEntryPointsSettings> settings,
            GameContext context)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (context == null) throw new ArgumentNullException(nameof(context));

            var contributions = ResolveVContainerEntryPointSettingsContributions(context);

            if (settings.Count == 0 && contributions.Length == 0)
                return RuntimeFlowVContainerEntryPointsSettings.Default;

            if (settings.Count == 1 && contributions.Length == 0)
                return settings[0];

            var excludedInitializables = settings
                .SelectMany(item => item.ExcludedInitializableImplementationTypes)
                .Concat(contributions.SelectMany(item => item.ExcludedInitializableImplementationTypes))
                .Distinct()
                .ToArray();
            var excludedStartables = settings
                .SelectMany(item => item.ExcludedStartableImplementationTypes)
                .Concat(contributions.SelectMany(item => item.ExcludedStartableImplementationTypes))
                .Distinct()
                .ToArray();
            var prioritizedInitializables = settings
                .SelectMany(item => item.PrioritizedInitializableImplementationTypes)
                .Distinct()
                .ToArray();
            var afterCallbacks = settings
                .Select(item => item.AfterPrioritizedInitializablesInitialized)
                .Where(callback => callback != null)
                .ToArray();

            return new RuntimeFlowVContainerEntryPointsSettings(
                excludedInitializables,
                excludedStartables,
                prioritizedInitializables,
                afterCallbacks.Length == 0
                    ? null
                    : resolver =>
                    {
                        foreach (var callback in afterCallbacks)
                        {
                            callback!(resolver);
                        }
                    });
        }

        private static RuntimeFlowVContainerEntryPointsSettingsContribution[] ResolveVContainerEntryPointSettingsContributions(
            GameContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var contributions = new List<RuntimeFlowVContainerEntryPointsSettingsContribution>();
            var current = context;
            while (current != null)
            {
                foreach (var registration in current.GetRegistrationsForServiceType(typeof(RuntimeFlowVContainerEntryPointsSettingsContribution)))
                {
                    if (current.Resolve(registration) is RuntimeFlowVContainerEntryPointsSettingsContribution contribution)
                    {
                        contributions.Add(contribution);
                    }
                }

                current = current.Parent as GameContext;
            }

            return contributions
                .Distinct()
                .ToArray();
        }

        private static IReadOnlyCollection<Type> ResolveEntryPointCompletedDependencyMarkers(GameContextType scope)
        {
            if (scope == GameContextType.Session)
            {
                return new[]
                {
                    typeof(RuntimeFlowVContainerEntryPointsStartupPhase),
                    typeof(IRuntimeFlowSessionSyncEntryPointsBootstrapService)
                };
            }

            return new[] { typeof(RuntimeFlowVContainerEntryPointsStartupPhase) };
        }

        private async Task ExecuteVContainerEntryPointInitializablesAsync(
            VContainerEntryPointsStartupPlan entryPoints,
            CancellationToken cancellationToken)
        {
            if (entryPoints == null) throw new ArgumentNullException(nameof(entryPoints));

            await _executionScheduler.ExecuteAsync(
                    InitializationThreadAffinity.MainThread,
                    token => RuntimeFlowVContainerEntryPointPhaseRunner.InitializeInitializablesAsync(
                        entryPoints,
                        _logger,
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task StartVContainerStartablesAsync(
            VContainerEntryPointsStartupPlan? entryPoints,
            CancellationToken cancellationToken)
        {
            if (entryPoints == null)
                return;

            await _executionScheduler.ExecuteAsync(
                    InitializationThreadAffinity.MainThread,
                    token => RuntimeFlowVContainerEntryPointPhaseRunner.StartStartablesAsync(
                        entryPoints,
                        _logger,
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<int> ExecuteGlobalBootstrapOperationsAsync(
            GameContextType scope,
            GameContext context,
            IReadOnlyList<GlobalBootstrapOperationBinding> operationBindings,
            IInitializationProgressNotifier progressNotifier,
            int completedServices,
            int totalServices,
            long generation,
            CancellationToken cancellationToken)
        {
            if (scope != GameContextType.Global)
                throw new InvalidOperationException("Global bootstrap operations can only run in the global scope.");

            var operations = operationBindings
                .Select(binding =>
                {
                    var operation = context.Resolve(binding.Registration) as IGlobalBootstrapOperation;
                    if (operation == null)
                    {
                        throw new InvalidOperationException(
                            $"Global bootstrap operation '{binding.ImplementationType.Name}' does not implement {nameof(IGlobalBootstrapOperation)}.");
                    }

                    return (binding, operation);
                })
                .OrderBy(item => item.operation.Order)
                .ThenBy(item => NormalizeOperationName(item.operation.Name, item.binding.ImplementationType), StringComparer.Ordinal)
                .ThenBy(item => item.binding.ImplementationType.FullName ?? item.binding.ImplementationType.Name, StringComparer.Ordinal)
                .ToArray();

            var totalOperations = operations.Length;
            for (var index = 0; index < operations.Length; index++)
            {
                ThrowIfStaleGeneration(generation, cancellationToken);
                var (binding, operation) = operations[index];
                var operationName = NormalizeOperationName(operation.Name, binding.ImplementationType);
                var operationContext = new StartupOperationContext(
                    scope,
                    RuntimeStartupOperationPhases.GlobalBootstrapOperations,
                    operationName,
                    operationIndex: index,
                    totalOperations,
                    binding.ImplementationType,
                    progressNotifier,
                    completedServices,
                    totalServices);

                progressNotifier.OnServiceStarted(scope, binding.ImplementationType, completedServices, totalServices);
                operationContext.NotifyStarted();
                try
                {
                    await ExecuteStartupOperationWithHealthAsync(
                            scope,
                            binding.ImplementationType,
                            operation,
                            operationContext,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
                {
                    var startupCancellation = ex as RuntimeStartupOperationCanceledException
                                              ?? new RuntimeStartupOperationCanceledException(
                                                  scope,
                                                  RuntimeStartupOperationPhases.GlobalBootstrapOperations,
                                                  operationName,
                                                  operationContext.LastStep,
                                                  operationContext.LastDetail,
                                                  ex);
                    operationContext.NotifyFailed(startupCancellation);
                    throw startupCancellation;
                }
                catch (Exception ex)
                {
                    var startupException = ex as RuntimeStartupOperationException
                                           ?? new RuntimeStartupOperationException(
                                               scope,
                                               RuntimeStartupOperationPhases.GlobalBootstrapOperations,
                                               operationName,
                                               operationContext.LastStep,
                                               operationContext.LastDetail,
                                               ex);
                    operationContext.NotifyFailed(startupException);
                    _logger.LogError(
                        startupException,
                        "Global bootstrap operation failed. phase={Phase}, operation={Operation}, step={Step}, detail={Detail}",
                        RuntimeStartupOperationPhases.GlobalBootstrapOperations,
                        operationName,
                        operationContext.LastStep ?? "<none>",
                        operationContext.LastDetail ?? "<none>");
                    throw startupException;
                }

                ThrowIfStaleGeneration(generation, cancellationToken);
                completedServices++;
                operationContext.NotifyCompleted(completedServices);
                progressNotifier.OnServiceCompleted(scope, binding.ImplementationType, completedServices, totalServices);
            }

            return completedServices;
        }

        private async Task ExecuteStartupOperationWithHealthAsync(
            GameContextType scope,
            Type operationType,
            IGlobalBootstrapOperation operation,
            StartupOperationContext operationContext,
            CancellationToken cancellationToken)
        {
            var affinity = operation is IInitializationThreadAffinityProvider affinityProvider
                ? affinityProvider.ThreadAffinity
                : InitializationThreadAffinity.MainThread;

            var timeout = _healthSupervisor.GetServiceTimeout(scope, operationType);
            var stopwatch = Stopwatch.StartNew();
            using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_healthSupervisor.IsEnabled && timeout != Timeout.InfiniteTimeSpan)
            {
                operationCts.CancelAfter(timeout);
            }

            try
            {
                await _executionScheduler.ExecuteAsync(
                        affinity,
                        async token => await operation.ExecuteAsync(operationContext, token).ConfigureAwait(false),
                        operationCts.Token)
                    .ConfigureAwait(false);

                stopwatch.Stop();
                _healthSupervisor.RecordServiceSuccess(scope, operationType, stopwatch.Elapsed, timeout);
            }
            catch (OperationCanceledException ex)
                when (_healthSupervisor.IsEnabled
                      && operationCts.IsCancellationRequested
                      && !cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                var critical = new RuntimeHealthCriticalException(
                    scope,
                    operationType,
                    timeout,
                    stopwatch.Elapsed,
                    ex);
                _healthSupervisor.RecordServiceFailure(
                    scope,
                    operationType,
                    stopwatch.Elapsed,
                    timeout,
                    critical);
                throw new RuntimeStartupOperationException(
                    scope,
                    operationContext.Phase,
                    operationContext.OperationName,
                    operationContext.LastStep,
                    operationContext.LastDetail,
                    critical);
            }
            catch (OperationCanceledException ex)
                when (cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                throw new RuntimeStartupOperationCanceledException(
                    scope,
                    operationContext.Phase,
                    operationContext.OperationName,
                    operationContext.LastStep,
                    operationContext.LastDetail,
                    ex);
            }
            catch (RuntimeStartupOperationException)
            {
                stopwatch.Stop();
                throw;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _healthSupervisor.RecordServiceFailure(
                    scope,
                    operationType,
                    stopwatch.Elapsed,
                    timeout,
                    ex);
                throw;
            }
        }

        private static string NormalizeOperationName(string? operationName, Type implementationType)
        {
            return string.IsNullOrWhiteSpace(operationName)
                ? implementationType.Name
                : operationName.Trim();
        }

        private static bool IsDependencyReadyForCurrentScope(
            Type dependency,
            IReadOnlyDictionary<Type, ServiceInitializerBinding> pending,
            ISet<Type> initializedServices)
        {
            return !pending.ContainsKey(dependency) && initializedServices.Contains(dependency);
        }

        private sealed class StartupOperationContext : IStartupOperationContext
        {
            private readonly Type _operationType;
            private readonly IInitializationProgressNotifier _progressNotifier;
            private readonly int _completedServices;
            private readonly int _totalServices;

            public StartupOperationContext(
                GameContextType scope,
                string phase,
                string operationName,
                int operationIndex,
                int totalOperations,
                Type operationType,
                IInitializationProgressNotifier progressNotifier,
                int completedServices,
                int totalServices)
            {
                Scope = scope;
                Phase = phase;
                OperationName = operationName;
                OperationIndex = operationIndex;
                TotalOperations = totalOperations;
                _operationType = operationType;
                _progressNotifier = progressNotifier;
                _completedServices = completedServices;
                _totalServices = totalServices;
            }

            public GameContextType Scope { get; }
            public string Phase { get; }
            public string OperationName { get; }
            public int OperationIndex { get; }
            public int TotalOperations { get; }
            public string? LastStep { get; private set; }
            public string? LastDetail { get; private set; }
            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

            public void ReportStep(string step, string? detail = null)
            {
                LastStep = string.IsNullOrWhiteSpace(step) ? "<unknown>" : step.Trim();
                LastDetail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();
                var message = FormatMessage(Phase, OperationName, LastStep, LastDetail);
                _progressNotifier.OnServiceProgress(
                    Scope,
                    _operationType,
                    0f,
                    message,
                    _completedServices,
                    _totalServices);

                if (_progressNotifier is IStartupOperationProgressNotifier startupNotifier)
                {
                    startupNotifier.OnStartupOperationStep(
                        Scope,
                        Phase,
                        OperationName,
                        LastStep,
                        LastDetail,
                        _completedServices,
                        _totalServices,
                        _stopwatch.Elapsed);
                }
            }

            public void NotifyStarted()
            {
                if (_progressNotifier is IStartupOperationProgressNotifier startupNotifier)
                {
                    startupNotifier.OnStartupOperationStarted(
                        Scope,
                        Phase,
                        OperationName,
                        _completedServices,
                        _totalServices,
                        _stopwatch.Elapsed);
                }
            }

            public void NotifyCompleted(int completedServices)
            {
                _stopwatch.Stop();
                if (_progressNotifier is IStartupOperationProgressNotifier startupNotifier)
                {
                    startupNotifier.OnStartupOperationCompleted(
                        Scope,
                        Phase,
                        OperationName,
                        completedServices,
                        _totalServices,
                        _stopwatch.Elapsed);
                }
            }

            public void NotifyFailed(Exception exception)
            {
                _stopwatch.Stop();
                if (_progressNotifier is IStartupOperationProgressNotifier startupNotifier)
                {
                    startupNotifier.OnStartupOperationFailed(
                        Scope,
                        Phase,
                        OperationName,
                        LastStep,
                        LastDetail,
                        exception,
                        _completedServices,
                        _totalServices,
                        _stopwatch.Elapsed);
                }
            }

            private static string FormatMessage(
                string phase,
                string operationName,
                string step,
                string? detail)
            {
                var message = $"phase={phase} operation={operationName} step={step}";
                return string.IsNullOrWhiteSpace(detail)
                    ? message
                    : $"{message} detail={detail.Trim()}";
            }
        }

        private static string ResolveScopeName(GameContextType scope)
        {
            return scope.ToString().ToLowerInvariant();
        }

        private async Task ExecuteInitializerWithHealthAsync(
            GameContextType scope,
            GameContext context,
            ServiceInitializerBinding initializer,
            IInitializationProgressNotifier progressNotifier,
            int completedServices,
            int totalServices,
            CancellationToken cancellationToken)
        {
            var resolved = context.Resolve(initializer);
            if (resolved is not IAsyncInitializableService asyncService)
            {
                throw new InvalidOperationException(
                    $"Service {initializer.ServiceType.Name} is expected to implement {nameof(IAsyncInitializableService)}.");
            }

            var affinity = resolved is IInitializationThreadAffinityProvider affinityProvider
                ? affinityProvider.ThreadAffinity
                : InitializationThreadAffinity.MainThread;

            var timeout = _healthSupervisor.GetServiceTimeout(scope, initializer.ServiceType);
            var stopwatch = Stopwatch.StartNew();
            using var serviceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_healthSupervisor.IsEnabled && timeout != Timeout.InfiniteTimeSpan)
            {
                serviceCts.CancelAfter(timeout);
            }

            try
            {
                if (resolved is IProgressAwareInitializableService progressAware)
                {
                    var initContext = new ServiceInitializationContext(scope, initializer.ServiceType, progressNotifier, completedServices, totalServices);
                    await _executionScheduler.ExecuteAsync(
                            affinity,
                            async token => await progressAware.InitializeAsync(initContext, token).ConfigureAwait(false),
                            serviceCts.Token)
                        .ConfigureAwait(false);
                }
                else
                {
                    await _executionScheduler.ExecuteAsync(
                            affinity,
                            async token => await asyncService.InitializeAsync(token).ConfigureAwait(false),
                            serviceCts.Token)
                        .ConfigureAwait(false);
                }

                stopwatch.Stop();
                _logger.LogDebug("Service {ServiceType} initialized ({Duration}ms)", initializer.ServiceType.Name, stopwatch.Elapsed.TotalMilliseconds.ToString("F1"));
                _healthSupervisor.RecordServiceSuccess(scope, initializer.ServiceType, stopwatch.Elapsed, timeout);
            }
            catch (OperationCanceledException ex)
                when (_healthSupervisor.IsEnabled
                      && serviceCts.IsCancellationRequested
                      && !cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                _logger.LogWarning("Service {ServiceType} init slow ({Duration}ms, baseline {Baseline}ms)", initializer.ServiceType.Name, stopwatch.Elapsed.TotalMilliseconds.ToString("F1"), timeout.TotalMilliseconds.ToString("F1"));
                var critical = new RuntimeHealthCriticalException(
                    scope,
                    initializer.ServiceType,
                    timeout,
                    stopwatch.Elapsed,
                    ex);
                _healthSupervisor.RecordServiceFailure(
                    scope,
                    initializer.ServiceType,
                    stopwatch.Elapsed,
                    timeout,
                    critical);
                throw critical;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "Service {ServiceType} initialization failed", initializer.ServiceType.Name);
                _healthSupervisor.RecordServiceFailure(
                    scope,
                    initializer.ServiceType,
                    stopwatch.Elapsed,
                    timeout,
                    ex);
                throw;
            }
        }

        private void ValidateDependencies(
            GameContextType scope,
            GameContext context,
            IReadOnlyDictionary<Type, ServiceInitializerBinding> pending,
            ISet<Type> initializedServices)
        {
            foreach (var initializer in pending.Values)
            {
                foreach (var dependency in initializer.Dependencies)
                {
                    if (!pending.ContainsKey(dependency)
                        && !initializedServices.Contains(dependency)
                        && !IsDependencyAvailableInParent(context.Parent, dependency))
                    {
                        throw new InvalidOperationException(
                            $"Initializer for {initializer.ServiceType.Name} depends on {dependency.Name}, but this dependency was not initialized before scope {scope}.");
                    }
                }
            }
        }

        private static bool IsDependencyAvailableInParent(IGameContext? parent, Type dependency)
        {
            if (parent == null || dependency == null)
                return false;

            if (parent.IsRegistered(dependency))
                return true;

            if (parent is GameContext gameContext)
                return IsDependencyAvailableInParent(gameContext.Parent, dependency);

            return false;
        }
    }
}
