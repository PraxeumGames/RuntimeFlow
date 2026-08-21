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
        private readonly ScopeOperationCoordinator _coordinator;
        private readonly ScopeInitializationService _initService;
        private readonly ScopeDisposalService _disposalService;
        private readonly ScopeLifecycleDependencies _deps;

        public RuntimeLifecycleOrchestrator(
            ActiveScopeState state,
            GameContextScopeProfileStore profiles,
            GameContextScopeRegistry registry,
            GameContextLazyInitializationRegistry lazy,
            IInitializationExecutionScheduler scheduler,
            ILogger logger,
            ScopeOperationCoordinator coordinator,
            ScopeInitializationService initService,
            ScopeDisposalService disposalService)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _lazy = lazy ?? throw new ArgumentNullException(nameof(lazy));
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _initService = initService ?? throw new ArgumentNullException(nameof(initService));
            _disposalService = disposalService ?? throw new ArgumentNullException(nameof(disposalService));
            _deps = new ScopeLifecycleDependencies
            {
                SetState = (scope, state, key) => _registry.SetScopeStateIfTracked(scope, state, key),
                ThrowIfStale = _coordinator.ThrowIfStaleGeneration,
                DisposeScope = (scope, ctx, ct, key, onDisposed) => _disposalService.DisposeScopeContextAsync(scope, ctx, ct, key, onDisposed),
                CaptureCleanup = ScopeCleanupFailures.CaptureCleanupFailuresAsync,
                CreateAggregate = ScopeCleanupFailures.CreateCleanupAggregate,
                FailureCleanupToken = CreateFailureCleanupToken,
                IsStaleCancellation = IsStaleCancellation,
            };
        }

        public async Task BuildAsyncCore(long generation, IInitializationProgressNotifier progressNotifier, CancellationToken cancellationToken)
        {
            ValidateExternalGlobalConfiguration();
            ResetBookkeeping();

            await _disposalService.DisposeAdditiveModulesAsync(cancellationToken).ConfigureAwait(false);
            await _disposalService.DisposePreloadedContextsAsync(cancellationToken).ConfigureAwait(false);

            if (_state.ModuleContext != null)
            {
                await _disposalService.DisposeActivatedScopeAsync(GameContextType.Module, _state.ModuleContext, _state.ActiveModuleScopeKey, ScopeLifecycleState.Deactivating, cancellationToken, () => _state.ModuleContext = null).ConfigureAwait(false);
            }
            if (_state.SceneContext != null)
            {
                await _disposalService.DisposeActivatedScopeAsync(GameContextType.Scene, _state.SceneContext, _state.ActiveSceneScopeKey, ScopeLifecycleState.Deactivating, cancellationToken, () => _state.SceneContext = null).ConfigureAwait(false);
            }
            if (_state.SessionContext != null)
            {
                await _disposalService.DisposeActivatedScopeAsync(GameContextType.Session, _state.SessionContext, null, ScopeLifecycleState.Deactivating, cancellationToken, () => _state.SessionContext = null).ConfigureAwait(false);
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
                _coordinator.ThrowIfStaleGeneration(generation, cancellationToken);
                if (_state.OwnsGlobalContext)
                {
                    _registry.SetScopeStateIfTracked(GameContextType.Global, ScopeLifecycleState.Loading);
                    _state.GlobalEventBus = new ScopeEventBus();
                    globalContext = CreateContext(null, _profiles.GlobalRegistrations, Array.Empty<ServiceDescriptor>(), _state.OnGlobalInitialized, true, availableServices, _state.GlobalEventBus, _scheduler);
                    var total = await _initService.ExecuteInitializersAsync(GameContextType.Global, (GameContext)globalContext, initializedServices, progressNotifier, generation, cancellationToken, null, _coordinator.ThrowIfStaleGeneration).ConfigureAwait(false);
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

                _coordinator.ThrowIfStaleGeneration(generation, cancellationToken);
                await _scheduler.ExecuteAsync(InitializationThreadAffinity.MainThread, token => progressNotifier.OnGlobalContextReadyForSessionInitializationAsync(token), cancellationToken).ConfigureAwait(false);
                _coordinator.ThrowIfStaleGeneration(generation, cancellationToken);
                _state.SessionEventBus = new ScopeEventBus(_state.GlobalEventBus);
                sessionContext = await _initService.CreateAndInitializeScopeContextAsync(GameContextType.Session, (globalContext ?? _state.GlobalContext)!, _profiles.SessionRegistrations, Array.Empty<ServiceDescriptor>(), _state.OnSessionInitialized, initializedServices, availableServices, progressNotifier, generation, cancellationToken, null, false, _state.SessionEventBus, _deps).ConfigureAwait(false);

                _coordinator.ThrowIfStaleGeneration(generation, cancellationToken);
                _state.SessionContext = sessionContext;
            }
            catch (Exception ex)
            {
                var ct2 = CreateFailureCleanupToken();
                var failures = await ScopeCleanupFailures.CaptureCleanupFailuresAsync(ct2,
                    async () => { await _disposalService.DisposeScopeContextAsync(GameContextType.Session, sessionContext, ct2).ConfigureAwait(false); sessionContext = null; },
                    async () =>
                    {
                        if (!_state.OwnsGlobalContext) return;
                        _registry.SetScopeStateIfTracked(GameContextType.Global, ScopeLifecycleState.Failed);
                        if (globalContext is GameContext g) await _disposalService.DisposeScopeContextAsync(GameContextType.Global, g, ct2).ConfigureAwait(false);
                        else if (globalContext != null) await DisposeExternalGlobalAsync(globalContext, ct2).ConfigureAwait(false);
                        globalContext = null;
                    }).ConfigureAwait(false);
                if (failures.Count > 0) throw ScopeCleanupFailures.CreateCleanupAggregate("BuildAsync", ex, failures);
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
                var key = _state.ActiveModuleScopeKey;
                try { await _disposalService.DisposeActivatedScopeAsync(GameContextType.Module, _state.ModuleContext, key, ScopeLifecycleState.Deactivating, cancellationToken).ConfigureAwait(false); }
                finally { _state.ModuleContext = null; }
            }
            if (_state.SceneContext != null)
            {
                var key = _state.ActiveSceneScopeKey;
                try { await _disposalService.DisposeActivatedScopeAsync(GameContextType.Scene, _state.SceneContext, key, ScopeLifecycleState.Deactivating, cancellationToken).ConfigureAwait(false); }
                finally { _state.SceneContext = null; }
            }
            if (_state.SessionContext != null)
            {
                try { await _disposalService.DisposeActivatedScopeAsync(GameContextType.Session, _state.SessionContext, null, ScopeLifecycleState.Deactivating, cancellationToken).ConfigureAwait(false); }
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
                _coordinator.ThrowIfStaleGeneration(generation, cancellationToken);
                await _scheduler.ExecuteAsync(InitializationThreadAffinity.MainThread, token => progressNotifier.OnSessionRestartTeardownCompletedAsync(token), cancellationToken).ConfigureAwait(false);
                _coordinator.ThrowIfStaleGeneration(generation, cancellationToken);
                _state.SessionEventBus = new ScopeEventBus(_state.GlobalEventBus);
                sessionContext = await _initService.CreateAndInitializeScopeContextAsync(GameContextType.Session, _state.GlobalContext!, _profiles.SessionRegistrations, Array.Empty<ServiceDescriptor>(), _state.OnSessionInitialized, initializedServices, availableServices, progressNotifier, generation, cancellationToken, null, false, _state.SessionEventBus, _deps).ConfigureAwait(false);

                if (_state.ActiveSceneScopeKey != null && _profiles.TryGetSceneProfile(_state.ActiveSceneScopeKey, out var sceneProfile))
                {
                    _state.SceneEventBus = new ScopeEventBus(_state.SessionEventBus);
                    sceneContext = await _initService.CreateAndInitializeScopeContextAsync(GameContextType.Scene, sessionContext, sceneProfile.Registrations, sceneProfile.Services, _state.OnSceneInitialized, initializedServices, availableServices, progressNotifier, generation, cancellationToken, _state.ActiveSceneScopeKey, false, _state.SceneEventBus, _deps).ConfigureAwait(false);
                }
                if (_state.ActiveModuleScopeKey != null && sceneContext != null && _profiles.TryGetModuleProfile(_state.ActiveModuleScopeKey, out var moduleProfile))
                {
                    _state.ModuleEventBus = new ScopeEventBus(_state.SceneEventBus);
                    moduleContext = await _initService.CreateAndInitializeScopeContextAsync(GameContextType.Module, sceneContext, moduleProfile.Registrations, moduleProfile.Services, _state.OnModuleInitialized, initializedServices, availableServices, progressNotifier, generation, cancellationToken, _state.ActiveModuleScopeKey, false, _state.ModuleEventBus, _deps).ConfigureAwait(false);
                }

                _coordinator.ThrowIfStaleGeneration(generation, cancellationToken);
                _state.SessionContext = sessionContext;
                _state.SceneContext = sceneContext;
                _state.ModuleContext = moduleContext;
            }
            catch (Exception ex)
            {
                var ct2 = CreateFailureCleanupToken();
                var failures = await ScopeCleanupFailures.CaptureCleanupFailuresAsync(ct2,
                    async () => { await _disposalService.DisposeScopeContextAsync(GameContextType.Module, moduleContext, ct2, _state.ActiveModuleScopeKey).ConfigureAwait(false); moduleContext = null; },
                    async () => { await _disposalService.DisposeScopeContextAsync(GameContextType.Scene, sceneContext, ct2, _state.ActiveSceneScopeKey).ConfigureAwait(false); sceneContext = null; },
                    async () => { await _disposalService.DisposeScopeContextAsync(GameContextType.Session, sessionContext, ct2).ConfigureAwait(false); sessionContext = null; }).ConfigureAwait(false);
                if (failures.Count > 0) throw ScopeCleanupFailures.CreateCleanupAggregate("RestartSession", ex, failures);
                throw;
            }
        }

        private void ResetBookkeeping()
        {
            _lazy.Clear();
            _registry.ResetScopeStates();
        }

        private const string ExternalGlobalContextErrorCode = "GBBR1001";

        private void ValidateExternalGlobalConfiguration()
        {
            if (_state.OwnsGlobalContext) return;
            if (_profiles.HasGlobalRegistrations)
                throw new InvalidOperationException($"{ExternalGlobalContextErrorCode}: Global registrations are not allowed when using an external global context bridge.");
        }

        private void DisposeAndClearEventBuses(bool includeGlobal)
            => ScopeCleanupFailures.DisposeAndClearEventBuses(_state, includeGlobal);

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
    }
}
