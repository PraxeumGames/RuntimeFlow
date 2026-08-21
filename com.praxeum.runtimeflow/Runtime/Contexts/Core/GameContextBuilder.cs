using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RuntimeFlow.Events;

namespace RuntimeFlow.Contexts
{
    public partial class GameContextBuilder : IGameContextBuilder
    {
        private readonly GameContextScopeProfileStore _scopeProfiles = new();
        private readonly GameContextScopeRegistry _scopeRegistry = new();
        private readonly GameContextDeferredRegistrationQueue _deferredRegistrations = new();
        private readonly IInitializationExecutionScheduler _executionScheduler;
        private readonly RuntimeHealthSupervisor _healthSupervisor;
        private readonly ILogger _logger;
        private readonly ScopeOperationCoordinator _coordinator;
        private readonly ScopeActivationService _activationService;
        private readonly ScopeTransitionEngine _scopeTransitions;
        private readonly ScopeTransitionService _scopeTransitionService;
        private readonly ScopeDisposalService _disposalService;
        private readonly ScopeLoadingService _loadingService;
        private readonly ScopeInitializationService _initService;
        private readonly RuntimeLifecycleOrchestrator _lifecycleOrchestrator;
        private readonly ActiveScopeState _activeState = new();
        private readonly GenerationGate _generationGate;

        private Dictionary<Type, GameContext> _preloadedContexts => _activeState.PreloadedContexts;
        private Dictionary<Type, GameContext> _additiveModuleContexts => _activeState.AdditiveModuleContexts;

        private Action<IGameContext>? _onGlobalInitialized { get => _activeState.OnGlobalInitialized; set => _activeState.OnGlobalInitialized = value; }
        private Action<IGameContext>? _onSessionInitialized { get => _activeState.OnSessionInitialized; set => _activeState.OnSessionInitialized = value; }
        private Action<IGameContext>? _onSceneInitialized { get => _activeState.OnSceneInitialized; set => _activeState.OnSceneInitialized = value; }
        private Action<IGameContext>? _onModuleInitialized { get => _activeState.OnModuleInitialized; set => _activeState.OnModuleInitialized = value; }

        private IGameContext? _globalContext { get => _activeState.GlobalContext; set => _activeState.GlobalContext = value; }
        private GameContext? _sessionContext { get => _activeState.SessionContext; set => _activeState.SessionContext = value; }
        private GameContext? _sceneContext { get => _activeState.SceneContext; set => _activeState.SceneContext = value; }
        private GameContext? _moduleContext { get => _activeState.ModuleContext; set => _activeState.ModuleContext = value; }
        private Type? _activeSceneScopeKey { get => _activeState.ActiveSceneScopeKey; set => _activeState.ActiveSceneScopeKey = value; }
        private Type? _activeModuleScopeKey { get => _activeState.ActiveModuleScopeKey; set => _activeState.ActiveModuleScopeKey = value; }

        internal IGameContext? GlobalContext => _activeState.GlobalContext;
        internal GameContext? SessionContext => _activeState.SessionContext;
        internal GameContext? SceneContext => _activeState.SceneContext;
        internal GameContext? ModuleContext => _activeState.ModuleContext;
        internal IReadOnlyDictionary<Type, GameContext> PreloadedContexts => _activeState.PreloadedContexts;
        internal IReadOnlyDictionary<Type, GameContext> AdditiveModuleContexts => _activeState.AdditiveModuleContexts;
        internal Type? ActiveSceneScopeKey => _activeState.ActiveSceneScopeKey;
        internal Type? ActiveModuleScopeKey => _activeState.ActiveModuleScopeKey;
        private bool _ownsGlobalContext { get => _activeState.OwnsGlobalContext; set => _activeState.OwnsGlobalContext = value; }

        private ScopeEventBus? _globalEventBus { get => _activeState.GlobalEventBus; set => _activeState.GlobalEventBus = value; }
        private ScopeEventBus? _sessionEventBus { get => _activeState.SessionEventBus; set => _activeState.SessionEventBus = value; }
        private ScopeEventBus? _sceneEventBus { get => _activeState.SceneEventBus; set => _activeState.SceneEventBus = value; }
        private ScopeEventBus? _moduleEventBus { get => _activeState.ModuleEventBus; set => _activeState.ModuleEventBus = value; }

        private readonly GameContextLazyInitializationRegistry _lazyInitialization = new();

        public GameContextBuilder(IInitializationExecutionScheduler? executionScheduler = null)
            : this(executionScheduler, null, null)
        {
        }

