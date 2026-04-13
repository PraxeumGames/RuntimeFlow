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
                SetScopeStateIfTracked(scope, ScopeLifecycleState.Failed, scopeKey);

                var cleanupFailures = await CaptureCleanupFailuresAsync(
                        cancellationToken,
                        async () =>
                        {
                            await DisposeScopeContextAsync(scope, context, cancellationToken, scopeKey).ConfigureAwait(false);
                            context = null;
                        })
                    .ConfigureAwait(false);

                if (cleanupFailures.Count > 0)
                    throw CreateCleanupAggregateException($"Initialize {scope} scope", ex, cleanupFailures);

                throw;
            }

            SetScopeStateIfTracked(scope, skipActivation ? ScopeLifecycleState.Preloaded : ScopeLifecycleState.Active, scopeKey);
            return context;
        }

        private void SeedInitializedFromContext(
            IGameContext? context,
            ISet<Type> initializedServices,
            IDictionary<Type, object> availableServices)
        {
            if (context is not GameContext gameContext)
                return;

            foreach (var initializer in DiscoverInitializers(gameContext))
            {
                initializedServices.Add(initializer.ServiceType);
                availableServices[initializer.ServiceType] = gameContext.Resolve(initializer.ServiceType);
            }
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
            var initializers = DiscoverInitializers(context);

            var lazyBindings = initializers
                .Where(b => typeof(ILazyInitializableService).IsAssignableFrom(b.ImplementationType))
                .ToList();
            foreach (var lazy in lazyBindings)
            {
                initializers.Remove(lazy);
                _lazyInitialization.RegisterLazyBinding(lazy.ServiceType, context, scope, scopeKey);
            }

            var totalServices = initializers.Count;
            progressNotifier.OnScopeStarted(scope, totalServices);
            if (totalServices == 0)
            {
                return totalServices;
            }

            var pending = initializers.ToDictionary(initializer => initializer.ServiceType);
            ValidateDependencies(scope, context, pending, initializedServices);

            var initOrder = new List<Type>();
            var completedServices = 0;
            while (pending.Count > 0)
            {
                ThrowIfStaleGeneration(generation, cancellationToken);
                var ready = pending.Values
                    .Where(initializer => initializer.Dependencies.All(initializedServices.Contains))
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

                ThrowIfStaleGeneration(generation, cancellationToken);
                foreach (var initializer in ready)
                {
                    pending.Remove(initializer.ServiceType);
                    initializedServices.Add(initializer.ServiceType);
                    initOrder.Add(initializer.ServiceType);
                    completedServices++;
                    progressNotifier.OnServiceCompleted(scope, initializer.ServiceType, completedServices, totalServices);
                }
            }

            _scopeInitializationLedger.SetInitializationOrder(scope, scopeKey, initOrder);
            return totalServices;
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
            var resolved = context.Resolve(initializer.ServiceType);
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
