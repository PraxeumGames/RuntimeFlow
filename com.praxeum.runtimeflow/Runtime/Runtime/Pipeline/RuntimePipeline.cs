using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using RuntimeFlow.Errors;
using RuntimeFlow.Flow;
using RuntimeFlow.Health;
using RuntimeFlow.Loading;
using RuntimeFlow.Status;
using RuntimeFlow.Transitions;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Pipeline
{
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
        private readonly PipelineStatusService _statusService;
        private readonly PipelineOperationExecutor _operationExecutor;
        private long _transitionOperationGeneration;
        private IScopeTransitionHandler _transitionHandler = NullScopeTransitionHandler.Instance;
        private IReadOnlyList<IRuntimeFlowGuard>? _guards;
        private IRuntimeFlowScenario? _flow;
        private IGameSceneLoader? _sceneLoader;
        private readonly RuntimeReadinessGate _restartReadinessGate;
        private readonly RuntimeRestartLifecycleManager _restartLifecycleManager;
        private bool _disposed;

        public static RuntimePipeline? ActivePipeline { get; internal set; }

        internal GameContextBuilder Builder => _builder;
        internal RuntimeHealthSupervisor HealthSupervisor => _healthSupervisor;
        internal IRuntimeFlowScenario? FlowScenario => _flow;
        internal IGameSceneLoader? SceneLoader => _sceneLoader;
        internal PipelineStatusService StatusService => _statusService;

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
            _guards = ComposeGuardsWithRestartPreparationHooks(guards: null, hooks: _sessionRestartPreparationHooks);
            _logger = logger;
            var initialStatus = new RuntimeStatus(RuntimeExecutionState.ColdStart, DateTimeOffset.UtcNow, RuntimeOperationCodes.ColdStart, "Pipeline is created and not initialized yet.", RuntimeOperationCodes.ColdStart);
            _statusService = new PipelineStatusService(initialStatus);
            _operationExecutor = new PipelineOperationExecutor(loadingProgressObserver, defaultProgressNotifier, _statusService, InvalidateTransitionOperations, logger);
            _restartReadinessGate = new RuntimeReadinessGate(GetReadinessStatus, () => _statusService.GetExecutionContext(), null, () => DateTimeOffset.UtcNow);
            _restartLifecycleManager = new RuntimeRestartLifecycleManager((request, ct) => RestartSessionAsync(cancellationToken: ct), null, _restartReadinessGate, null, _statusService.ExecutionContextManager, this, () => DateTimeOffset.UtcNow);
            _builder.OnSessionInitialized(context => SessionContextInitialized?.Invoke(context));
        }

        private void SetStatus(RuntimeExecutionState state, string? operationCode = null, string? message = null, Exception? error = null) => _statusService.SetStatus(state, operationCode, message, error);
        public RuntimeStatus GetRuntimeStatus() => _statusService.GetStatus();
        public IRuntimeExecutionContext GetExecutionContext() => _statusService.GetExecutionContext();
    }
}
