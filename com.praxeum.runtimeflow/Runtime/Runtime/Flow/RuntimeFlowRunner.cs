using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    internal sealed class RuntimeFlowRunner : IRuntimeFlowContext
    {
        private static readonly System.Random JitterRandom = new();

        private readonly GameContextBuilder _builder;
        private readonly IGameSceneLoader _sceneLoader;
        private readonly IInitializationProgressNotifier _progressNotifier;
        private readonly IRuntimeLoadingProgressObserver _loadingProgressObserver;
        private readonly Func<RuntimeLoadingOperationKind, string> _operationIdFactory;
        private readonly RuntimeHealthSupervisor _healthSupervisor;
        private readonly IRuntimeErrorClassifier _errorClassifier;
        private readonly RuntimeRetryPolicyOptions _retryPolicy;
        private readonly IRuntimeRetryObserver _retryObserver;
        private readonly IScopeTransitionHandler _transitionHandler;
        private readonly IReadOnlyList<IRuntimeFlowGuard>? _guards;
        private readonly Action<RuntimeExecutionState, string?>? _statusObserver;

        public RuntimeFlowRunner(
            GameContextBuilder builder,
            IGameSceneLoader sceneLoader,
            IInitializationProgressNotifier? progressNotifier,
            IRuntimeLoadingProgressObserver? loadingProgressObserver,
            Func<RuntimeLoadingOperationKind, string>? operationIdFactory,
            RuntimeHealthSupervisor healthSupervisor,
            IRuntimeErrorClassifier errorClassifier,
            RuntimeRetryPolicyOptions retryPolicy,
            IRuntimeRetryObserver retryObserver,
            IScopeTransitionHandler transitionHandler,
            IReadOnlyList<IRuntimeFlowGuard>? guards = null,
            Action<RuntimeExecutionState, string?>? statusObserver = null)
        {
            _builder = builder ?? throw new ArgumentNullException(nameof(builder));
            _sceneLoader = sceneLoader ?? throw new ArgumentNullException(nameof(sceneLoader));
            _progressNotifier = progressNotifier ?? NullInitializationProgressNotifier.Instance;
            _loadingProgressObserver = loadingProgressObserver ?? NullRuntimeLoadingProgressObserver.Instance;
            _operationIdFactory = operationIdFactory ?? (_ => Guid.NewGuid().ToString("N"));
            _healthSupervisor = healthSupervisor ?? throw new ArgumentNullException(nameof(healthSupervisor));
            _errorClassifier = errorClassifier ?? throw new ArgumentNullException(nameof(errorClassifier));
            _retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
            _retryObserver = retryObserver ?? throw new ArgumentNullException(nameof(retryObserver));
            _transitionHandler = transitionHandler ?? NullScopeTransitionHandler.Instance;
            _guards = guards;
            _statusObserver = statusObserver;
        }

        public IGameContext SessionContext => _builder.GetSessionContext();

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteWithRecoveryAsync(
                operation: (notifier, token) => _builder.CanRestartSession()
                    ? _builder.RestartSessionAsync(notifier, token)
                    : _builder.BuildAsync(notifier, token),
                retryAfterRecovery: false,
                operationCode: RuntimeOperationCodes.Initialize,
                loadingOperationKind: RuntimeLoadingOperationKind.Initialize,
                splitOperationPerScope: true,
                cancellationToken: cancellationToken);
        }

        public async Task LoadScopeSceneAsync(Type sceneScopeKey, CancellationToken cancellationToken = default)
        {
            if (sceneScopeKey == null) throw new ArgumentNullException(nameof(sceneScopeKey));

            var transitionContext = new ScopeTransitionContext(
                _builder.ActiveSceneScopeKey != null ? GameContextType.Scene : GameContextType.Session,
                _builder.ActiveSceneScopeKey,
                GameContextType.Scene,
                sceneScopeKey);

            await _transitionHandler.OnTransitionOutAsync(transitionContext, cancellationToken).ConfigureAwait(false);

            await ExecuteWithRecoveryAsync(
                operation: async (notifier, token) =>
                {
                    await _builder.LoadSceneAsync(sceneScopeKey, notifier, token).ConfigureAwait(false);
                    await _transitionHandler.OnTransitionProgressAsync(transitionContext, 1f, token).ConfigureAwait(false);
                },
                retryAfterRecovery: true,
                operationCode: "load_scene_scope",
                loadingOperationKind: RuntimeLoadingOperationKind.LoadScene,
                splitOperationPerScope: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await _transitionHandler.OnTransitionInAsync(transitionContext, cancellationToken).ConfigureAwait(false);
        }

        public Task LoadScopeSceneAsync<TSceneScope>(CancellationToken cancellationToken = default)
        {
            return LoadScopeSceneAsync(typeof(TSceneScope), cancellationToken);
        }

        public async Task LoadScopeModuleAsync(Type moduleScopeKey, CancellationToken cancellationToken = default)
        {
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));

            var transitionContext = new ScopeTransitionContext(
                _builder.ActiveModuleScopeKey != null ? GameContextType.Module : GameContextType.Scene,
                _builder.ActiveModuleScopeKey,
                GameContextType.Module,
                moduleScopeKey);

            await _transitionHandler.OnTransitionOutAsync(transitionContext, cancellationToken).ConfigureAwait(false);

            await ExecuteWithRecoveryAsync(
                operation: async (notifier, token) =>
                {
                    await _builder.LoadModuleAsync(moduleScopeKey, notifier, token).ConfigureAwait(false);
                    await _transitionHandler.OnTransitionProgressAsync(transitionContext, 1f, token).ConfigureAwait(false);
                },
                retryAfterRecovery: true,
                operationCode: "load_module_scope",
                loadingOperationKind: RuntimeLoadingOperationKind.LoadModule,
                splitOperationPerScope: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await _transitionHandler.OnTransitionInAsync(transitionContext, cancellationToken).ConfigureAwait(false);
        }

        public Task LoadScopeModuleAsync<TModuleScope>(CancellationToken cancellationToken = default)
        {
            return LoadScopeModuleAsync(typeof(TModuleScope), cancellationToken);
        }

        public async Task ReloadScopeModuleAsync(Type moduleScopeKey, CancellationToken cancellationToken = default)
        {
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
            await EvaluateGuardsAsync(RuntimeFlowGuardStage.BeforeScopeReload, moduleScopeKey, GameContextType.Module, cancellationToken).ConfigureAwait(false);
            await ExecuteWithRecoveryAsync(
                operation: (notifier, token) => _builder.ReloadModuleAsync(moduleScopeKey, notifier, token),
                retryAfterRecovery: true,
                operationCode: "reload_module_scope",
                loadingOperationKind: RuntimeLoadingOperationKind.ReloadModule,
                splitOperationPerScope: false,
                cancellationToken: cancellationToken);
        }

        public Task ReloadScopeModuleAsync<TModuleScope>(CancellationToken cancellationToken = default)
        {
            return ReloadScopeModuleAsync(typeof(TModuleScope), cancellationToken);
        }

        public async Task ReloadScopeAsync(Type scopeType, CancellationToken cancellationToken = default)
        {
            if (scopeType == null) throw new ArgumentNullException(nameof(scopeType));
            if (!_builder.TryResolveScopeType(scopeType, out var scope))
                throw CreateScopeTypeNotDeclaredException(scopeType);

            await EvaluateGuardsAsync(RuntimeFlowGuardStage.BeforeScopeReload, scopeType, scope, cancellationToken).ConfigureAwait(false);
            if (scope == GameContextType.Session)
            {
                await EvaluateGuardsAsync(
                        RuntimeFlowGuardStage.BeforeSessionRestart,
                        scopeKey: null,
                        targetScopeType: GameContextType.Session,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await (scope switch
            {
                GameContextType.Session => ExecuteWithRecoveryAsync(
                    operation: (notifier, token) => _builder.RestartSessionAsync(notifier, token),
                    retryAfterRecovery: false,
                    operationCode: "restart_session_scope",
                    loadingOperationKind: RuntimeLoadingOperationKind.RestartSession,
                    splitOperationPerScope: true,
                    cancellationToken: cancellationToken),
                GameContextType.Scene => ExecuteWithRecoveryAsync(
                    operation: (notifier, token) => _builder.LoadSceneAsync(scopeType, notifier, token),
                    retryAfterRecovery: true,
                    operationCode: "reload_scene_scope",
                    loadingOperationKind: RuntimeLoadingOperationKind.ReloadScene,
                    splitOperationPerScope: false,
                    cancellationToken: cancellationToken),
                GameContextType.Module => ReloadScopeModuleAsync(scopeType, cancellationToken),
                GameContextType.Global => throw new ScopeNotRestartableException(scopeType),
                _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported scope type.")
            }).ConfigureAwait(false);
        }

        public Task ReloadScopeAsync<TScope>(CancellationToken cancellationToken = default)
        {
            return ReloadScopeAsync(typeof(TScope), cancellationToken);
        }

        public Task LoadSceneSingleAsync(string sceneName, CancellationToken cancellationToken = default)
        {
            return _sceneLoader.LoadSceneSingleAsync(sceneName, cancellationToken);
        }

        public Task LoadSceneAdditiveAsync(string sceneName, CancellationToken cancellationToken = default)
        {
            return _sceneLoader.LoadSceneAdditiveAsync(sceneName, cancellationToken);
        }

        public async Task GoToAsync(SceneRoute route, CancellationToken cancellationToken = default)
        {
            if (route == null) throw new ArgumentNullException(nameof(route));
            await EvaluateGuardsAsync(RuntimeFlowGuardStage.BeforeNavigation, route.SceneScopeKey, GameContextType.Scene, cancellationToken).ConfigureAwait(false);

            await LoadScopeSceneAsync(route.SceneScopeKey, cancellationToken).ConfigureAwait(false);
            if (route.ModuleScopeKey != null)
                await LoadScopeModuleAsync(route.ModuleScopeKey, cancellationToken).ConfigureAwait(false);

            if (route.LoadSceneAdditively)
                await LoadSceneAdditiveAsync(route.SceneName, cancellationToken).ConfigureAwait(false);
            else
                await LoadSceneSingleAsync(route.SceneName, cancellationToken).ConfigureAwait(false);
        }

        public async Task<SceneRoute> ResolveRouteAsync(
            SceneRoute fallbackRoute,
            ISessionSceneRouteResolver? routeResolver = null,
            CancellationToken cancellationToken = default)
        {
            if (fallbackRoute == null) throw new ArgumentNullException(nameof(fallbackRoute));

            var resolver = routeResolver;
            if (resolver == null
                && _builder.TryResolveFromSession<ISessionSceneRouteResolver>(out var sessionResolver))
            {
                resolver = sessionResolver;
            }

            if (resolver == null)
                return fallbackRoute;

            var route = await resolver
                .DecideNextSceneAsync(new SceneRouteDecisionContext(SessionContext, this), cancellationToken)
                .ConfigureAwait(false);
            return route ?? throw new InvalidOperationException("Scene route resolver returned null route.");
        }

        public TService ResolveSessionService<TService>() where TService : class
        {
            if (!_builder.TryResolveFromSession<TService>(out var service) || service == null)
            {
                throw new InvalidOperationException(
                    $"Session service '{typeof(TService).Name}' is not registered or not available.");
            }

            return service;
        }

        public bool TryResolveSessionService<TService>(out TService? service) where TService : class
        {
            return _builder.TryResolveFromSession(out service);
        }

        public Task PreloadSceneAsync<TSceneScope>(CancellationToken cancellationToken = default)
        {
            return _builder.PreloadSceneAsync(typeof(TSceneScope), cancellationToken: cancellationToken);
        }

        public Task PreloadModuleAsync<TModuleScope>(CancellationToken cancellationToken = default)
        {
            return _builder.PreloadModuleAsync(typeof(TModuleScope), cancellationToken: cancellationToken);
        }

        public bool HasPreloadedScope<TScope>()
        {
            return _builder.HasPreloadedScope(typeof(TScope));
        }

        public Task LoadAdditiveModuleAsync<TModuleScope>(CancellationToken cancellationToken = default)
        {
            return _builder.LoadAdditiveModuleAsync(typeof(TModuleScope), cancellationToken: cancellationToken);
        }

        public Task UnloadAdditiveModuleAsync<TModuleScope>(CancellationToken cancellationToken = default)
        {
            return _builder.UnloadAdditiveModuleAsync(typeof(TModuleScope), cancellationToken);
        }

        private async Task EvaluateGuardsAsync(
            RuntimeFlowGuardStage stage,
            Type? scopeKey,
            GameContextType? targetScopeType,
            CancellationToken cancellationToken)
        {
            if (_guards == null || _guards.Count == 0) return;

            var context = new RuntimeFlowGuardContext(stage, this, scopeKey, targetScopeType);
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

        private async Task ExecuteWithRecoveryAsync(
            Func<IInitializationProgressNotifier, CancellationToken, Task> operation,
            bool retryAfterRecovery,
            string operationCode,
            RuntimeLoadingOperationKind loadingOperationKind,
            bool splitOperationPerScope,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(operationCode))
                throw new ArgumentException("Operation code is required.", nameof(operationCode));

            var maxAttempts = _retryPolicy.Enabled ? Math.Max(1, _retryPolicy.MaxAttempts) : 1;
            var attempt = 0;

            while (true)
            {
                attempt++;
                var operationId = _operationIdFactory(loadingOperationKind);
                var notifier = CreateProgressNotifier(operationId, loadingOperationKind, splitOperationPerScope);
                PublishOperationSnapshot(
                    operationId,
                    loadingOperationKind,
                    RuntimeLoadingOperationStage.Preparing,
                    RuntimeLoadingOperationState.Running,
                    percent: 0d,
                    currentStep: 0,
                    totalSteps: 1,
                    message: $"Executing operation '{operationCode}'.");

                try
                {
                    await operation(notifier, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    PublishOperationSnapshot(
                        operationId,
                        loadingOperationKind,
                        RuntimeLoadingOperationStage.Canceled,
                        RuntimeLoadingOperationState.Canceled,
                        percent: 0d,
                        currentStep: 0,
                        totalSteps: 1,
                        message: $"Operation '{operationCode}' canceled by caller.");
                    throw;
                }
                catch (RuntimeHealthCriticalException critical)
                {
                    PublishOperationSnapshot(
                        operationId,
                        loadingOperationKind,
                        RuntimeLoadingOperationStage.Failed,
                        RuntimeLoadingOperationState.Failed,
                        percent: 0d,
                        currentStep: 0,
                        totalSteps: 1,
                        message: critical.Message,
                        error: critical);
                    var recovered = await TryAutoRecoverSessionAsync(critical, cancellationToken).ConfigureAwait(false);
                    if (!recovered)
                        throw;

                    if (!retryAfterRecovery)
                        return;

                    if (attempt >= maxAttempts)
                        throw;
                }
                catch (Exception ex)
                {
                    PublishOperationSnapshot(
                        operationId,
                        loadingOperationKind,
                        RuntimeLoadingOperationStage.Failed,
                        RuntimeLoadingOperationState.Failed,
                        percent: 0d,
                        currentStep: 0,
                        totalSteps: 1,
                        message: ex.Message,
                        error: ex);
                    var classification = _errorClassifier.Classify(ex);
                    var canRetry = _retryPolicy.Enabled
                                   && classification.IsRetryable
                                   && attempt < maxAttempts;

                    var delay = canRetry
                        ? ComputeRetryDelay(attempt)
                        : TimeSpan.Zero;

                    _retryObserver.OnRetryDecision(new RuntimeRetryDecision(
                        operationCode,
                        attempt,
                        maxAttempts,
                        delay,
                        classification,
                        ex,
                        canRetry));

                    if (!canRetry)
                        throw;

                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private IInitializationProgressNotifier CreateProgressNotifier(
            string operationId,
            RuntimeLoadingOperationKind operationKind,
            bool splitOperationPerScope)
        {
            var loadingNotifier = new RuntimeLoadingProgressNotifierAdapter(
                _loadingProgressObserver,
                operationKind,
                operationId,
                splitOperationPerScope: splitOperationPerScope);
            return new CompositeInitializationProgressNotifier(_progressNotifier, loadingNotifier);
        }

        private void PublishOperationSnapshot(
            string operationId,
            RuntimeLoadingOperationKind operationKind,
            RuntimeLoadingOperationStage stage,
            RuntimeLoadingOperationState state,
            double percent,
            int currentStep,
            int totalSteps,
            string? message,
            Exception? error = null)
        {
            _loadingProgressObserver.OnLoadingProgress(
                new RuntimeLoadingOperationSnapshot(
                    operationId: operationId,
                    operationKind: operationKind,
                    stage: stage,
                    state: state,
                    scopeKey: null,
                    scopeName: null,
                    percent: percent,
                    currentStep: currentStep,
                    totalSteps: totalSteps,
                    message: message,
                    timestampUtc: DateTimeOffset.UtcNow,
                    errorType: error?.GetType().Name,
                    errorMessage: error?.Message));
        }

        private TimeSpan ComputeRetryDelay(int attempt)
        {
            var initialMs = Math.Max(0.0d, _retryPolicy.InitialBackoff.TotalMilliseconds);
            var multiplier = Math.Max(1.0d, _retryPolicy.BackoffMultiplier);
            var maxMs = Math.Max(initialMs, _retryPolicy.MaxBackoff.TotalMilliseconds);
            var exponent = Math.Max(0, attempt - 1);
            var delayMs = initialMs * Math.Pow(multiplier, exponent);
            delayMs = Math.Min(delayMs, maxMs);

            if (_retryPolicy.UseJitter && delayMs > 1.0d)
            {
                var jitterFactor = 0.85d + JitterRandom.NextDouble() * 0.30d;
                delayMs *= jitterFactor;
            }

            return TimeSpan.FromMilliseconds(delayMs);
        }

        private async Task<bool> TryAutoRecoverSessionAsync(
            RuntimeHealthCriticalException critical,
            CancellationToken cancellationToken)
        {
            if (!_builder.CanRestartSession())
                return false;

            var anomaly = new RuntimeHealthAnomaly(
                RuntimeHealthStatus.Critical,
                critical.Scope,
                critical.ServiceType,
                critical.Message,
                critical);

            if (!_healthSupervisor.TryBeginSessionRecovery(anomaly, out _, out _))
                return false;

            _statusObserver?.Invoke(
                RuntimeExecutionState.Recovering,
                $"Recovering session after critical anomaly in '{critical.ServiceType.Name}'.");
            await _builder.RestartSessionAsync(_progressNotifier, cancellationToken).ConfigureAwait(false);
            _statusObserver?.Invoke(
                RuntimeExecutionState.Degraded,
                $"Session recovered after critical anomaly in '{critical.ServiceType.Name}'.");
            return true;
        }

    }
}
