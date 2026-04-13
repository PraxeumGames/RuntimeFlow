using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

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
    }
}
