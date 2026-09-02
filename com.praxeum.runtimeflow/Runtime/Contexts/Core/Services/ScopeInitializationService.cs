using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RuntimeFlow.Events;
using RuntimeFlow.Initialization.Graph;
using RuntimeFlow.Initialization.Planning;
using VContainer;
using RuntimeFlow.Health;
using RuntimeFlow.Pipeline;

namespace RuntimeFlow.Contexts
{
    internal sealed class ScopeInitializationService
    {
        private readonly ActiveScopeState _activeState;
        private readonly GameContextScopeRegistry _scopeRegistry;
        private readonly GameContextLazyInitializationRegistry _lazyRegistry;
        private readonly IInitializationExecutionScheduler _scheduler;
        private readonly RuntimeHealthSupervisor _health;
        private readonly ILogger _logger;
        private readonly ScopeActivationService _activation;

        public ScopeInitializationService(
            ActiveScopeState activeState,
            GameContextScopeRegistry scopeRegistry,
            GameContextLazyInitializationRegistry lazyRegistry,
            IInitializationExecutionScheduler scheduler,
            RuntimeHealthSupervisor health,
            ILogger logger,
            ScopeActivationService activation)
        {
            _activeState = activeState;
            _scopeRegistry = scopeRegistry;
            _lazyRegistry = lazyRegistry;
            _scheduler = scheduler;
            _health = health;
            _logger = logger;
            _activation = activation;
        }

        public (HashSet<Type> initialized, Dictionary<Type, object> available) CreateSeededState(params IGameContext?[] contexts)
        {
            var initialized = new HashSet<Type>();
            var available = new Dictionary<Type, object>();
            foreach (var ctx in contexts)
            {
                if (ctx is not GameContext gc)
                    continue;
                foreach (var init in gc.InitializationOrder)
                {
                    // Seeded state reads already-initialized instances straight from the
                    // instance ledger: no construction, no main-thread dispatch required.
                    if (!gc.TryGetInitializedInstance(init, out var instance))
                        throw new InvalidOperationException(
                            $"Seeded state expects an initialized instance for '{init.ServiceType.Name}', but none was recorded.");
                    initialized.Add(init.ServiceType);
                    available[init.ServiceType] = instance;
                }
            }
            return (initialized, available);
        }

        public async Task<GameContext> CreateAndInitializeScopeContextAsync(
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
            Type? scopeKey,
            bool skipActivation,
            ScopeEventBus? eventBus,
            ScopeLifecycleDependencies deps)
        {
            var setState = deps.SetState;
            var throwIfStale = deps.ThrowIfStale;
            _logger.LogDebug("Building scope {Scope}", scope);
            setState(scope, ScopeLifecycleState.Loading, scopeKey);
            throwIfStale(generation, cancellationToken);
            GameContext? context = null;
            var sw = Stopwatch.StartNew();
            try
            {
                context = CreateContext(parentContext, registrations, autoServices, initializedCallback, true, availableServices, eventBus, _scheduler);
                var totalServices = await ExecuteInitializersAsync(scope, context, initializedServices, progressNotifier, generation, cancellationToken, scopeKey, throwIfStale).ConfigureAwait(false);
                throwIfStale(generation, cancellationToken);
                if (scope != GameContextType.Global && !skipActivation)
                    await _activation.ExecuteEnterAsync(scope, context, progressNotifier, totalServices, cancellationToken).ConfigureAwait(false);
                progressNotifier.OnScopeCompleted(scope, totalServices);
                throwIfStale(generation, cancellationToken);
                sw.Stop();
                _logger.LogInformation("Scope {Scope} initialized ({ServiceCount} services, {Duration}s)", scope, totalServices, sw.Elapsed.TotalSeconds.ToString("F2"));
            }
            catch (Exception ex)
            {
                var isStale = deps.IsStaleCancellation(ex, cancellationToken);
                setState(scope, isStale ? ScopeLifecycleState.Deactivating : ScopeLifecycleState.Failed, scopeKey);
                var cleanupToken = deps.FailureCleanupToken();
                var cleanupFailures = await deps.CaptureCleanup(cleanupToken, new Func<Task>[] { async () => { await deps.DisposeScope(scope, context, cleanupToken, scopeKey, null).ConfigureAwait(false); context = null; } }).ConfigureAwait(false);
                if (cleanupFailures.Count > 0)
                {
                    setState(scope, ScopeLifecycleState.Failed, scopeKey);
                    throw deps.CreateAggregate($"Initialize {scope} scope", ex, cleanupFailures);
                }
                if (isStale) setState(scope, ScopeLifecycleState.Disposed, scopeKey);
                throw;
            }
            setState(scope, skipActivation ? ScopeLifecycleState.Preloaded : ScopeLifecycleState.Active, scopeKey);
            return context;
        }