        internal GameContextBuilder(
            IInitializationExecutionScheduler? executionScheduler,
            RuntimeHealthSupervisor? healthSupervisor,
            ILogger? logger = null)
        {
            _executionScheduler = executionScheduler ?? InlineInitializationExecutionScheduler.Instance;
            _healthSupervisor = healthSupervisor ?? RuntimeHealthSupervisor.Disabled;
            _logger = logger ?? NullLogger.Instance;
            _coordinator = new ScopeOperationCoordinator();
            _generationGate = new GenerationGate(_coordinator);
            _activationService = new ScopeActivationService(_executionScheduler);
            _initService = new ScopeInitializationService(_activeState, _scopeRegistry, _lazyInitialization, _executionScheduler, _healthSupervisor, _logger, _activationService);
            _scopeTransitions = new ScopeTransitionEngine(this);
            _scopeTransitionService = new ScopeTransitionService(
                _activeState,
                _activationService,
                (scope, parent, regs, auto, cb, init, avail, notifier, gen, ct, key, skip, bus) => _initService.CreateAndInitializeScopeContextAsync(scope, parent, regs, auto, cb, init, avail, notifier, gen, ct, key, skip, bus, SetScopeStateIfTracked, ThrowIfStaleGeneration, DisposeScopeContextAsync, (t, ops2) => CaptureCleanupFailuresAsync(t, ops2), CreateCleanupAggregateException, CreateFailureCleanupCancellationToken, IsStaleGenerationCancellation),
                DisposeScopeContextAsync,
                (token, ops) => CaptureCleanupFailuresAsync(token, ops),
                CreateCleanupAggregateException,
                CreateFailureCleanupCancellationToken,
                FilterCancellationFailures,
                SetScopeStateIfTracked,
                PublishInCurrentGeneration,
                ThrowIfStaleGeneration);
            _disposalService = new ScopeDisposalService(_activeState, _scopeRegistry, _executionScheduler, _logger, _coordinator, _activationService);
            _loadingService = new ScopeLoadingService(_activeState, _scopeProfiles, _coordinator, _scopeTransitionService, _scopeTransitions);
            _lifecycleOrchestrator = new RuntimeLifecycleOrchestrator(_activeState, _scopeProfiles, _scopeRegistry, _lazyInitialization, _executionScheduler, _logger, _generationGate, _initService, _disposalService);
        }

        internal ActiveScopeState ActiveState => _activeState;

        internal GenerationGate GenerationGate => _generationGate;

        internal ScopeTransitionService ScopeTransitionService => _scopeTransitionService;

        internal ScopeDisposalService DisposalService => _disposalService;

        internal ScopeLoadingService LoadingService => _loadingService;

        internal ScopeInitializationService InitializationService => _initService;

        internal RuntimeLifecycleOrchestrator LifecycleOrchestrator => _lifecycleOrchestrator;

        internal Task ExecuteOnMainThreadAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            return _executionScheduler.ExecuteAsync(InitializationThreadAffinity.MainThread, operation, cancellationToken);
        }

