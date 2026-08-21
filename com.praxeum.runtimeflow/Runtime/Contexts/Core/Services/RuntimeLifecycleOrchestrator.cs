using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RuntimeFlow.Events;
using VContainer;

namespace RuntimeFlow.Contexts
{
    internal sealed class RuntimeLifecycleOrchestrator
    {
        private readonly ActiveScopeState _state;
        private readonly GameContextScopeProfileStore _profiles;
        private readonly GameContextScopeRegistry _registry;
        private readonly GameContextLazyInitializationRegistry _lazy;
        private readonly IInitializationExecutionScheduler _scheduler;
        private readonly ILogger _logger;
        private readonly GenerationGate _generationGate;
        private readonly ScopeInitializationService _initService;
        private readonly ScopeDisposalService _disposalService;

        public RuntimeLifecycleOrchestrator(
            ActiveScopeState state,
            GameContextScopeProfileStore profiles,
            GameContextScopeRegistry registry,
            GameContextLazyInitializationRegistry lazy,
            IInitializationExecutionScheduler scheduler,
            ILogger logger,
            GenerationGate generationGate,
            ScopeInitializationService initService,
            ScopeDisposalService disposalService)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _lazy = lazy ?? throw new ArgumentNullException(nameof(lazy));
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _generationGate = generationGate ?? throw new ArgumentNullException(nameof(generationGate));
            _initService = initService ?? throw new ArgumentNullException(nameof(initService));
            _disposalService = disposalService ?? throw new ArgumentNullException(nameof(disposalService));
        }

        public async Task BuildAsyncCore(long generation, IInitializationProgressNotifier progressNotifier, CancellationToken cancellationToken)
        {
            ValidateExternalGlobalConfiguration();
            ResetBookkeeping();

            await _disposalService.DisposeAdditiveModulesAsync(cancellationToken).ConfigureAwait(false);
            await _disposalService.DisposePreloadedContextsAsync(cancellationToken).ConfigureAwait(false);

            if (_state.ModuleContext != null)
            {
                await _disposalService.DisposeScopeContextAsync(GameContextType.Module, _state.ModuleContext, cancellationToken, _state.ActiveModuleScopeKey, () => _registry.SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Disposed, _state.ActiveModuleScopeKey)).ConfigureAwait(false);
                _state.ModuleContext = null;
            }
            if (_state.SceneContext != null)
            {
                await _disposalService.DisposeScopeContextAsync(GameContextType.Scene, _state.SceneContext, cancellationToken, _state.ActiveSceneScopeKey, () => _registry.SetScopeStateIfTracked(GameContextType.Scene, ScopeLifecycleState.Disposed, _state.ActiveSceneScopeKey)).ConfigureAwait(false);
                _state.SceneContext = null;
            }
            if (_state.SessionContext != null)
            {
                await _disposalService.DisposeScopeContextAsync(GameContextType.Session, _state.SessionContext, cancellationToken, null, () => _registry.SetScopeStateIfTracked(GameContextType.Session, ScopeLifecycleState.Disposed)).ConfigureAwait(false);
                _state.SessionContext = null;
            }
            if (_state.OwnsGlobalContext)
            {
                await _disposalService.DisposeScopeContextAsync(GameContextType.Global, (GameContext)_state.GlobalContext!, cancellationToken, null, () => _registry.SetScopeStateIfTracked(GameContextType.Global, ScopeLifecycleState.Disposed)).ConfigureAwait(false);
                _state.GlobalContext = null;
            }
            DisposeAndClearEventBuses(_state.OwnsGlobalContext);
            _state.ActiveSceneScopeKey = null;
            _state.ActiveModuleScopeKey = null;

            var (initializedServices, availableServices) = _initService.CreateSeededState();
            _logger.LogInformation("BuildAsync started — initializing scopes");

            IGameContext? globalContext = null;
            GameContext? sessionContext = null;

