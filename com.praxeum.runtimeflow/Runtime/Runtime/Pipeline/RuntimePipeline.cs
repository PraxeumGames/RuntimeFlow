using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Generic;
using System.Linq;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Orchestrates the full runtime lifecycle: bootstrap, flow execution, health monitoring, and teardown.
    /// </summary>
    public sealed partial class RuntimePipeline :
        IAsyncDisposable,
        IRuntimePipelineStateProvider,
        IRuntimeExecutionContextProvider,
        IRuntimeRestartLifecycleManager
    {
        private readonly GameContextBuilder _builder;
        private readonly RuntimeHealthSupervisor _healthSupervisor;
        private readonly IRuntimeErrorClassifier _errorClassifier;
        private readonly RuntimeRetryPolicyOptions _retryPolicy;
        private readonly IRuntimeRetryObserver _retryObserver;
        private readonly IRuntimeLoadingProgressObserver _loadingProgressObserver;
        private readonly IInitializationProgressNotifier? _defaultProgressNotifier;
        private readonly bool _replayFlowOnSessionRestart;
        private readonly IReadOnlyList<IRuntimeSessionRestartPreparationHook>? _sessionRestartPreparationHooks;
        private readonly ILogger _logger;
        private readonly object _statusSync = new();
        private long _loadingOperationSequence;
        private IScopeTransitionHandler _transitionHandler = NullScopeTransitionHandler.Instance;
        private IReadOnlyList<IRuntimeFlowGuard>? _guards;
        private IRuntimeFlowScenario? _flow;
        private IGameSceneLoader? _sceneLoader;
        private RuntimeStatus _status;
        private readonly RuntimeExecutionContextManager _executionContextManager;
        private readonly RuntimeReadinessGate _restartReadinessGate;
        private readonly RuntimeRestartLifecycleManager _restartLifecycleManager;
        private bool _disposed;

        private RuntimePipeline(
            GameContextBuilder builder,
            RuntimeHealthSupervisor healthSupervisor,
            IRuntimeErrorClassifier errorClassifier,
            RuntimeRetryPolicyOptions retryPolicy,
            IRuntimeRetryObserver retryObserver,
            IRuntimeLoadingProgressObserver loadingProgressObserver,
            IInitializationProgressNotifier? defaultProgressNotifier,
            bool replayFlowOnSessionRestart,
            IReadOnlyList<IRuntimeSessionRestartPreparationHook>? sessionRestartPreparationHooks,
            ILogger logger)
        {
            _builder = builder;
            _healthSupervisor = healthSupervisor;
            _errorClassifier = errorClassifier ?? throw new ArgumentNullException(nameof(errorClassifier));
            _retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
            _retryObserver = retryObserver ?? throw new ArgumentNullException(nameof(retryObserver));
            _loadingProgressObserver = loadingProgressObserver ?? throw new ArgumentNullException(nameof(loadingProgressObserver));
            _defaultProgressNotifier = defaultProgressNotifier;
            _replayFlowOnSessionRestart = replayFlowOnSessionRestart;
            _sessionRestartPreparationHooks = sessionRestartPreparationHooks;
            _guards = ComposeGuardsWithRestartPreparationHooks(
                guards: null,
                hooks: _sessionRestartPreparationHooks);
            _logger = logger;
            _status = new RuntimeStatus(
                RuntimeExecutionState.ColdStart,
                DateTimeOffset.UtcNow,
                currentOperationCode: RuntimeOperationCodes.ColdStart,
                message: "Pipeline is created and not initialized yet.",
                blockingReasonCode: RuntimeOperationCodes.ColdStart);
            _executionContextManager = new RuntimeExecutionContextManager(
                initialPhase: RuntimeExecutionPhase.Bootstrap,
                initialState: _status.State,
                currentOperationCode: _status.CurrentOperationCode,
                initialIsReplay: RuntimeFlowReplayScope.IsActive,
                timestampProvider: () => DateTimeOffset.UtcNow);
            _restartReadinessGate = new RuntimeReadinessGate(
                runtimeReadinessProvider: GetReadinessStatus,
                executionContextProvider: () => _executionContextManager.GetExecutionContext(),
                restartLifecycleSnapshotProvider: null,
                timestampProvider: () => DateTimeOffset.UtcNow);
            _restartLifecycleManager = new RuntimeRestartLifecycleManager(
                restartOperation: (request, ct) => RestartSessionAsync(cancellationToken: ct),
                replayOperation: null,
                readinessGate: _restartReadinessGate,
                guard: null,
                executionContextProvider: _executionContextManager,
                pipelineStateQuery: this,
                timestampProvider: () => DateTimeOffset.UtcNow);
        }

        public static RuntimePipeline Create(
            Action<GameContextBuilder> configure,
            Action<RuntimePipelineOptions>? configureOptions = null,
            ILoggerFactory? loggerFactory = null)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));

            var options = new RuntimePipelineOptions();
            configureOptions?.Invoke(options);

            var healthSupervisor = RuntimeHealthSupervisor.Create(options);
            var logger = loggerFactory?.CreateLogger<RuntimePipeline>() ?? (ILogger)NullLogger<RuntimePipeline>.Instance;
            var builderLogger = loggerFactory?.CreateLogger<GameContextBuilder>() ?? (ILogger)NullLogger<GameContextBuilder>.Instance;
            var builder = new GameContextBuilder(options.ExecutionScheduler, healthSupervisor, builderLogger);
            configure(builder);
            builder.FlushDeferredScopedRegistrations();
            return CreatePipeline(builder, healthSupervisor, options, logger);
        }

        public static RuntimePipeline CreateFromGlobalContext(
            IGameContext globalContext,
            Action<GameContextBuilder> configure,
            Action<RuntimePipelineOptions>? configureOptions = null,
            ILoggerFactory? loggerFactory = null)
        {
            if (globalContext == null) throw new ArgumentNullException(nameof(globalContext));
            if (configure == null) throw new ArgumentNullException(nameof(configure));

            var options = new RuntimePipelineOptions();
            configureOptions?.Invoke(options);

            var healthSupervisor = RuntimeHealthSupervisor.Create(options);
            var logger = loggerFactory?.CreateLogger<RuntimePipeline>() ?? (ILogger)NullLogger<RuntimePipeline>.Instance;
            var builderLogger = loggerFactory?.CreateLogger<GameContextBuilder>() ?? (ILogger)NullLogger<GameContextBuilder>.Instance;
            var builder = new GameContextBuilder(options.ExecutionScheduler, healthSupervisor, builderLogger);
            builder.UseExternalGlobalContext(globalContext);
            configure(builder);
            builder.FlushDeferredScopedRegistrations();
            return CreatePipeline(builder, healthSupervisor, options, logger);
        }

        public static RuntimePipeline CreateFromResolver(
            VContainer.IObjectResolver globalResolver,
            Action<GameContextBuilder> configure,
            Action<RuntimePipelineOptions>? configureOptions = null,
            ILoggerFactory? loggerFactory = null)
        {
            if (globalResolver == null) throw new ArgumentNullException(nameof(globalResolver));
            return CreateFromGlobalContext(
                new ResolverBackedGameContext(globalResolver),
                configure,
                configureOptions,
                loggerFactory);
        }

        private static RuntimePipeline CreatePipeline(
            GameContextBuilder builder,
            RuntimeHealthSupervisor healthSupervisor,
            RuntimePipelineOptions options,
            ILogger logger)
        {
            var errorClassifier = options.ErrorClassifier ?? DefaultRuntimeErrorClassifier.Instance;
            var retryObserver = options.RetryObserver ?? NullRuntimeRetryObserver.Instance;
            var loadingProgressObserver = options.LoadingProgressObserver ?? NullRuntimeLoadingProgressObserver.Instance;

            return new RuntimePipeline(
                builder,
                healthSupervisor,
                errorClassifier,
                options.RetryPolicy,
                retryObserver,
                loadingProgressObserver,
                options.DefaultProgressNotifier,
                options.ReplayFlowOnSessionRestart,
                options.SessionRestartPreparationHooks,
                logger);
        }

        public RuntimePipeline ConfigureFlow(IRuntimeFlowScenario flow)
        {
            _flow = flow ?? throw new ArgumentNullException(nameof(flow));
            return this;
        }

        public IGameContext SessionContext => _builder.GetSessionContext();

        public RuntimePipeline ConfigureTransitionHandler(IScopeTransitionHandler handler)
        {
            _transitionHandler = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        public RuntimePipeline ConfigureGuards(params IRuntimeFlowGuard[] guards)
        {
            _guards = ComposeGuardsWithRestartPreparationHooks(
                guards ?? throw new ArgumentNullException(nameof(guards)),
                _sessionRestartPreparationHooks);
            return this;
        }

        public RuntimePipeline ConfigureGuards(IEnumerable<IRuntimeFlowGuard> guards)
        {
            if (guards == null) throw new ArgumentNullException(nameof(guards));
            _guards = ComposeGuardsWithRestartPreparationHooks(
                guards is IReadOnlyList<IRuntimeFlowGuard> list ? list : new List<IRuntimeFlowGuard>(guards),
                _sessionRestartPreparationHooks);
            return this;
        }

        public RuntimeStatus GetRuntimeStatus()
        {
            lock (_statusSync)
            {
                return _status;
            }
        }

        public RuntimeReadinessStatus GetReadinessStatus()
        {
            var status = GetRuntimeStatus();
            return new RuntimeReadinessStatus(
                isReady: status.IsReady,
                updatedAtUtc: status.UpdatedAtUtc,
                currentOperationCode: status.CurrentOperationCode,
                blockingReasonCode: status.BlockingReasonCode,
                blockingReason: status.Message);
        }

        public RuntimeRestartReadiness GetRestartReadiness()
        {
            return _restartLifecycleManager.GetRestartReadiness();
        }

        public IRuntimeExecutionContext GetExecutionContext()
        {
            return _executionContextManager.GetExecutionContext();
        }

        RuntimeRestartLifecycleSnapshot IRuntimeRestartLifecycleManager.Snapshot => _restartLifecycleManager.Snapshot;

        Task IRuntimeRestartLifecycleManager.RestartAsync(
            RuntimeRestartRequest request,
            CancellationToken cancellationToken)
        {
            return _restartLifecycleManager.RestartAsync(request, cancellationToken);
        }

        public Task RestartSessionAsync(
            RuntimeRestartRequest request,
            CancellationToken cancellationToken = default)
        {
            return _restartLifecycleManager.RestartAsync(
                request ?? new RuntimeRestartRequest(),
                cancellationToken);
        }

        public async Task<IGameContext> InitializeAsync(
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            _logger.LogInformation("Pipeline initializing");
            var result = await ExecuteScopeOperationAsync(
                RuntimeLoadingOperationKind.Initialize,
                RuntimeOperationCodes.Initialize,
                scopeKey: null,
                RuntimeExecutionState.Initializing,
                splitPerScope: true,
                startMessage: "Initializing runtime contexts.",
                successMessage: "Runtime contexts initialized.",
                cancelMessage: "Initialization canceled by caller.",
                failMessage: "Initialization failed.",
                (notifier, ct) => _builder.BuildAsync(notifier, ct),
                progressNotifier,
                cancellationToken).ConfigureAwait(false);
            return result;
        }

        public Task LoadSceneAsync<TSceneScope>(
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            return LoadSceneAsync(typeof(TSceneScope), progressNotifier, cancellationToken);
        }

        public async Task LoadSceneAsync(
            Type sceneScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (sceneScopeKey == null) throw new ArgumentNullException(nameof(sceneScopeKey));
            _logger.LogInformation("Loading scene {ScopeType}", sceneScopeKey.Name);
            await EvaluateGuardsAsync(RuntimeFlowGuardStage.BeforeSceneLoad, sceneScopeKey, GameContextType.Scene, cancellationToken).ConfigureAwait(false);

            var transitionContext = new ScopeTransitionContext(
                _builder.ActiveSceneScopeKey != null ? GameContextType.Scene : GameContextType.Session,
                _builder.ActiveSceneScopeKey,
                GameContextType.Scene,
                sceneScopeKey);

            await _transitionHandler.OnTransitionOutAsync(transitionContext, cancellationToken).ConfigureAwait(false);
            await _transitionHandler.OnTransitionProgressAsync(transitionContext, 0f, cancellationToken).ConfigureAwait(false);

            await ExecuteScopeOperationAsync(
                RuntimeLoadingOperationKind.LoadScene,
                RuntimeOperationCodes.LoadScene,
                scopeKey: sceneScopeKey,
                RuntimeExecutionState.Initializing,
                splitPerScope: false,
                startMessage: $"Loading scene scope '{sceneScopeKey.Name}'.",
                successMessage: $"Scene scope '{sceneScopeKey.Name}' loaded.",
                cancelMessage: "Scene loading canceled by caller.",
                failMessage: $"Failed to load scene scope '{sceneScopeKey.Name}'.",
                (notifier, ct) => _builder.LoadSceneAsync(sceneScopeKey, notifier, ct),
                progressNotifier,
                cancellationToken).ConfigureAwait(false);

            await _transitionHandler.OnTransitionProgressAsync(transitionContext, 1f, cancellationToken).ConfigureAwait(false);
            await _transitionHandler.OnTransitionInAsync(transitionContext, cancellationToken).ConfigureAwait(false);
        }

        public Task LoadModuleAsync<TModuleScope>(
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            return LoadModuleAsync(typeof(TModuleScope), progressNotifier, cancellationToken);
        }

        public async Task LoadModuleAsync(
            Type moduleScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
            _logger.LogInformation("Loading module {ScopeType}", moduleScopeKey.Name);
            await EvaluateGuardsAsync(RuntimeFlowGuardStage.BeforeModuleLoad, moduleScopeKey, GameContextType.Module, cancellationToken).ConfigureAwait(false);

            var transitionContext = new ScopeTransitionContext(
                _builder.ActiveModuleScopeKey != null ? GameContextType.Module : GameContextType.Scene,
                _builder.ActiveModuleScopeKey ?? _builder.ActiveSceneScopeKey,
                GameContextType.Module,
                moduleScopeKey);

            await _transitionHandler.OnTransitionOutAsync(transitionContext, cancellationToken).ConfigureAwait(false);
            await _transitionHandler.OnTransitionProgressAsync(transitionContext, 0f, cancellationToken).ConfigureAwait(false);

            await ExecuteScopeOperationAsync(
                RuntimeLoadingOperationKind.LoadModule,
                RuntimeOperationCodes.LoadModule,
                scopeKey: moduleScopeKey,
                RuntimeExecutionState.Initializing,
                splitPerScope: false,
                startMessage: $"Loading module scope '{moduleScopeKey.Name}'.",
                successMessage: $"Module scope '{moduleScopeKey.Name}' loaded.",
                cancelMessage: "Module loading canceled by caller.",
                failMessage: $"Failed to load module scope '{moduleScopeKey.Name}'.",
                (notifier, ct) => _builder.LoadModuleAsync(moduleScopeKey, notifier, ct),
                progressNotifier,
                cancellationToken).ConfigureAwait(false);

            await _transitionHandler.OnTransitionProgressAsync(transitionContext, 1f, cancellationToken).ConfigureAwait(false);
            await _transitionHandler.OnTransitionInAsync(transitionContext, cancellationToken).ConfigureAwait(false);
        }

        public Task PreloadSceneAsync<TSceneScope>(
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            return PreloadSceneAsync(typeof(TSceneScope), progressNotifier, cancellationToken);
        }

        public async Task PreloadSceneAsync(
            Type sceneScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (sceneScopeKey == null) throw new ArgumentNullException(nameof(sceneScopeKey));
            await _builder.PreloadSceneAsync(sceneScopeKey, progressNotifier, cancellationToken).ConfigureAwait(false);
        }

        public Task PreloadModuleAsync<TModuleScope>(
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            return PreloadModuleAsync(typeof(TModuleScope), progressNotifier, cancellationToken);
        }

        public async Task PreloadModuleAsync(
            Type moduleScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
            await _builder.PreloadModuleAsync(moduleScopeKey, progressNotifier, cancellationToken).ConfigureAwait(false);
        }

        public bool HasPreloadedScope<TScope>()
        {
            return HasPreloadedScope(typeof(TScope));
        }

        public bool HasPreloadedScope(Type scopeKey)
        {
            if (scopeKey == null) throw new ArgumentNullException(nameof(scopeKey));
            return _builder.HasPreloadedScope(scopeKey);
        }

        public Task LoadAdditiveModuleAsync<TModuleScope>(
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            return LoadAdditiveModuleAsync(typeof(TModuleScope), progressNotifier, cancellationToken);
        }

        public async Task LoadAdditiveModuleAsync(
            Type moduleScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
            await _builder.LoadAdditiveModuleAsync(moduleScopeKey, progressNotifier, cancellationToken).ConfigureAwait(false);
        }

        public Task UnloadAdditiveModuleAsync<TModuleScope>(
            CancellationToken cancellationToken = default)
        {
            return UnloadAdditiveModuleAsync(typeof(TModuleScope), cancellationToken);
        }

        public async Task UnloadAdditiveModuleAsync(
            Type moduleScopeKey,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
            await _builder.UnloadAdditiveModuleAsync(moduleScopeKey, cancellationToken).ConfigureAwait(false);
        }

        public Task ReloadModuleAsync<TModuleScope>(
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            return ReloadModuleAsync(typeof(TModuleScope), progressNotifier, cancellationToken);
        }

        public Task ReloadScopeAsync<TScope>(
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            return ReloadScopeAsync(typeof(TScope), progressNotifier, cancellationToken);
        }

        public async Task ReloadScopeAsync(
            Type scopeType,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (scopeType == null) throw new ArgumentNullException(nameof(scopeType));
            if (!_builder.TryResolveScopeType(scopeType, out var scope))
                throw CreateScopeTypeNotDeclaredException(scopeType);
            _logger.LogInformation("Reloading scope {ScopeType}", scopeType.Name);
            await EvaluateGuardsAsync(RuntimeFlowGuardStage.BeforeScopeReload, scopeType, scope, cancellationToken).ConfigureAwait(false);

            await (scope switch
            {
                GameContextType.Session => RestartSessionAsync(progressNotifier, cancellationToken),
                GameContextType.Scene => ReloadSceneAsync(scopeType, progressNotifier, cancellationToken),
                GameContextType.Module => ReloadModuleAsync(scopeType, progressNotifier, cancellationToken),
                GameContextType.Global => throw new ScopeNotRestartableException(scopeType),
                _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported scope type.")
            }).ConfigureAwait(false);
        }

        public async Task ReloadModuleAsync(
            Type moduleScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
            await EvaluateGuardsAsync(RuntimeFlowGuardStage.BeforeScopeReload, moduleScopeKey, GameContextType.Module, cancellationToken).ConfigureAwait(false);
            await ExecuteScopeOperationAsync(
                RuntimeLoadingOperationKind.ReloadModule,
                RuntimeOperationCodes.ReloadModule,
                scopeKey: moduleScopeKey,
                RuntimeExecutionState.Initializing,
                splitPerScope: false,
                startMessage: $"Reloading module scope '{moduleScopeKey.Name}'.",
                successMessage: $"Module scope '{moduleScopeKey.Name}' reloaded.",
                cancelMessage: "Module reloading canceled by caller.",
                failMessage: $"Failed to reload module scope '{moduleScopeKey.Name}'.",
                (notifier, ct) => _builder.ReloadModuleAsync(moduleScopeKey, notifier, ct),
                progressNotifier,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task ReloadSceneAsync(
            Type sceneScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            if (sceneScopeKey == null) throw new ArgumentNullException(nameof(sceneScopeKey));
            await ExecuteScopeOperationAsync(
                RuntimeLoadingOperationKind.ReloadScene,
                RuntimeOperationCodes.ReloadScene,
                scopeKey: sceneScopeKey,
                RuntimeExecutionState.Initializing,
                splitPerScope: false,
                startMessage: $"Reloading scene scope '{sceneScopeKey.Name}'.",
                successMessage: $"Scene scope '{sceneScopeKey.Name}' reloaded.",
                cancelMessage: "Scene reloading canceled by caller.",
                failMessage: $"Failed to reload scene scope '{sceneScopeKey.Name}'.",
                (notifier, ct) => _builder.LoadSceneAsync(sceneScopeKey, notifier, ct),
                progressNotifier,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task RestartSessionAsync(
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            _logger.LogInformation("Restarting session");
            if (_replayFlowOnSessionRestart && _flow != null && _sceneLoader != null)
            {
                await EvaluateGuardsAsync(RuntimeFlowGuardStage.BeforeSessionRestart, null, GameContextType.Session, cancellationToken);
                await RestartSessionByReplayingFlowAsync(progressNotifier ?? _defaultProgressNotifier, cancellationToken);
                return;
            }

            await EvaluateGuardsAsync(RuntimeFlowGuardStage.BeforeSessionRestart, null, GameContextType.Session, cancellationToken).ConfigureAwait(false);
            await ExecuteScopeOperationAsync(
                RuntimeLoadingOperationKind.RestartSession,
                RuntimeOperationCodes.RestartSession,
                scopeKey: null,
                RuntimeExecutionState.Recovering,
                splitPerScope: true,
                startMessage: "Restarting session.",
                successMessage: "Session restarted.",
                cancelMessage: "Session restart canceled by caller.",
                failMessage: "Session restart failed.",
                (notifier, ct) => _builder.RestartSessionAsync(notifier, ct),
                progressNotifier,
                cancellationToken).ConfigureAwait(false);
        }
        /// <summary>
        /// Returns the current lifecycle state of the specified scope.
        /// </summary>
        public ScopeLifecycleState GetScopeState<TScope>()
        {
            return GetScopeState(typeof(TScope));
        }

        /// <summary>
        /// Returns the current lifecycle state of the specified scope.
        /// </summary>
        public ScopeLifecycleState GetScopeState(Type scopeType)
        {
            if (scopeType == null) throw new ArgumentNullException(nameof(scopeType));
            return _builder.GetScopeLifecycleState(scopeType);
        }

        /// <summary>
        /// Returns true if the specified scope is fully initialized and activated.
        /// </summary>
        public bool IsScopeActive<TScope>()
        {
            return GetScopeState<TScope>() == ScopeLifecycleState.Active;
        }

        /// <summary>
        /// Returns true if the specified scope is fully initialized and activated.
        /// </summary>
        public bool IsScopeActive(Type scopeType)
        {
            return GetScopeState(scopeType) == ScopeLifecycleState.Active;
        }

        /// <summary>
        /// Returns true if the specified scope can currently be reloaded
        /// (must be active and declared as a restartable scope type).
        /// </summary>
        public bool CanReloadScope<TScope>()
        {
            return CanReloadScope(typeof(TScope));
        }

        /// <summary>
        /// Returns true if the specified scope can currently be reloaded
        /// (must be active and declared as a restartable scope type).
        /// </summary>
        public bool CanReloadScope(Type scopeType)
        {
            if (scopeType == null) throw new ArgumentNullException(nameof(scopeType));
            if (_disposed) return false;

            var state = _builder.GetScopeLifecycleState(scopeType);
            if (state != ScopeLifecycleState.Active) return false;

            if (!_builder.TryResolveScopeType(scopeType, out var contextType)) return false;
            return RuntimeScopeRestartabilityPolicy.IsRestartable(contextType);
        }

    }
}