        private static GameContext CreateContext(
            IGameContext? parent,
            IReadOnlyCollection<Action<IGameContext>> registrations,
            IReadOnlyCollection<ServiceDescriptor> autoServices,
            Action<IGameContext>? initializedCallback,
            bool initialize,
            IDictionary<Type, object> availableServices,
            ScopeEventBus? eventBus,
            IInitializationExecutionScheduler? scheduler = null)
        {
            var context = new GameContext(parent) { ExecutionScheduler = scheduler };
            foreach (var r in registrations) r(context);
            if (eventBus != null)
            {
                context.RegisterInstance<IScopeEventBus>(eventBus);
                context.OnBeforeDispose += eventBus.Dispose;
            }
            InitializationGraphResolver.RegisterAutoServices(context, autoServices, availableServices);
            if (initializedCallback != null) context.OnInitialized += () => initializedCallback(context);
            if (initialize) context.Initialize();
            return context;
        }

        public async Task<int> ExecuteInitializersAsync(
            GameContextType scope,
            GameContext context,
            ISet<Type> initializedServices,
            IInitializationProgressNotifier progressNotifier,
            long generation,
            CancellationToken cancellationToken,
            Type? scopeKey,
            Action<long, CancellationToken> throwIfStale)
        {
            var plan = await CreateStartupPlan(scope, context, scopeKey, cancellationToken).ConfigureAwait(false);
            var totalServices = plan.TotalServiceCount;
            progressNotifier.OnScopeStarted(scope, totalServices);
            if (totalServices == 0) return totalServices;

            var completedServices = 0;

            if (plan.EntryPoints != null)
            {
                throwIfStale(generation, cancellationToken);
                progressNotifier.OnServiceStarted(scope, plan.EntryPoints.ProgressServiceType, completedServices, totalServices);
                await ExecuteVContainerEntryPointInitializablesAsync(plan.EntryPoints, cancellationToken).ConfigureAwait(false);
                throwIfStale(generation, cancellationToken);
                foreach (var marker in plan.EntryPoints.CompletedDependencyMarkers) initializedServices.Add(marker);
                completedServices++;
                progressNotifier.OnServiceCompleted(scope, plan.EntryPoints.ProgressServiceType, completedServices, totalServices);
            }

            if (plan.GlobalBootstrapOperations.Count > 0)
            {
                completedServices = await ExecuteGlobalBootstrapOperationsAsync(scope, context, plan.GlobalBootstrapOperations, progressNotifier, completedServices, totalServices, generation, cancellationToken, throwIfStale).ConfigureAwait(false);
            }

            if (plan.AsyncInitializers.Count == 0)
            {
                await StartVContainerStartablesAsync(plan.EntryPoints, cancellationToken).ConfigureAwait(false);
                return totalServices;
            }

            var pending = plan.AsyncInitializers.ToDictionary(x => x.ServiceType);
            ValidateDependencies(scope, context, pending, initializedServices);

            // Unified planning: the topological layering comes from LoadGraphTopology —
            // the same planner DescribeStartupPlan uses — so inspection and execution agree.
            var graphNodes = new List<LoadGraphNode>(pending.Count);
            foreach (var kv in pending)
                graphNodes.Add(new LoadGraphNode(kv.Key, kv.Key.Name, LoadGraphNodeKind.Initializer, kv.Value.Dependencies, LoadNodeWeights.Resolve(kv.Value.ImplementationType)));
            var layers = LoadGraphTopology.BuildLayers(graphNodes);

            var weighted = progressNotifier as IWeightedInitializationProgressNotifier;
            double totalWeight = 0;
            foreach (var layer in layers)
                foreach (var node in layer)
                    totalWeight += node.Weight;
            weighted?.OnScopeStarted(scope, totalWeight, totalServices);

            var completedWeightAccumulator = 0d;

            foreach (var layer in layers)
            {
                throwIfStale(generation, cancellationToken);
                var ready = new List<ServiceInitializerBinding>(layer.Count);
                foreach (var node in layer)
                    if (pending.TryGetValue(node.Key, out var binding))
                        ready.Add(binding);

                foreach (var init in ready)
                {
                    progressNotifier.OnServiceStarted(scope, init.ServiceType, completedServices, totalServices);
                    if (weighted != null)
                    {
                        var weight = LoadNodeWeights.Resolve(init.ImplementationType);
                        weighted.OnServiceStarted(scope, init.ServiceType, completedWeightAccumulator, totalWeight);
                    }
                }

                var unique = DedupeByImplementationType(ready);
                await RunWaveAsync(scope, context, unique, progressNotifier, completedServices, totalServices, cancellationToken).ConfigureAwait(false);

                foreach (var init in ready) context.RecordInitialized(init);
                throwIfStale(generation, cancellationToken);
                foreach (var init in ready)
                {
                    pending.Remove(init.ServiceType);
                    initializedServices.Add(init.ServiceType);
                    completedServices++;
                    var weight = LoadNodeWeights.Resolve(init.ImplementationType);
                    completedWeightAccumulator += weight;
                    progressNotifier.OnServiceCompleted(scope, init.ServiceType, completedServices, totalServices);
                    weighted?.OnServiceCompleted(scope, init.ServiceType, completedWeightAccumulator, totalWeight);
                }
            }

            await StartVContainerStartablesAsync(plan.EntryPoints, cancellationToken).ConfigureAwait(false);
            return totalServices;
        }

