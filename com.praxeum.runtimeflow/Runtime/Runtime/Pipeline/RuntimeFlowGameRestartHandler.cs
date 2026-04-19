using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RuntimeFlow.Contexts;
using UniRx;
using VContainer;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace SFS.Core.GameLoading
{
    public sealed class RuntimeFlowGameRestartHandler : IGameRestartHandler, IDisposable
    {
        private const string SessionRestartStage = "session-restart";
        private const string SessionRestartPreparingReasonCode = "loading.session-restart.preparing";
        private const string SessionRestartResettingReasonCode = "loading.session-restart.resetting";
        private const string SessionRestartReplayingReasonCode = "loading.session-restart.replaying";
        private const string SessionRestartReadyReasonCode = "loading.session-restart.ready";
        private const string SessionRestartFailedReasonCode = "loading.session-restart.failed";
        private const string SessionRestartTimeoutReasonCode = "loading.session-restart.timeout";
        private const string SessionRestartDuplicateReasonCode = "loading.session-restart.duplicate-skipped";
        private const string SessionRestartPipelineMissingReasonCode = "loading.session-restart.pipeline-missing";

        private static readonly TimeSpan RestartReadinessTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan RestartReadinessPollInterval = TimeSpan.FromMilliseconds(100);
        private static readonly RuntimeRestartStageProjectionOptions<string> RestartStageProjectionOptions =
            new(
                stage: SessionRestartStage,
                preparingReasonCode: SessionRestartPreparingReasonCode,
                duplicateReasonCode: SessionRestartDuplicateReasonCode,
                completedReasonCode: SessionRestartReadyReasonCode,
                failedReasonCode: SessionRestartFailedReasonCode,
                timedOutReasonCode: SessionRestartTimeoutReasonCode,
                lifecycleManagerMissingReasonCode: SessionRestartPipelineMissingReasonCode);

        private ILogger _logger;
        private readonly IGameRestartStateSaver _stateSaver;
        private readonly IGameDataCleaner _gameDataCleaner;
        private readonly IRuntimeFlowPipelineProvider _pipelineProvider;
        private readonly IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>> _loadingState;
        private readonly ReactiveProperty<bool> _isApplicationRestarting = new();
        private readonly object _restartGate = new();
        private readonly IRuntimeRestartCoordinator _restartCoordinator;

        [Inject]
        public RuntimeFlowGameRestartHandler(
            IGameRestartStateSaver stateSaver,
            IGameDataCleaner gameDataCleaner,
            IRuntimeFlowPipelineProvider pipelineProvider,
            IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>> loadingState)
            : this(
                stateSaver,
                gameDataCleaner,
                pipelineProvider,
                loadingState,
                logger: null)
        {
        }

        public RuntimeFlowGameRestartHandler(
            IGameRestartStateSaver stateSaver,
            IGameDataCleaner gameDataCleaner,
            IRuntimeFlowPipelineProvider pipelineProvider,
            IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>> loadingState,
            ILogger? logger = null)
        {
            _stateSaver = stateSaver ?? throw new ArgumentNullException(nameof(stateSaver));
            _gameDataCleaner = gameDataCleaner ?? throw new ArgumentNullException(nameof(gameDataCleaner));
            _pipelineProvider = pipelineProvider ?? throw new ArgumentNullException(nameof(pipelineProvider));
            _loadingState = loadingState ?? throw new ArgumentNullException(nameof(loadingState));
            _logger = logger ?? NullLogger<RuntimeFlowGameRestartHandler>.Instance;

            _restartCoordinator = _pipelineProvider.CreateRestartCoordinator(
                runtimeReadinessProvider: BuildReadinessStatus,
                timestampProvider: () => DateTimeOffset.UtcNow);
        }

        [Inject]
        private void InjectLogger(IObjectResolver resolver)
        {
            ConfigureLoggerFromResolver(resolver);
        }

        internal void ConfigureLoggerFromResolver(IObjectResolver resolver)
        {
            _logger = ResolveLogger(resolver, _logger);
        }

        public IReadOnlyReactiveProperty<bool> IsApplicationRestarting => _isApplicationRestarting;

        public void RestartAndClearSecondaryUserData(string reason, bool forceSave = true)
        {
            Restart(reason, forceSave, _gameDataCleaner.ClearSecondaryUserData);
        }

        public void HardRestart(string reason)
        {
            Restart(reason, forceSave: false, _gameDataCleaner.ClearAllUserData);
        }

        public void Restart(string reason, bool forceSave = true, Action? callback = null)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                reason = "restart.requested";
            }

            _logger.LogInformation($"{nameof(Restart)}: create request. Reason: {reason}");
            if (forceSave)
            {
                _stateSaver.SaveAppState();
            }

            lock (_restartGate)
            {
                var dispatch = _restartCoordinator.Dispatch(
                    new RuntimeRestartCoordinatorRequest(
                        restartRequest: new RuntimeRestartRequest(
                            reasonCode: SessionRestartReplayingReasonCode,
                            diagnostic: reason),
                        readinessTimeout: RestartReadinessTimeout,
                        readinessPollInterval: RestartReadinessPollInterval,
                        onBeforeRestartAsync: _ =>
                        {
                            _logger.LogInformation($"{nameof(Restart)}: run. Reason: {reason}");
                            _loadingState.StartStage(
                                SessionRestartStage,
                                SessionRestartResettingReasonCode,
                                reason);
                            callback?.Invoke();
                            _loadingState.StartStage(
                                SessionRestartStage,
                                SessionRestartReplayingReasonCode,
                                reason);
                            return Task.CompletedTask;
                        }));

                if (!RuntimeRestartStageProjector.TryProjectDispatch(
                        dispatch,
                        _loadingState,
                        RestartStageProjectionOptions,
                        reason))
                {
                    LogDuplicateRequest(reason, dispatch.DuplicateReason);
                    return;
                }

                _isApplicationRestarting.Value = true;
                _ = ObserveRestartOutcomeAsync(reason, dispatch.ExecutionTask);
            }
        }

        public void Dispose()
        {
            _isApplicationRestarting.Value = false;
        }

        private async Task ObserveRestartOutcomeAsync(
            string reason,
            Task<RuntimeRestartExecutionResult> executionTask)
        {
            try
            {
                var result = await executionTask.ConfigureAwait(false);
                switch (result.Outcome)
                {
                    case RuntimeRestartExecutionOutcome.Completed:
                        break;
                    case RuntimeRestartExecutionOutcome.LifecycleManagerMissing:
                        _logger.LogError($"{nameof(Restart)}: pipeline restart lifecycle manager is not available. Reason: {reason}");
                        break;
                    case RuntimeRestartExecutionOutcome.TimedOut:
                    {
                        var readiness = result.Readiness;
                        _logger.LogError(
                            $"{nameof(Restart)}: timeout while waiting for restart readiness. Reason: {reason}. BlockingReasonCode: {readiness?.BlockingReasonCode ?? "<none>"}. BlockingReason: {readiness?.BlockingReason ?? "<none>"}");
                        break;
                    }
                    case RuntimeRestartExecutionOutcome.Failed:
                    {
                        var exception = GetRestartFailureException(result);
                        _logger.LogError($"{nameof(Restart)} failed. Reason: {reason}", exception);
                        break;
                    }
                    case RuntimeRestartExecutionOutcome.Deduplicated:
                        LogDuplicateRequest(reason, result.DuplicateReason);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                RuntimeRestartStageProjector.ProjectOutcome(
                    result,
                    _loadingState,
                    RestartStageProjectionOptions,
                    diagnostic: reason,
                    timeoutDiagnosticResolver: timeoutResult => BuildTimeoutDiagnostic(reason, timeoutResult),
                    lifecycleMissingDiagnosticResolver: _ =>
                        $"Pipeline restart lifecycle manager is not available. reason={reason}",
                    failedExceptionResolver: GetRestartFailureException);
            }
            catch (Exception exception)
            {
                _logger.LogError($"{nameof(Restart)} failed. Reason: {reason}", exception);
                _loadingState.FailStage(
                    SessionRestartStage,
                    SessionRestartFailedReasonCode,
                    exception,
                    reason);
            }
            finally
            {
                _isApplicationRestarting.Value = false;
            }
        }

        private void LogDuplicateRequest(string reason, RuntimeRestartDuplicateReason duplicateReason)
        {
            if (duplicateReason == RuntimeRestartDuplicateReason.LifecycleInProgress)
            {
                _logger.LogWarning(
                    $"{nameof(Restart)}: skip duplicate request while RuntimeFlow session recovery is already in progress. Reason: {reason}");
                return;
            }

            _logger.LogWarning(
                $"{nameof(Restart)}: skip duplicate request while restart is already in progress. Reason: {reason}");
        }

        private RuntimeReadinessStatus BuildReadinessStatus()
        {
            return _pipelineProvider.HasCurrent
                ? _pipelineProvider.GetRuntimeReadinessStatus()
                : new RuntimeReadinessStatus(
                    isReady: false,
                    updatedAtUtc: DateTimeOffset.UtcNow,
                    blockingReasonCode: SessionRestartPipelineMissingReasonCode,
                    blockingReason: "RuntimeFlow pipeline is not available for replay restart.");
        }

        private static string BuildTimeoutDiagnostic(string reason, RuntimeRestartExecutionResult result)
        {
            var readiness = result.Readiness;
            return
                $"RuntimeFlow restart timed out while waiting readiness. reason={reason}; blockingCode={readiness?.BlockingReasonCode ?? "<none>"}; blockingReason={readiness?.BlockingReason ?? "<none>"}";
        }

        private static Exception GetRestartFailureException(RuntimeRestartExecutionResult result)
        {
            return result.Exception ?? new InvalidOperationException("RuntimeFlow restart failed.");
        }

        private static ILogger ResolveLogger(IObjectResolver resolver, ILogger? fallbackLogger)
        {
            if (resolver != null)
            {
                if (resolver.TryResolve(typeof(ILoggerFactory), out var loggerFactoryInstance)
                    && loggerFactoryInstance is ILoggerFactory loggerFactory)
                {
                    return loggerFactory.CreateLogger<RuntimeFlowGameRestartHandler>();
                }

                if (resolver.TryResolve(typeof(ILogger<RuntimeFlowGameRestartHandler>), out var typedLoggerInstance)
                    && typedLoggerInstance is ILogger typedLogger)
                {
                    return typedLogger;
                }

                if (resolver.TryResolve(typeof(ILogger), out var loggerInstance)
                    && loggerInstance is ILogger logger)
                {
                    return logger;
                }
            }

            return fallbackLogger ?? NullLogger<RuntimeFlowGameRestartHandler>.Instance;
        }

    }
}
