using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Generic;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Orchestrates the full runtime lifecycle: bootstrap, flow execution, health monitoring, and teardown.
    /// </summary>
    public sealed class RuntimePipeline :
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
            _guards = guards ?? throw new ArgumentNullException(nameof(guards));
            return this;
        }

        public RuntimePipeline ConfigureGuards(IEnumerable<IRuntimeFlowGuard> guards)
        {
            if (guards == null) throw new ArgumentNullException(nameof(guards));
            _guards = guards is IReadOnlyList<IRuntimeFlowGuard> list ? list : new List<IRuntimeFlowGuard>(guards);
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

        public async Task RunAsync(
            IGameSceneLoader sceneLoader,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (sceneLoader == null) throw new ArgumentNullException(nameof(sceneLoader));
            if (_flow == null)
                throw new FlowNotConfiguredException();
            _sceneLoader = sceneLoader;
            _logger.LogInformation("Running flow scenario {ScenarioType}", _flow.GetType().Name);
            var operationId = CreateLoadingOperationId(RuntimeLoadingOperationKind.RunFlow);

            SetStatus(RuntimeExecutionState.Initializing, operationCode: RuntimeOperationCodes.RunFlow, message: "Executing runtime flow.");
            PublishLoadingSnapshot(
                operationId,
                RuntimeLoadingOperationKind.RunFlow,
                RuntimeLoadingOperationStage.Preparing,
                RuntimeLoadingOperationState.Running,
                percent: 0d,
                currentStep: 0,
                totalSteps: 1,
                message: "Executing runtime flow.");

            _healthSupervisor.BeginRun();
            var runner = new RuntimeFlowRunner(
                _builder,
                sceneLoader,
                progressNotifier ?? _defaultProgressNotifier,
                _loadingProgressObserver,
                CreateLoadingOperationId,
                _healthSupervisor,
                _errorClassifier,
                _retryPolicy,
                _retryObserver,
                _transitionHandler,
                _guards,
                OnRunnerStatusChanged);

            try
            {
                await _flow.ExecuteAsync(runner, cancellationToken).ConfigureAwait(false);
                PublishLoadingSnapshot(
                    operationId,
                    RuntimeLoadingOperationKind.RunFlow,
                    RuntimeLoadingOperationStage.Finalizing,
                    RuntimeLoadingOperationState.Running,
                    percent: 100d,
                    currentStep: 1,
                    totalSteps: 1,
                    message: "Finalizing runtime flow.");
                PublishLoadingSnapshot(
                    operationId,
                    RuntimeLoadingOperationKind.RunFlow,
                    RuntimeLoadingOperationStage.Completed,
                    RuntimeLoadingOperationState.Completed,
                    percent: 100d,
                    currentStep: 1,
                    totalSteps: 1,
                    message: "Runtime flow completed.");
                SetStatus(RuntimeExecutionState.Ready, operationCode: RuntimeOperationCodes.RunFlow, message: "Runtime flow completed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                PublishLoadingSnapshot(
                    operationId,
                    RuntimeLoadingOperationKind.RunFlow,
                    RuntimeLoadingOperationStage.Canceled,
                    RuntimeLoadingOperationState.Canceled,
                    percent: 0d,
                    currentStep: 0,
                    totalSteps: 1,
                    message: "Runtime flow canceled by caller.");
                SetStatus(RuntimeExecutionState.Degraded, operationCode: RuntimeOperationCodes.RunFlow, message: "Runtime flow canceled by caller.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipeline operation failed");
                PublishLoadingSnapshot(
                    operationId,
                    RuntimeLoadingOperationKind.RunFlow,
                    RuntimeLoadingOperationStage.Failed,
                    RuntimeLoadingOperationState.Failed,
                    percent: 0d,
                    currentStep: 0,
                    totalSteps: 1,
                    message: "Runtime flow failed.",
                    error: ex);
                SetStatus(RuntimeExecutionState.Failed, operationCode: RuntimeOperationCodes.RunFlow, message: "Runtime flow failed.", error: ex);
                throw;
            }
        }

        private async Task RestartSessionByReplayingFlowAsync(
            IInitializationProgressNotifier? progressNotifier,
            CancellationToken cancellationToken)
        {
            var sceneLoader = _sceneLoader ?? throw new InvalidOperationException(
                "Cannot replay flow for session restart before the pipeline has been run at least once.");
            var flow = _flow ?? throw new FlowNotConfiguredException();
            var operationId = CreateLoadingOperationId(RuntimeLoadingOperationKind.RestartSession);

            SetStatus(RuntimeExecutionState.Recovering, operationCode: RuntimeOperationCodes.RestartSession, message: "Replaying runtime flow for session restart.");
            PublishLoadingSnapshot(
                operationId,
                RuntimeLoadingOperationKind.RestartSession,
                RuntimeLoadingOperationStage.Preparing,
                RuntimeLoadingOperationState.Running,
                percent: 0d,
                currentStep: 0,
                totalSteps: 1,
                message: "Replaying runtime flow for session restart.");

            _healthSupervisor.BeginRun();
            var runner = new RuntimeFlowRunner(
                _builder,
                sceneLoader,
                progressNotifier ?? _defaultProgressNotifier,
                _loadingProgressObserver,
                CreateLoadingOperationId,
                _healthSupervisor,
                _errorClassifier,
                _retryPolicy,
                _retryObserver,
                _transitionHandler,
                _guards,
                OnRunnerStatusChanged);

            try
            {
                using (RuntimeFlowReplayScope.Enter())
                {
                    await _builder.ExecuteOnMainThreadAsync(
                            token => flow.ExecuteAsync(runner, token),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                PublishLoadingSnapshot(
                    operationId,
                    RuntimeLoadingOperationKind.RestartSession,
                    RuntimeLoadingOperationStage.Finalizing,
                    RuntimeLoadingOperationState.Running,
                    percent: 100d,
                    currentStep: 1,
                    totalSteps: 1,
                    message: "Finalizing session restart.");
                PublishLoadingSnapshot(
                    operationId,
                    RuntimeLoadingOperationKind.RestartSession,
                    RuntimeLoadingOperationStage.Completed,
                    RuntimeLoadingOperationState.Completed,
                    percent: 100d,
                    currentStep: 1,
                    totalSteps: 1,
                    message: "Session restarted.");
                SetStatus(RuntimeExecutionState.Ready, operationCode: RuntimeOperationCodes.RestartSession, message: "Session restarted.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                PublishLoadingSnapshot(
                    operationId,
                    RuntimeLoadingOperationKind.RestartSession,
                    RuntimeLoadingOperationStage.Canceled,
                    RuntimeLoadingOperationState.Canceled,
                    percent: 0d,
                    currentStep: 0,
                    totalSteps: 1,
                    message: "Session restart canceled by caller.");
                SetStatus(RuntimeExecutionState.Degraded, operationCode: RuntimeOperationCodes.RestartSession, message: "Session restart canceled by caller.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipeline operation failed");
                PublishLoadingSnapshot(
                    operationId,
                    RuntimeLoadingOperationKind.RestartSession,
                    RuntimeLoadingOperationStage.Failed,
                    RuntimeLoadingOperationState.Failed,
                    percent: 0d,
                    currentStep: 0,
                    totalSteps: 1,
                    message: "Session restart failed.",
                    error: ex);
                SetStatus(RuntimeExecutionState.Failed, operationCode: RuntimeOperationCodes.RestartSession, message: "Session restart failed.", error: ex);
                throw;
            }
        }

        private void OnRunnerStatusChanged(RuntimeExecutionState state, string? message)
        {
            SetStatus(state, operationCode: RuntimeOperationCodes.Recovery, message: message);
        }

        private async Task<T> ExecuteScopeOperationAsync<T>(
            RuntimeLoadingOperationKind operationKind,
            string operationCode,
            Type? scopeKey,
            RuntimeExecutionState startState,
            bool splitPerScope,
            string startMessage,
            string successMessage,
            string cancelMessage,
            string failMessage,
            Func<IInitializationProgressNotifier, CancellationToken, Task<T>> operation,
            IInitializationProgressNotifier? progressNotifier,
            CancellationToken cancellationToken)
        {
            var operationId = CreateLoadingOperationId(operationKind);
            var notifier = CreateProgressNotifier(progressNotifier, operationKind, operationId, splitPerScope);

            SetStatus(startState, operationCode: operationCode, message: startMessage);
            PublishLoadingSnapshot(
                operationId, operationKind,
                RuntimeLoadingOperationStage.Preparing, RuntimeLoadingOperationState.Running,
                scopeKey: scopeKey, scopeName: scopeKey?.Name,
                percent: 0d, currentStep: 0, totalSteps: 1,
                message: startMessage);

            try
            {
                var result = await operation(notifier, cancellationToken).ConfigureAwait(false);
                SetStatus(RuntimeExecutionState.Ready, operationCode: operationCode, message: successMessage);
                return result;
            }
            catch (OperationCanceledException)
            {
                PublishLoadingSnapshot(
                    operationId, operationKind,
                    RuntimeLoadingOperationStage.Canceled, RuntimeLoadingOperationState.Canceled,
                    scopeKey: scopeKey, scopeName: scopeKey?.Name,
                    percent: 0d, currentStep: 0, totalSteps: 1,
                    message: cancelMessage);
                // Only downgrade status if the pipeline hasn't already moved to a terminal state
                // (e.g., a concurrent reload may have already succeeded and set Ready).
                lock (_statusSync)
                {
                    if (_status.State != RuntimeExecutionState.Ready)
                        SetStatusUnsafe(RuntimeExecutionState.Degraded, operationCode: operationCode, message: cancelMessage);
                }
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipeline operation failed");
                PublishLoadingSnapshot(
                    operationId, operationKind,
                    RuntimeLoadingOperationStage.Failed, RuntimeLoadingOperationState.Failed,
                    scopeKey: scopeKey, scopeName: scopeKey?.Name,
                    percent: 0d, currentStep: 0, totalSteps: 1,
                    message: failMessage, error: ex);
                SetStatus(RuntimeExecutionState.Failed, operationCode: operationCode, message: failMessage, error: ex);
                throw;
            }
        }

        private async Task ExecuteScopeOperationAsync(
            RuntimeLoadingOperationKind operationKind,
            string operationCode,
            Type? scopeKey,
            RuntimeExecutionState startState,
            bool splitPerScope,
            string startMessage,
            string successMessage,
            string cancelMessage,
            string failMessage,
            Func<IInitializationProgressNotifier, CancellationToken, Task> operation,
            IInitializationProgressNotifier? progressNotifier,
            CancellationToken cancellationToken)
        {
            await ExecuteScopeOperationAsync<object?>(
                operationKind, operationCode, scopeKey, startState, splitPerScope,
                startMessage, successMessage, cancelMessage, failMessage,
                async (notifier, ct) => { await operation(notifier, ct).ConfigureAwait(false); return null; },
                progressNotifier, cancellationToken).ConfigureAwait(false);
        }

        private IInitializationProgressNotifier CreateProgressNotifier(
            IInitializationProgressNotifier? progressNotifier,
            RuntimeLoadingOperationKind operationKind,
            string operationId,
            bool splitOperationPerScope = false)
        {
            var baseNotifier = progressNotifier ?? _defaultProgressNotifier ?? NullInitializationProgressNotifier.Instance;
            var loadingNotifier = new RuntimeLoadingProgressNotifierAdapter(
                _loadingProgressObserver,
                operationKind,
                operationId,
                splitOperationPerScope: splitOperationPerScope);
            return new CompositeInitializationProgressNotifier(baseNotifier, loadingNotifier);
        }

        private string CreateLoadingOperationId(RuntimeLoadingOperationKind operationKind)
        {
            var sequence = Interlocked.Increment(ref _loadingOperationSequence);
            var operationCode = operationKind switch
            {
                RuntimeLoadingOperationKind.Initialize => RuntimeOperationCodes.Initialize,
                RuntimeLoadingOperationKind.LoadScene => RuntimeOperationCodes.LoadScene,
                RuntimeLoadingOperationKind.LoadModule => RuntimeOperationCodes.LoadModule,
                RuntimeLoadingOperationKind.ReloadModule => RuntimeOperationCodes.ReloadModule,
                RuntimeLoadingOperationKind.RestartSession => RuntimeOperationCodes.RestartSession,
                RuntimeLoadingOperationKind.ReloadScene => RuntimeOperationCodes.ReloadScene,
                RuntimeLoadingOperationKind.RunFlow => RuntimeOperationCodes.RunFlow,
                _ => "loading"
            };

            return $"{operationCode}-{sequence:D6}";
        }

        private async Task EvaluateGuardsAsync(
            RuntimeFlowGuardStage stage,
            Type? scopeKey,
            GameContextType? targetScopeType,
            CancellationToken cancellationToken)
        {
            if (_guards == null || _guards.Count == 0) return;

            var context = new RuntimeFlowGuardContext(stage, null, scopeKey, targetScopeType);
            foreach (var guard in _guards)
            {
                var result = await guard.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
                if (!result.IsAllowed)
                    throw new RuntimeFlowGuardFailedException(stage, result.ReasonCode!, result.Reason);
            }
        }

        private static ScopeNotDeclaredException CreateScopeTypeNotDeclaredException(Type scopeType)
        {
            return new ScopeNotDeclaredException(scopeType);
        }

        private void PublishLoadingSnapshot(
            string operationId,
            RuntimeLoadingOperationKind operationKind,
            RuntimeLoadingOperationStage stage,
            RuntimeLoadingOperationState state,
            Type? scopeKey = null,
            string? scopeName = null,
            double percent = 0d,
            int currentStep = 0,
            int totalSteps = 0,
            string? message = null,
            Exception? error = null)
        {
            _loadingProgressObserver.OnLoadingProgress(
                new RuntimeLoadingOperationSnapshot(
                    operationId: operationId,
                    operationKind: operationKind,
                    stage: stage,
                    state: state,
                    scopeKey: scopeKey,
                    scopeName: scopeName,
                    percent: percent,
                    currentStep: currentStep,
                    totalSteps: totalSteps,
                    message: message,
                    timestampUtc: DateTimeOffset.UtcNow,
                    errorType: error?.GetType().Name,
                    errorMessage: error?.Message));
        }

        public ValueTask DisposeAsync()
        {
            return DisposeAsyncCore(CancellationToken.None);
        }

        internal async ValueTask DisposeAsync(CancellationToken cancellationToken)
        {
            await DisposeAsyncCore(cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask DisposeAsyncCore(CancellationToken cancellationToken)
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                await _builder.DisposeAllScopesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort disposal
            }

            _logger.LogDebug("Pipeline disposed");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RuntimePipeline));
        }

        private void SetStatus(
            RuntimeExecutionState state,
            string? operationCode = null,
            string? message = null,
            Exception? error = null)
        {
            lock (_statusSync)
            {
                SetStatusUnsafe(state, operationCode, message, error);
            }
        }

        /// <summary>
        /// Sets status without acquiring <see cref="_statusSync"/>. Caller must hold the lock.
        /// </summary>
        private void SetStatusUnsafe(
            RuntimeExecutionState state,
            string? operationCode = null,
            string? message = null,
            Exception? error = null)
        {
            var blockingReasonCode = state switch
            {
                RuntimeExecutionState.ColdStart => RuntimeOperationCodes.ColdStart,
                RuntimeExecutionState.Initializing => operationCode ?? "initializing",
                RuntimeExecutionState.Recovering => operationCode ?? "recovering",
                RuntimeExecutionState.Failed => operationCode ?? "failed",
                _ => null
            };

            var status = new RuntimeStatus(
                state,
                DateTimeOffset.UtcNow,
                currentOperationCode: operationCode,
                message: message,
                blockingReasonCode: blockingReasonCode,
                lastErrorType: error?.GetType().Name,
                lastErrorMessage: error?.Message);

            _status = status;
            _executionContextManager.UpdateFromStatus(
                DetermineExecutionPhase(state, operationCode),
                status,
                RuntimeFlowReplayScope.IsActive);
        }

        private static RuntimeExecutionPhase DetermineExecutionPhase(
            RuntimeExecutionState state,
            string? operationCode)
        {
            if (state == RuntimeExecutionState.Recovering)
                return RuntimeExecutionPhase.Restart;

            if (string.Equals(operationCode, RuntimeOperationCodes.RestartSession, StringComparison.Ordinal)
                || string.Equals(operationCode, RuntimeOperationCodes.Recovery, StringComparison.Ordinal))
            {
                return RuntimeExecutionPhase.Restart;
            }

            if (string.Equals(operationCode, RuntimeOperationCodes.RunFlow, StringComparison.Ordinal)
                || string.Equals(operationCode, RuntimeOperationCodes.LoadScene, StringComparison.Ordinal)
                || string.Equals(operationCode, RuntimeOperationCodes.LoadModule, StringComparison.Ordinal)
                || string.Equals(operationCode, RuntimeOperationCodes.ReloadScene, StringComparison.Ordinal)
                || string.Equals(operationCode, RuntimeOperationCodes.ReloadModule, StringComparison.Ordinal))
            {
                return RuntimeExecutionPhase.Flow;
            }

            if (state == RuntimeExecutionState.ColdStart
                || string.Equals(operationCode, RuntimeOperationCodes.ColdStart, StringComparison.Ordinal)
                || string.Equals(operationCode, RuntimeOperationCodes.Initialize, StringComparison.Ordinal))
            {
                return RuntimeExecutionPhase.Bootstrap;
            }

            return RuntimeFlowReplayScope.IsActive
                ? RuntimeExecutionPhase.Restart
                : RuntimeExecutionPhase.Flow;
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
