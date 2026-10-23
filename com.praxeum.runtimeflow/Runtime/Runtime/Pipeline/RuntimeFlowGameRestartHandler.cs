using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RuntimeFlow.Contexts;
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
        private readonly object _restartGate = new();
        private readonly IRuntimeRestartCoordinator _restartCoordinator;
        private bool _isApplicationRestarting;

        // The restart this handler started and that has not finished yet, and the one that arrived
        // during it. A request during a restart supersedes it: the session being replayed may be
        // waiting on the very initializer that asked for the new restart, so skipping the request
        // as a duplicate would leave that replay hanging.
        private CancellationTokenSource? _inFlightCancellation;
        private PendingRestart? _pendingRestart;

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

        public bool IsApplicationRestarting => _isApplicationRestarting;
        public event Action<bool>? ApplicationRestartingChanged;

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

            CancellationTokenSource? superseded;
            lock (_restartGate)
            {
                if (_inFlightCancellation == null)
                {
                    if (!TryDispatchLocked(reason, callback, out var duplicateReason))
                        LogDuplicateRequest(reason, duplicateReason);
                    return;
                }

                _logger.LogInformation(
                    $"{nameof(Restart)}: supersedes the restart in progress. Reason: {reason}");
                _pendingRestart = new PendingRestart(reason, callback);
                superseded = _inFlightCancellation;
            }

            // Outside the gate: the superseded restart may finish synchronously and dispatch the pending one
            try
            {
                superseded.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It finished on its own meanwhile and has already taken the pending request
            }
        }

        private bool TryDispatchLocked(string reason, Action? callback, out RuntimeRestartDuplicateReason duplicateReason)
        {
            var cancellation = new CancellationTokenSource();
            var dispatch = _restartCoordinator.Dispatch(
                new RuntimeRestartCoordinatorRequest(
                    restartRequest: new RuntimeRestartRequest(
                        reasonCode: SessionRestartReplayingReasonCode,
                        diagnostic: reason,
                        allowBeforeReady: IsRuntimeInitializing()),
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
                    },
                    cancellationToken: cancellation.Token));

            if (!RuntimeRestartStageProjector.TryProjectDispatch(
                    dispatch,
                    _loadingState,
                    RestartStageProjectionOptions,
                    reason))
            {
                cancellation.Dispose();
                duplicateReason = dispatch.DuplicateReason;
                return false;
            }

            duplicateReason = RuntimeRestartDuplicateReason.None;
            _inFlightCancellation = cancellation;
            SetApplicationRestarting(true);
            _ = ObserveRestartOutcomeAsync(reason, dispatch.ExecutionTask, cancellation);
            return true;
        }

        public void Dispose()
        {
            SetApplicationRestarting(false);
        }

        private async Task ObserveRestartOutcomeAsync(
            string reason,
            Task<RuntimeRestartExecutionResult> executionTask,
            CancellationTokenSource cancellation)
        {
            try
            {
                var result = await executionTask.ConfigureAwait(false);
                if (cancellation.IsCancellationRequested && result.Outcome != RuntimeRestartExecutionOutcome.Completed)
                {
                    _logger.LogInformation($"{nameof(Restart)}: superseded by a newer restart. Reason: {reason}");
                    return;
                }

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
                OnRestartFinished(cancellation);
            }
        }

        private void OnRestartFinished(CancellationTokenSource cancellation)
        {
            PendingRestart? pending;
            lock (_restartGate)
            {
                if (_inFlightCancellation == cancellation)
                    _inFlightCancellation = null;
                cancellation.Dispose();

                pending = _pendingRestart;
                _pendingRestart = null;
            }

            if (pending is { } superseding)
            {
                _ = RunSupersedingRestartAsync(superseding);
                return;
            }

            SetApplicationRestarting(false);
        }

        /// <summary>
        /// The superseded restart reports cancellation as soon as its token fires, while the replay it
        /// cancelled may still be unwinding: the coordinator then refuses with LifecycleInProgress for a
        /// moment. The superseding restart waits that out instead of being skipped.
        /// </summary>
        private async Task RunSupersedingRestartAsync(PendingRestart pending)
        {
            _logger.LogInformation($"{nameof(Restart)}: run the superseding restart. Reason: {pending.Reason}");
            var deadline = DateTimeOffset.UtcNow + RestartReadinessTimeout;
            while (true)
            {
                RuntimeRestartDuplicateReason duplicateReason;
                lock (_restartGate)
                {
                    // Another request has started meanwhile: it is the latest and runs instead
                    if (_inFlightCancellation != null)
                        return;

                    if (TryDispatchLocked(pending.Reason, pending.Callback, out duplicateReason))
                        return;
                }

                if (duplicateReason != RuntimeRestartDuplicateReason.LifecycleInProgress
                    || DateTimeOffset.UtcNow >= deadline)
                {
                    LogDuplicateRequest(pending.Reason, duplicateReason);
                    SetApplicationRestarting(false);
                    return;
                }

                await Task.Delay(RestartReadinessPollInterval).ConfigureAwait(false);
            }
        }

        private readonly struct PendingRestart
        {
            public PendingRestart(string reason, Action? callback)
            {
                Reason = reason;
                Callback = callback;
            }

            public string Reason { get; }
            public Action? Callback { get; }
        }

        private void SetApplicationRestarting(bool value)
        {
            if (_isApplicationRestarting == value)
            {
                return;
            }

            _isApplicationRestarting = value;
            ApplicationRestartingChanged?.Invoke(value);
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

        private bool IsRuntimeInitializing()
        {
            return _pipelineProvider.TryGetCurrent(out var pipeline)
                   && pipeline != null
                   && pipeline.GetRuntimeStatus().State == RuntimeExecutionState.Initializing;
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