        private static List<ServiceInitializerBinding> DedupeByImplementationType(List<ServiceInitializerBinding> ready)
        {
            var unique = new List<ServiceInitializerBinding>(ready.Count);
            var seen = new HashSet<Type>();
            foreach (var init in ready)
                if (seen.Add(init.ImplementationType)) unique.Add(init);
            return unique;
        }

        private async Task<(Task task, ServiceInitializerBinding initializer)[]> RunWaveAsync(
            GameContextType scope,
            GameContext context,
            IReadOnlyList<ServiceInitializerBinding> unique,
            IInitializationProgressNotifier progressNotifier,
            int completedServices,
            int totalServices,
            CancellationToken cancellationToken)
        {
            var taskMap = new (Task task, ServiceInitializerBinding initializer)[unique.Count];
            var tasks = new Task[unique.Count];
            for (var i = 0; i < unique.Count; i++)
            {
                var init = unique[i];
                var task = ExecuteInitializerWithHealthAsync(scope, context, init, progressNotifier, completedServices, totalServices, cancellationToken);
                taskMap[i] = (task, init);
                tasks[i] = task;
            }

            var waveTask = Task.WhenAll(tasks);
            var stall = _health.Options.WaveStallTimeout;
            try
            {
                if (_health.IsEnabled && stall > TimeSpan.Zero && stall != Timeout.InfiniteTimeSpan)
                {
                    var first = await Task.WhenAny(waveTask, Task.Delay(stall, cancellationToken)).ConfigureAwait(false);
                    if (first != waveTask)
                        await AwaitStalledWaveAsync(scope, waveTask, taskMap, stall).ConfigureAwait(false);
                }
                else await waveTask.ConfigureAwait(false);
            }
            catch
            {
                foreach (var entry in taskMap)
                    if (entry.task.Status == TaskStatus.RanToCompletion)
                        context.RecordInitialized(entry.initializer);
                throw;
            }
            return taskMap;
        }

        private async Task AwaitStalledWaveAsync(
            GameContextType scope,
            Task waveTask,
            (Task task, ServiceInitializerBinding initializer)[] taskMap,
            TimeSpan stall)
        {
            var stalled = new List<string>();
            foreach (var entry in taskMap)
                if (!entry.task.IsCompleted) stalled.Add(entry.initializer.ServiceType.Name);
            if (stalled.Count > 0)
                _logger.LogWarning("[RuntimeFlow] Wave stall detected in scope {Scope}: {Count} service(s) haven't completed after {Timeout:F0}s: {Services}", scope, stalled.Count, stall.TotalSeconds, string.Join(", ", stalled));
            await waveTask.ConfigureAwait(false);
        }