        private Task ExecuteStageCallbackOnMainThreadAsync(Func<CancellationToken, Task> callback, CancellationToken cancellationToken)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            return _executionScheduler.ExecuteAsync(InitializationThreadAffinity.MainThread, callback, cancellationToken);
        }

        private Task DrainMainThreadFrameAsync(CancellationToken cancellationToken)
        {
            return _executionScheduler.ExecuteAsync(InitializationThreadAffinity.MainThread, async token =>
            {
                token.ThrowIfCancellationRequested();
                await Task.Yield();
                token.ThrowIfCancellationRequested();
                await Task.Yield();
            }, cancellationToken);
        }

        internal void UseExternalGlobalContext(IGameContext globalContext)
        {
            _globalContext = globalContext ?? throw new ArgumentNullException(nameof(globalContext));
            _ownsGlobalContext = false;
        }

        public IGameContextBuilder OnGlobalInitialized(Action<IGameContext> callback) { _onGlobalInitialized += callback; return this; }
        public IGameContextBuilder OnSessionInitialized(Action<IGameContext> callback) { _onSessionInitialized += callback; return this; }
        public IGameContextBuilder OnSceneInitialized(Action<IGameContext> callback) { _onSceneInitialized += callback; return this; }
        public IGameContextBuilder OnModuleInitialized(Action<IGameContext> callback) { _onModuleInitialized += callback; return this; }

        internal void SetScopeStateIfTracked(GameContextType scope, ScopeLifecycleState state, Type? explicitScopeKey = null)
            => _scopeRegistry.SetScopeStateIfTracked(scope, state, explicitScopeKey);

        internal ScopeLifecycleState GetScopeState(Type scopeType)
            => _scopeRegistry.GetScopeState(scopeType);

        public ScopeLifecycleState GetScopeLifecycleState(Type scopeType)
            => _scopeRegistry.GetScopeState(scopeType);

        public bool TryResolveScopeType(Type scopeType, out GameContextType scope)
            => _scopeRegistry.TryResolveScopeType(scopeType, out scope);

        public IGameContext GetSessionContext()
            => _sessionContext ?? throw new InvalidOperationException("Session scope is not initialized.");

        public bool CanRestartSession()
            => _sessionContext != null;

        public bool TryResolveFromSession<T>(out T service) where T : class
        {
            if (_sessionContext != null && _sessionContext.TryResolve(typeof(T), out var resolved) && resolved is T typed)
            {
                service = typed;
                return true;
            }
            service = null!;
            return false;
        }

        public bool TryResolveFromSession(Type serviceType, out object service)
        {
            if (_sessionContext != null && _sessionContext.TryResolve(serviceType, out service))
                return true;
            service = null!;
            return false;
        }

        public async Task EnsureLazyServiceInitializedAsync(Type serviceType, CancellationToken cancellationToken = default)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (_lazyInitialization.IsInitialized(serviceType))
                return;

            await _coordinator.LazyInitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_lazyInitialization.IsInitialized(serviceType))
                    return;

                if (!_lazyInitialization.TryGetBinding(serviceType, out var binding))
                    return;

                var instance = binding.Context.Resolve(binding.Initializer);
                if (instance is IAsyncInitializableService asyncService)
                {
                    await asyncService.InitializeAsync(cancellationToken).ConfigureAwait(false);
                }

                binding.Context.RecordInitialized(binding.Initializer);
                _lazyInitialization.MarkInitialized(serviceType);
            }
            finally
            {
                _coordinator.LazyInitLock.Release();
            }
        }

        private void ValidateSceneScopeOperationPreconditions(Type sceneScopeKey)
        {
            if (sceneScopeKey == null) throw new ArgumentNullException(nameof(sceneScopeKey));
            if (!_scopeRegistry.TryResolveScopeType(sceneScopeKey, out _))
                throw new InvalidOperationException($"Scene scope '{sceneScopeKey.Name}' is not declared.");
            if (_sessionContext == null)
                throw new InvalidOperationException("Session context is not initialized. Call LoadSessionAsync first.");
        }

        private void ValidateModuleScopeOperationPreconditions(Type moduleScopeKey)
        {
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
            if (!_scopeRegistry.TryResolveScopeType(moduleScopeKey, out _))
                throw new InvalidOperationException($"Module scope '{moduleScopeKey.Name}' is not declared.");
            if (_sceneContext == null)
                throw new InvalidOperationException("Scene context is not initialized. Call LoadSceneAsync first.");
        }

        internal Task ExecuteExclusiveScopeOperationAsync(IInitializationProgressNotifier? n, CancellationToken ct, Func<ScopeOperationCoordinator.ScopeOperationContext, Task> op) => _coordinator.ExecuteExclusiveScopeOperationAsync(n, ct, op);
        internal Task ExecuteParentInvalidatingExclusiveScopeOperationAsync(IInitializationProgressNotifier? n, CancellationToken ct, Func<ScopeOperationCoordinator.ScopeOperationContext, Task> op) => _coordinator.ExecuteParentInvalidatingExclusiveScopeOperationAsync(n, ct, op);
        internal Task ExecuteGenerationBoundSideScopeOperationAsync(IInitializationProgressNotifier? n, CancellationToken ct, Func<ScopeOperationCoordinator.ScopeOperationContext, Task> op) => _coordinator.ExecuteGenerationBoundSideScopeOperationAsync(n, ct, op);
        internal Task CancelActiveLoadAsync(CancellationToken ct = default) => _coordinator.CancelActiveLoadAsync(ct);
        internal long BeginNewScopeGeneration() => _coordinator.BeginNewScopeGeneration();
        internal long ReadScopeGeneration() => _coordinator.ReadScopeGeneration();
        internal void PublishInCurrentGeneration(long generation, CancellationToken ct, Action publish) => _coordinator.PublishInCurrentGeneration(generation, ct, publish);
        internal void ThrowIfStaleGeneration(long generation, CancellationToken ct) => _coordinator.ThrowIfStaleGeneration(generation, ct);
        internal SemaphoreSlim SideScopeLock => _coordinator.SideLock;
        internal SemaphoreSlim LazyInitLock => _coordinator.LazyInitLock;
    }
}