            try
            {
                _generationGate.ThrowIfStaleGeneration(generation, cancellationToken);
                if (_state.OwnsGlobalContext)
                {
                    _registry.SetScopeStateIfTracked(GameContextType.Global, ScopeLifecycleState.Loading);
                    _state.GlobalEventBus = new ScopeEventBus();
                    globalContext = CreateContext(null, _profiles.GlobalRegistrations, Array.Empty<ServiceDescriptor>(), _state.OnGlobalInitialized, true, availableServices, _state.GlobalEventBus, _scheduler);
                    var total = await _initService.ExecuteInitializersAsync(GameContextType.Global, (GameContext)globalContext, initializedServices, progressNotifier, generation, cancellationToken, null, _generationGate.ThrowIfStaleGeneration).ConfigureAwait(false);
                    progressNotifier.OnScopeCompleted(GameContextType.Global, total);
                    _registry.SetScopeStateIfTracked(GameContextType.Global, ScopeLifecycleState.Active);
                    _state.GlobalContext = globalContext;
                    globalContext = null;
                }
                else
                {
                    globalContext = _state.GlobalContext ?? throw new InvalidOperationException("External global context is not configured.");
                    _registry.SetScopeStateIfTracked(GameContextType.Global, ScopeLifecycleState.Active);
                }

                _generationGate.ThrowIfStaleGeneration(generation, cancellationToken);
                await _scheduler.ExecuteAsync(InitializationThreadAffinity.MainThread, token => progressNotifier.OnGlobalContextReadyForSessionInitializationAsync(token), cancellationToken).ConfigureAwait(false);
                _generationGate.ThrowIfStaleGeneration(generation, cancellationToken);
                _state.SessionEventBus = new ScopeEventBus(_state.GlobalEventBus);
                sessionContext = await _initService.CreateAndInitializeScopeContextAsync(GameContextType.Session, (globalContext ?? _state.GlobalContext)!, _profiles.SessionRegistrations, Array.Empty<ServiceDescriptor>(), _state.OnSessionInitialized, initializedServices, availableServices, progressNotifier, generation, cancellationToken, null, false, _state.SessionEventBus,
                    (s, st, k) => _registry.SetScopeStateIfTracked(s, st, k), _generationGate.ThrowIfStaleGeneration,
                    (scope, ctx, ct, key, onDisposed) => _disposalService.DisposeScopeContextAsync(scope, ctx, ct, key, onDisposed),
                    (ct, ops) => CaptureCleanupFailuresAsync(ct, ops), CreateCleanupAggregate, CreateFailureCleanupToken, IsStaleCancellation).ConfigureAwait(false);

                _generationGate.ThrowIfStaleGeneration(generation, cancellationToken);
                _state.SessionContext = sessionContext;
            }
            catch (Exception ex)
            {
                var ct2 = CreateFailureCleanupToken();
                var failures = await CaptureCleanupFailuresAsync(ct2,
                    async () => { await _disposalService.DisposeScopeContextAsync(GameContextType.Session, sessionContext, ct2).ConfigureAwait(false); sessionContext = null; },
                    async () =>
                    {
                        if (!_state.OwnsGlobalContext) return;
                        _registry.SetScopeStateIfTracked(GameContextType.Global, ScopeLifecycleState.Failed);
                        if (globalContext is GameContext g) await _disposalService.DisposeScopeContextAsync(GameContextType.Global, g, ct2).ConfigureAwait(false);
                        else if (globalContext != null) await DisposeExternalGlobalAsync(globalContext, ct2).ConfigureAwait(false);
                        globalContext = null;
                    }).ConfigureAwait(false);
                if (failures.Count > 0) throw CreateCleanupAggregate("BuildAsync", ex, failures);
                throw;
            }
        }

        public async Task RestartSessionAsyncCore(long generation, IInitializationProgressNotifier progressNotifier, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Session restarting");
            ResetBookkeeping();
            if (_state.GlobalContext != null) _registry.SetScopeStateIfTracked(GameContextType.Global, ScopeLifecycleState.Active);

            await _disposalService.DisposeAdditiveModulesAsync(cancellationToken).ConfigureAwait(false);
            await _disposalService.DisposePreloadedContextsAsync(cancellationToken).ConfigureAwait(false);

            if (_state.ModuleContext != null)
            {
                var ctx = _state.ModuleContext;
                var key = _state.ActiveModuleScopeKey;
                try { await _disposalService.DisposeScopeContextAsync(GameContextType.Module, ctx, cancellationToken, key, () => _registry.SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Disposed, key)).ConfigureAwait(false); }
                finally { _state.ModuleContext = null; }
            }
            if (_state.SceneContext != null)
            {
                var ctx = _state.SceneContext;
                var key = _state.ActiveSceneScopeKey;
                try { await _disposalService.DisposeScopeContextAsync(GameContextType.Scene, ctx, cancellationToken, key, () => _registry.SetScopeStateIfTracked(GameContextType.Scene, ScopeLifecycleState.Disposed, key)).ConfigureAwait(false); }
                finally { _state.SceneContext = null; }
            }
            if (_state.SessionContext != null)
            {
                var ctx = _state.SessionContext;
                try { await _disposalService.DisposeScopeContextAsync(GameContextType.Session, ctx, cancellationToken, null, () => _registry.SetScopeStateIfTracked(GameContextType.Session, ScopeLifecycleState.Disposed)).ConfigureAwait(false); }
                finally { _state.SessionContext = null; }
            }

            var (initializedServices, availableServices) = _initService.CreateSeededState(_state.GlobalContext);
            GameContext? sessionContext = null;
            GameContext? sceneContext = null;
            GameContext? moduleContext = null;

            try
            {
                DisposeAndClearEventBuses(false);
                await _scheduler.ExecuteAsync(InitializationThreadAffinity.MainThread, async token => { token.ThrowIfCancellationRequested(); await Task.Yield(); token.ThrowIfCancellationRequested(); await Task.Yield(); }, cancellationToken).ConfigureAwait(false);
                _generationGate.ThrowIfStaleGeneration(generation, cancellationToken);
                await _scheduler.ExecuteAsync(InitializationThreadAffinity.MainThread, token => progressNotifier.OnSessionRestartTeardownCompletedAsync(token), cancellationToken).ConfigureAwait(false);
                _generationGate.ThrowIfStaleGeneration(generation, cancellationToken);
                _state.SessionEventBus = new ScopeEventBus(_state.GlobalEventBus);
                sessionContext = await _initService.CreateAndInitializeScopeContextAsync(GameContextType.Session, _state.GlobalContext!, _profiles.SessionRegistrations, Array.Empty<ServiceDescriptor>(), _state.OnSessionInitialized, initializedServices, availableServices, progressNotifier, generation, cancellationToken, null, false, _state.SessionEventBus,
                    (s, st, k) => _registry.SetScopeStateIfTracked(s, st, k), _generationGate.ThrowIfStaleGeneration,
                    (scope, ctx, ct, key, onDisposed) => _disposalService.DisposeScopeContextAsync(scope, ctx, ct, key, onDisposed),
                    (ct, ops) => CaptureCleanupFailuresAsync(ct, ops), CreateCleanupAggregate, CreateFailureCleanupToken, IsStaleCancellation).ConfigureAwait(false);

                if (_state.ActiveSceneScopeKey != null && _profiles.TryGetSceneProfile(_state.ActiveSceneScopeKey, out var sceneProfile))
                {
                    _state.SceneEventBus = new ScopeEventBus(_state.SessionEventBus);
                    sceneContext = await _initService.CreateAndInitializeScopeContextAsync(GameContextType.Scene, sessionContext, sceneProfile.Registrations, sceneProfile.Services, _state.OnSceneInitialized, initializedServices, availableServices, progressNotifier, generation, cancellationToken, _state.ActiveSceneScopeKey, false, _state.SceneEventBus,
                        (s, st, k) => _registry.SetScopeStateIfTracked(s, st, k), _generationGate.ThrowIfStaleGeneration,
                        (scope, ctx, ct, key, onDisposed) => _disposalService.DisposeScopeContextAsync(scope, ctx, ct, key, onDisposed),
                        (ct, ops) => CaptureCleanupFailuresAsync(ct, ops), CreateCleanupAggregate, CreateFailureCleanupToken, IsStaleCancellation).ConfigureAwait(false);
                }
                if (_state.ActiveModuleScopeKey != null && sceneContext != null && _profiles.TryGetModuleProfile(_state.ActiveModuleScopeKey, out var moduleProfile))
                {
                    _state.ModuleEventBus = new ScopeEventBus(_state.SceneEventBus);
                    moduleContext = await _initService.CreateAndInitializeScopeContextAsync(GameContextType.Module, sceneContext, moduleProfile.Registrations, moduleProfile.Services, _state.OnModuleInitialized, initializedServices, availableServices, progressNotifier, generation, cancellationToken, _state.ActiveModuleScopeKey, false, _state.ModuleEventBus,
                        (s, st, k) => _registry.SetScopeStateIfTracked(s, st, k), _generationGate.ThrowIfStaleGeneration,
                        (scope, ctx, ct, key, onDisposed) => _disposalService.DisposeScopeContextAsync(scope, ctx, ct, key, onDisposed),
                        (ct, ops) => CaptureCleanupFailuresAsync(ct, ops), CreateCleanupAggregate, CreateFailureCleanupToken, IsStaleCancellation).ConfigureAwait(false);
                }

                _generationGate.ThrowIfStaleGeneration(generation, cancellationToken);
                _state.SessionContext = sessionContext;
                _state.SceneContext = sceneContext;
                _state.ModuleContext = moduleContext;
            }
            catch (Exception ex)
            {
                var ct2 = CreateFailureCleanupToken();
                var failures = await CaptureCleanupFailuresAsync(ct2,
                    async () => { await _disposalService.DisposeScopeContextAsync(GameContextType.Module, moduleContext, ct2, _state.ActiveModuleScopeKey).ConfigureAwait(false); moduleContext = null; },
                    async () => { await _disposalService.DisposeScopeContextAsync(GameContextType.Scene, sceneContext, ct2, _state.ActiveSceneScopeKey).ConfigureAwait(false); sceneContext = null; },
                    async () => { await _disposalService.DisposeScopeContextAsync(GameContextType.Session, sessionContext, ct2).ConfigureAwait(false); sessionContext = null; }).ConfigureAwait(false);
                if (failures.Count > 0) throw CreateCleanupAggregate("RestartSession", ex, failures);
                throw;
            }
        }

        private void ResetBookkeeping()
        {
            _lazy.Clear();
            _registry.ResetScopeStates();
        }

        private void ValidateExternalGlobalConfiguration()
        {
            if (_state.OwnsGlobalContext) return;
            if (_profiles.HasGlobalRegistrations)
                throw new InvalidOperationException("GBBR1001: Global registrations are not allowed when using an external global context bridge.");
        }

        private void DisposeAndClearEventBuses(bool includeGlobal)
        {
            _state.ModuleEventBus?.Dispose(); _state.ModuleEventBus = null;
            _state.SceneEventBus?.Dispose(); _state.SceneEventBus = null;
            _state.SessionEventBus?.Dispose(); _state.SessionEventBus = null;
            if (!includeGlobal) return;
            _state.GlobalEventBus?.Dispose(); _state.GlobalEventBus = null;
        }

        private static GameContext CreateContext(IGameContext? parent, IReadOnlyCollection<Action<IGameContext>> regs, IReadOnlyCollection<ServiceDescriptor> auto, Action<IGameContext>? cb, bool init, IDictionary<Type, object> avail, ScopeEventBus? bus, IInitializationExecutionScheduler scheduler)
        {
            var ctx = new GameContext(parent) { ExecutionScheduler = scheduler };
            foreach (var r in regs) r(ctx);
            if (bus != null) { ctx.RegisterInstance<IScopeEventBus>(bus); ctx.OnBeforeDispose += bus.Dispose; }
            InitializationGraphResolver.RegisterAutoServices(ctx, auto, avail);
            if (cb != null) ctx.OnInitialized += () => cb(ctx);
            if (init) ctx.Initialize();
            return ctx;
        }

        private Task DisposeExternalGlobalAsync(IGameContext ctx, CancellationToken ct)
            => _scheduler.ExecuteAsync(InitializationThreadAffinity.MainThread, _ => { ctx.Dispose(); return Task.CompletedTask; }, ct);

        private static bool IsStaleCancellation(Exception ex, CancellationToken ct) => ex is OperationCanceledException && !ct.IsCancellationRequested;
        private static CancellationToken CreateFailureCleanupToken() => CancellationToken.None;
        private static AggregateException CreateCleanupAggregate(string op, Exception ex, IReadOnlyCollection<Exception> failures)
        {
            var list = new List<Exception>(failures.Count + 1) { ex };
            foreach (var f in failures) if (f is AggregateException agg) list.AddRange(agg.Flatten().InnerExceptions); else list.Add(f);
            return new AggregateException($"{op} failed and cleanup encountered additional errors.", list);
        }
        private static async Task<List<Exception>> CaptureCleanupFailuresAsync(CancellationToken ct, params Func<Task>[] ops)
        {
            var failures = new List<Exception>();
            foreach (var op in ops)
            {
                if (op == null) continue;
                try { await op().ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (AggregateException agg) { var f = FilterCancellationFailures(agg, ct.IsCancellationRequested); if (f != null) failures.Add(f); }
                catch (Exception ex) { if (ct.IsCancellationRequested && IsCancellationFailure(ex)) continue; failures.Add(ex); }
            }
            return failures;
        }
        private static Exception? FilterCancellationFailures(Exception ex, bool requested)
        {
            if (!requested) return ex;
            if (ex is not AggregateException agg) return IsCancellationFailure(ex) ? null : ex;
            var non = agg.Flatten().InnerExceptions.Where(i => !IsCancellationFailure(i)).ToArray();
            return non.Length == 0 ? null : new AggregateException(non);
        }
        private static bool IsCancellationFailure(Exception ex)
        {
            if (ex is OperationCanceledException) return true;
            if (ex is AggregateException agg) { var f = agg.Flatten().InnerExceptions; return f.Count > 0 && f.All(IsCancellationFailure); }
            return false;
        }
    }
}