        private async Task<ScopeStartupPlan> CreateStartupPlan(GameContextType scope, GameContext context, Type? scopeKey, CancellationToken cancellationToken)
        {
            var initializers = InitializationGraphResolver.DiscoverInitializers(context);
            var lazy = initializers.Where(b => typeof(ILazyInitializableService).IsAssignableFrom(b.ImplementationType)).ToList();
            foreach (var l in lazy) { initializers.Remove(l); _lazyRegistry.RegisterLazyBinding(l, context, scope, scopeKey); }
            var globalOps = scope == GameContextType.Global ? DiscoverGlobalOps(context) : Array.Empty<GlobalBootstrapOperationBinding>();
            var entryPoints = await TryCreateEntryPointsPlanAsync(scope, context, cancellationToken).ConfigureAwait(false);
            return new ScopeStartupPlan(entryPoints, globalOps, initializers.ToArray());
        }

        private static IReadOnlyList<GlobalBootstrapOperationBinding> DiscoverGlobalOps(GameContext context)
            => context.GetRegistrationsForServiceType(typeof(IGlobalBootstrapOperation)).Where(r => typeof(IGlobalBootstrapOperation).IsAssignableFrom(r.ImplementationType)).GroupBy(r => r.ImplementationType).Select(g => new GlobalBootstrapOperationBinding(g.Key, g.First())).ToArray();

        private async Task<VContainerEntryPointsStartupPlan?> TryCreateEntryPointsPlanAsync(GameContextType scope, GameContext context, CancellationToken cancellationToken)
        {
            var regs = context.GetRegistrationsForServiceType(typeof(RuntimeFlowVContainerEntryPointsSettings));
            if (regs.Count == 0) return null;
            var resolvedSettings = new RuntimeFlowVContainerEntryPointsSettings[regs.Count];
            for (var i = 0; i < regs.Count; i++)
                resolvedSettings[i] = (RuntimeFlowVContainerEntryPointsSettings)await context.ResolveAsync(regs[i], cancellationToken).ConfigureAwait(false);
            var settings = await MergeSettingsAsync(resolvedSettings, context, cancellationToken).ConfigureAwait(false);
            var resolver = context.Resolver;
            var entryResolver = RuntimeFlowVContainerEntryPointPhaseRunner.ResolveEntryPointResolver(scope, resolver);
            return new VContainerEntryPointsStartupPlan(scope, scope.ToString().ToLowerInvariant(), resolver, entryResolver, settings,
                RuntimeFlowVContainerEntryPointPhaseRunner.GetScopeLocalRegistrations<VContainer.Unity.IInitializable>(entryResolver, settings),
                RuntimeFlowVContainerEntryPointPhaseRunner.GetScopeLocalRegistrations<VContainer.Unity.IStartable>(entryResolver, settings),
                scope == GameContextType.Session ? new[] { typeof(RuntimeFlowVContainerEntryPointsStartupPhase), typeof(IRuntimeFlowSessionSyncEntryPointsBootstrapService) } : new[] { typeof(RuntimeFlowVContainerEntryPointsStartupPhase) },
                scope == GameContextType.Session);
        }

        private static async Task<RuntimeFlowVContainerEntryPointsSettings> MergeSettingsAsync(IReadOnlyList<RuntimeFlowVContainerEntryPointsSettings> settings, GameContext context, CancellationToken cancellationToken)
        {
            var contributions = await ResolveContributionsAsync(context, cancellationToken).ConfigureAwait(false);
            if (settings.Count == 0 && contributions.Length == 0) return RuntimeFlowVContainerEntryPointsSettings.Default;
            if (settings.Count == 1 && contributions.Length == 0) return settings[0];
            var exclInit = settings.SelectMany(s => s.ExcludedInitializableImplementationTypes).Concat(contributions.SelectMany(c => c.ExcludedInitializableImplementationTypes)).Distinct().ToArray();
            var exclStart = settings.SelectMany(s => s.ExcludedStartableImplementationTypes).Concat(contributions.SelectMany(c => c.ExcludedStartableImplementationTypes)).Distinct().ToArray();
            var priInit = settings.SelectMany(s => s.PrioritizedInitializableImplementationTypes).Distinct().ToArray();
            var after = settings.Select(s => s.AfterPrioritizedInitializablesInitialized).Where(c => c != null).ToArray();
            return new RuntimeFlowVContainerEntryPointsSettings(exclInit, exclStart, priInit, after.Length == 0 ? null : resolver => { foreach (var cb in after) cb!(resolver); });
        }

