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
        private readonly GameContextScopeInitializationLedger _scopeInitializationLedger = new();
        private readonly Dictionary<Type, GameContext> _preloadedContexts = new();
        private readonly Dictionary<Type, GameContext> _additiveModuleContexts = new();
        private static readonly Lazy<Type[]> ExplicitDependencyTypeCatalog = new(
            BuildExplicitDependencyTypeCatalog,
            LazyThreadSafetyMode.ExecutionAndPublication);

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

        internal Type? ActiveSceneScopeKey => _activeSceneScopeKey;
        internal Type? ActiveModuleScopeKey => _activeModuleScopeKey;
        private bool _ownsGlobalContext = true;

        private ScopeEventBus? _globalEventBus;
        private ScopeEventBus? _sessionEventBus;
        private ScopeEventBus? _sceneEventBus;
        private ScopeEventBus? _moduleEventBus;

        private CancellationTokenSource? _activeLoadCts;
        private Task _activeLoadTask = Task.CompletedTask;
        private long _runGeneration;

        private readonly GameContextLazyInitializationRegistry _lazyInitialization = new();
        private readonly SemaphoreSlim _lazyInitLock = new(1, 1);

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
        }

        internal Task ExecuteOnMainThreadAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            return _executionScheduler.ExecuteAsync(
                InitializationThreadAffinity.MainThread,
                operation,
                cancellationToken);
        }

        private Task ExecuteStageCallbackOnMainThreadAsync(
            Func<CancellationToken, Task> callback,
            CancellationToken cancellationToken)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            return _executionScheduler.ExecuteAsync(
                InitializationThreadAffinity.MainThread,
                callback,
                cancellationToken);
        }

        private Task DrainMainThreadFrameAsync(CancellationToken cancellationToken)
        {
            return _executionScheduler.ExecuteAsync(
                InitializationThreadAffinity.MainThread,
                async token =>
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Yield();
                    token.ThrowIfCancellationRequested();
                    await Task.Yield();
                },
                cancellationToken);
        }

        internal void UseExternalGlobalContext(IGameContext globalContext)
        {
            _globalContext = globalContext ?? throw new ArgumentNullException(nameof(globalContext));
            _ownsGlobalContext = false;
        }

        public IGameContextBuilder OnGlobalInitialized(Action<IGameContext> callback)
        {
            _onGlobalInitialized += callback;
            return this;
        }

        public IGameContextBuilder OnSessionInitialized(Action<IGameContext> callback)
        {
            _onSessionInitialized += callback;
            return this;
        }

        public IGameContextBuilder OnSceneInitialized(Action<IGameContext> callback)
        {
            _onSceneInitialized += callback;
            return this;
        }

        public IGameContextBuilder OnModuleInitialized(Action<IGameContext> callback)
        {
            _onModuleInitialized += callback;
            return this;
        }
    }
}
