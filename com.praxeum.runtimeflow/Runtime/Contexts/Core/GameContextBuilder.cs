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
        private readonly Dictionary<Type, GameContext> _preloadedContexts = new();
        private readonly Dictionary<Type, GameContext> _additiveModuleContexts = new();

        private Action<IGameContext>? _onGlobalInitialized;
        private Action<IGameContext>? _onSessionInitialized;
        private Action<IGameContext>? _onSceneInitialized;
        private Action<IGameContext>? _onModuleInitialized;

        private IGameContext? _globalContext;
        private GameContext? _sessionContext;
        private GameContext? _sceneContext;
        private GameContext? _moduleContext;
        private Type? _activeSceneScopeKey;
        private Type? _activeModuleScopeKey;

        internal IGameContext? GlobalContext => _globalContext;
        internal GameContext? SessionContext => _sessionContext;
        internal GameContext? SceneContext => _sceneContext;
        internal GameContext? ModuleContext => _moduleContext;
        internal IReadOnlyDictionary<Type, GameContext> PreloadedContexts => _preloadedContexts;
        internal IReadOnlyDictionary<Type, GameContext> AdditiveModuleContexts => _additiveModuleContexts;
        internal Type? ActiveSceneScopeKey => _activeSceneScopeKey;
        internal Type? ActiveModuleScopeKey => _activeModuleScopeKey;
        private bool _ownsGlobalContext = true;

        private ScopeEventBus? _globalEventBus;
        private ScopeEventBus? _sessionEventBus;
        private ScopeEventBus? _sceneEventBus;
        private ScopeEventBus? _moduleEventBus;

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
            _activationService = new ScopeActivationService(_executionScheduler);
            _scopeTransitions = new ScopeTransitionEngine(this);
        }

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