        private static async Task<RuntimeFlowVContainerEntryPointsSettingsContribution[]> ResolveContributionsAsync(GameContext context, CancellationToken cancellationToken)
        {
            var list = new List<RuntimeFlowVContainerEntryPointsSettingsContribution>();
            var cur = context;
            while (cur != null)
            {
                foreach (var r in cur.GetRegistrationsForServiceType(typeof(RuntimeFlowVContainerEntryPointsSettingsContribution)))
                    if (await cur.ResolveAsync(r, cancellationToken).ConfigureAwait(false) is RuntimeFlowVContainerEntryPointsSettingsContribution c) list.Add(c);
                cur = cur.Parent as GameContext;
            }
            return list.Distinct().ToArray();
        }

        private Task ExecuteVContainerEntryPointInitializablesAsync(VContainerEntryPointsStartupPlan entryPoints, CancellationToken ct)
            => _scheduler.ExecuteAsync(InitializationThreadAffinity.MainThread, token => RuntimeFlowVContainerEntryPointPhaseRunner.InitializeInitializablesAsync(entryPoints, _logger, token), ct);

        private Task StartVContainerStartablesAsync(VContainerEntryPointsStartupPlan? entryPoints, CancellationToken ct)
        {
            if (entryPoints == null) return Task.CompletedTask;
            return _scheduler.ExecuteAsync(InitializationThreadAffinity.MainThread, token => RuntimeFlowVContainerEntryPointPhaseRunner.StartStartablesAsync(entryPoints, _logger, token), ct);
        }

        private async Task<int> ExecuteGlobalBootstrapOperationsAsync(GameContextType scope, GameContext context, IReadOnlyList<GlobalBootstrapOperationBinding> bindings, IInitializationProgressNotifier notifier, int completed, int total, long generation, CancellationToken ct, Action<long, CancellationToken> throwIfStale)
        {
            var ops = new (GlobalBootstrapOperationBinding binding, IGlobalBootstrapOperation operation)[bindings.Count];
            for (var i = 0; i < bindings.Count; i++)
            {
                var b = bindings[i];
                var operation = (IGlobalBootstrapOperation)await context.ResolveAsync(b.Registration, ct).ConfigureAwait(false);
                ops[i] = (b, operation);
            }
            Array.Sort(ops, (x, y) =>
            {
                var byOrder = x.operation.Order.CompareTo(y.operation.Order);
                if (byOrder != 0) return byOrder;
                var byName = string.CompareOrdinal(NormalizeName(x.operation.Name, x.binding.ImplementationType), NormalizeName(y.operation.Name, y.binding.ImplementationType));
                if (byName != 0) return byName;
                return string.CompareOrdinal(x.binding.ImplementationType.FullName ?? x.binding.ImplementationType.Name, y.binding.ImplementationType.FullName ?? y.binding.ImplementationType.Name);
            });
            for (var i = 0; i < ops.Length; i++)
            {
                throwIfStale(generation, ct);
                var (binding, op) = ops[i];
                var name = NormalizeName(op.Name, binding.ImplementationType);
                var opCtx = new StartupOperationContext(scope, RuntimeStartupOperationPhases.GlobalBootstrapOperations, name, i, ops.Length, binding.ImplementationType, notifier, completed, total);
                notifier.OnServiceStarted(scope, binding.ImplementationType, completed, total);
                opCtx.NotifyStarted();
                try { await ExecuteStartupOperationWithHealthAsync(scope, binding.ImplementationType, op, opCtx, ct).ConfigureAwait(false); }
                catch (OperationCanceledException ex) when (ct.IsCancellationRequested) { var c = ex as RuntimeStartupOperationCanceledException ?? new RuntimeStartupOperationCanceledException(scope, RuntimeStartupOperationPhases.GlobalBootstrapOperations, name, opCtx.LastStep, opCtx.LastDetail, ex); opCtx.NotifyFailed(c); throw c; }
                catch (Exception ex) { var e = ex as RuntimeStartupOperationException ?? new RuntimeStartupOperationException(scope, RuntimeStartupOperationPhases.GlobalBootstrapOperations, name, opCtx.LastStep, opCtx.LastDetail, ex); opCtx.NotifyFailed(e); _logger.LogError(e, "Global bootstrap operation failed. phase={Phase}, operation={Operation}, step={Step}, detail={Detail}", RuntimeStartupOperationPhases.GlobalBootstrapOperations, name, opCtx.LastStep ?? "<none>", opCtx.LastDetail ?? "<none>"); throw e; }
                throwIfStale(generation, ct);
                completed++; opCtx.NotifyCompleted(completed); notifier.OnServiceCompleted(scope, binding.ImplementationType, completed, total);
            }
            return completed;
        }

        private async Task ExecuteStartupOperationWithHealthAsync(GameContextType scope, Type opType, IGlobalBootstrapOperation op, StartupOperationContext ctx, CancellationToken ct)
        {
            var affinity = op is IInitializationThreadAffinityProvider p ? p.ThreadAffinity : InitializationThreadAffinity.MainThread;
            var timeout = _health.GetServiceTimeout(scope, opType);
            var sw = Stopwatch.StartNew();
            using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_health.IsEnabled && timeout != Timeout.InfiniteTimeSpan) opCts.CancelAfter(timeout);
            try
            {
                await _scheduler.ExecuteAsync(affinity, async token => await op.ExecuteAsync(ctx, token).ConfigureAwait(false), opCts.Token).ConfigureAwait(false);
                sw.Stop(); _health.RecordServiceSuccess(scope, opType, sw.Elapsed, timeout);
            }
            catch (OperationCanceledException ex) when (_health.IsEnabled && opCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                sw.Stop(); var crit = new RuntimeHealthCriticalException(scope, opType, timeout, sw.Elapsed, ex); _health.RecordServiceFailure(scope, opType, sw.Elapsed, timeout, crit); throw new RuntimeStartupOperationException(scope, ctx.Phase, ctx.OperationName, ctx.LastStep, ctx.LastDetail, crit);
            }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested) { sw.Stop(); throw new RuntimeStartupOperationCanceledException(scope, ctx.Phase, ctx.OperationName, ctx.LastStep, ctx.LastDetail, ex); }
            catch (RuntimeStartupOperationException) { sw.Stop(); throw; }
            catch (Exception ex) { sw.Stop(); _health.RecordServiceFailure(scope, opType, sw.Elapsed, timeout, ex); throw; }
        }

        private static string NormalizeName(string? name, Type t) => string.IsNullOrWhiteSpace(name) ? t.Name : name.Trim();

        private async Task ExecuteInitializerWithHealthAsync(GameContextType scope, GameContext context, ServiceInitializerBinding init, IInitializationProgressNotifier notifier, int completed, int total, CancellationToken ct)
        {
            // Wave tasks continue on worker threads after the first await, so instance
            // construction goes through the async dispatch path.
            var resolved = await context.ResolveAsync(init, ct).ConfigureAwait(false);
            if (resolved is not IAsyncInitializableService svc) throw new InvalidOperationException($"Service {init.ServiceType.Name} is expected to implement {nameof(IAsyncInitializableService)}.");
            var affinity = resolved is IInitializationThreadAffinityProvider p ? p.ThreadAffinity : InitializationThreadAffinity.MainThread;
            var timeout = _health.GetServiceTimeout(scope, init.ServiceType);
            var sw = Stopwatch.StartNew();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_health.IsEnabled && timeout != Timeout.InfiniteTimeSpan) cts.CancelAfter(timeout);
            try
            {
                if (resolved is IProgressAwareInitializableService pa)
                {
                    var ic = new ServiceInitializationContext(scope, init.ServiceType, notifier, completed, total);
                    await _scheduler.ExecuteAsync(affinity, async token => await pa.InitializeAsync(ic, token).ConfigureAwait(false), cts.Token).ConfigureAwait(false);
                }
                else await _scheduler.ExecuteAsync(affinity, async token => await svc.InitializeAsync(token).ConfigureAwait(false), cts.Token).ConfigureAwait(false);
                sw.Stop(); _logger.LogDebug("Service {ServiceType} initialized ({Duration}ms)", init.ServiceType.Name, sw.Elapsed.TotalMilliseconds.ToString("F1")); _health.RecordServiceSuccess(scope, init.ServiceType, sw.Elapsed, timeout);
            }
            catch (OperationCanceledException ex) when (_health.IsEnabled && cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                sw.Stop(); _logger.LogWarning("Service {ServiceType} init slow ({Duration}ms, baseline {Baseline}ms)", init.ServiceType.Name, sw.Elapsed.TotalMilliseconds.ToString("F1"), timeout.TotalMilliseconds.ToString("F1"));
                var crit = new RuntimeHealthCriticalException(scope, init.ServiceType, timeout, sw.Elapsed, ex); _health.RecordServiceFailure(scope, init.ServiceType, sw.Elapsed, timeout, crit); throw crit;
            }
            catch (Exception ex) { sw.Stop(); _logger.LogError(ex, "Service {ServiceType} initialization failed", init.ServiceType.Name); _health.RecordServiceFailure(scope, init.ServiceType, sw.Elapsed, timeout, ex); throw; }
        }

        private void ValidateDependencies(GameContextType scope, GameContext context, IReadOnlyDictionary<Type, ServiceInitializerBinding> pending, ISet<Type> init)
        {
            foreach (var kv in pending.Values)
                foreach (var dep in kv.Dependencies)
                    if (!pending.ContainsKey(dep) && !init.Contains(dep) && !IsDependencyAvailableInParent(context.Parent, dep))
                        throw new InvalidOperationException($"Initializer for {kv.ServiceType.Name} depends on {dep.Name}, but this dependency was not initialized before scope {scope}.");
        }

        private static bool IsDependencyAvailableInParent(IGameContext? parent, Type dep)
        {
            if (parent == null) return false;
            if (parent.IsRegistered(dep)) return true;
            if (parent is GameContext gc) return IsDependencyAvailableInParent(gc.Parent, dep);
            return false;
        }

        private sealed class StartupOperationContext : IStartupOperationContext
        {
            private readonly Type _opType; private readonly IInitializationProgressNotifier _notifier; private readonly int _completed; private readonly int _total; private readonly Stopwatch _sw = Stopwatch.StartNew();
            public StartupOperationContext(GameContextType scope, string phase, string name, int idx, int total, Type opType, IInitializationProgressNotifier notifier, int completed, int totalSvcs) { Scope = scope; Phase = phase; OperationName = name; OperationIndex = idx; TotalOperations = total; _opType = opType; _notifier = notifier; _completed = completed; _total = totalSvcs; }
            public GameContextType Scope { get; } public string Phase { get; } public string OperationName { get; } public int OperationIndex { get; } public int TotalOperations { get; } public string? LastStep { get; private set; } public string? LastDetail { get; private set; }
            public void ReportStep(string step, string? detail = null) { LastStep = string.IsNullOrWhiteSpace(step) ? "<unknown>" : step.Trim(); LastDetail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim(); var msg = $"phase={Phase} operation={OperationName} step={LastStep}"; if (!string.IsNullOrWhiteSpace(LastDetail)) msg += $" detail={LastDetail.Trim()}"; _notifier.OnServiceProgress(Scope, _opType, 0f, msg, _completed, _total); if (_notifier is IStartupOperationProgressNotifier s) s.OnStartupOperationStep(Scope, Phase, OperationName, LastStep, LastDetail, _completed, _total, _sw.Elapsed); }
            public void NotifyStarted() { if (_notifier is IStartupOperationProgressNotifier s) s.OnStartupOperationStarted(Scope, Phase, OperationName, _completed, _total, _sw.Elapsed); }
            public void NotifyCompleted(int c) { _sw.Stop(); if (_notifier is IStartupOperationProgressNotifier s) s.OnStartupOperationCompleted(Scope, Phase, OperationName, c, _total, _sw.Elapsed); }
            public void NotifyFailed(Exception ex) { _sw.Stop(); if (_notifier is IStartupOperationProgressNotifier s) s.OnStartupOperationFailed(Scope, Phase, OperationName, LastStep, LastDetail, ex, _completed, _total, _sw.Elapsed); }
        }
    }
}
