using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    public interface IRuntimeRestartCoordinator
    {
        RuntimeRestartDispatch Dispatch(RuntimeRestartCoordinatorRequest request);
    }

    public enum RuntimeRestartDispatchKind
    {
        Started = 0,
        Deduplicated = 1
    }

    public enum RuntimeRestartDuplicateReason
    {
        None = 0,
        InFlight = 1,
        LifecycleInProgress = 2
    }

    public enum RuntimeRestartExecutionOutcome
    {
        Completed = 0,
        TimedOut = 1,
        LifecycleManagerMissing = 2,
        Failed = 3,
        Deduplicated = 4
    }

    public readonly struct RuntimeRestartDispatch
    {
        private RuntimeRestartDispatch(
            RuntimeRestartDispatchKind kind,
            RuntimeRestartDuplicateReason duplicateReason,
            Task<RuntimeRestartExecutionResult> executionTask)
        {
            Kind = kind;
            DuplicateReason = duplicateReason;
            ExecutionTask = executionTask ?? throw new ArgumentNullException(nameof(executionTask));
        }

        public RuntimeRestartDispatchKind Kind { get; }
        public RuntimeRestartDuplicateReason DuplicateReason { get; }
        public Task<RuntimeRestartExecutionResult> ExecutionTask { get; }
        public bool IsAccepted => Kind == RuntimeRestartDispatchKind.Started;

        public static RuntimeRestartDispatch Started(Task<RuntimeRestartExecutionResult> executionTask)
        {
            return new RuntimeRestartDispatch(
                RuntimeRestartDispatchKind.Started,
                RuntimeRestartDuplicateReason.None,
                executionTask);
        }

        public static RuntimeRestartDispatch Deduplicated(
            RuntimeRestartDuplicateReason duplicateReason,
            Task<RuntimeRestartExecutionResult> executionTask)
        {
            return new RuntimeRestartDispatch(
                RuntimeRestartDispatchKind.Deduplicated,
                duplicateReason,
                executionTask);
        }
    }

    public sealed class RuntimeRestartCoordinatorRequest
    {
        public RuntimeRestartCoordinatorRequest(
            RuntimeRestartRequest restartRequest,
            TimeSpan readinessTimeout,
            TimeSpan? readinessPollInterval = null,
            Func<CancellationToken, Task> onBeforeRestartAsync = null)
        {
            if (restartRequest == null)
                throw new ArgumentNullException(nameof(restartRequest));
            if (readinessTimeout != Timeout.InfiniteTimeSpan && readinessTimeout < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(readinessTimeout), readinessTimeout, "Readiness timeout must be positive or infinite.");
            if (readinessPollInterval.HasValue && readinessPollInterval.Value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(readinessPollInterval), readinessPollInterval, "Readiness poll interval must be positive.");

            RestartRequest = restartRequest;
            ReadinessTimeout = readinessTimeout;
            ReadinessPollInterval = readinessPollInterval;
            OnBeforeRestartAsync = onBeforeRestartAsync;
        }

        public RuntimeRestartRequest RestartRequest { get; }
        public TimeSpan ReadinessTimeout { get; }
        public TimeSpan? ReadinessPollInterval { get; }
        public Func<CancellationToken, Task> OnBeforeRestartAsync { get; }
    }

    public sealed class RuntimeRestartExecutionResult
    {
        private RuntimeRestartExecutionResult(
            RuntimeRestartExecutionOutcome outcome,
            RuntimeRestartRequest request,
            RuntimeRestartDuplicateReason duplicateReason = RuntimeRestartDuplicateReason.None,
            RuntimeRestartReadiness readiness = null,
            Exception exception = null)
        {
            Outcome = outcome;
            Request = request;
            DuplicateReason = duplicateReason;
            Readiness = readiness;
            Exception = exception;
        }

        public RuntimeRestartExecutionOutcome Outcome { get; }
        public RuntimeRestartRequest Request { get; }
        public RuntimeRestartDuplicateReason DuplicateReason { get; }
        public RuntimeRestartReadiness Readiness { get; }
        public Exception Exception { get; }

        public static RuntimeRestartExecutionResult Completed(RuntimeRestartRequest request)
            => new RuntimeRestartExecutionResult(RuntimeRestartExecutionOutcome.Completed, request);

        public static RuntimeRestartExecutionResult Deduplicated(
            RuntimeRestartRequest request,
            RuntimeRestartDuplicateReason duplicateReason)
            => new RuntimeRestartExecutionResult(
                RuntimeRestartExecutionOutcome.Deduplicated,
                request,
                duplicateReason: duplicateReason);

        public static RuntimeRestartExecutionResult TimedOut(
            RuntimeRestartRequest request,
            RuntimeRestartReadiness readiness)
            => new RuntimeRestartExecutionResult(
                RuntimeRestartExecutionOutcome.TimedOut,
                request,
                readiness: readiness);

        public static RuntimeRestartExecutionResult LifecycleManagerMissing(RuntimeRestartRequest request)
            => new RuntimeRestartExecutionResult(RuntimeRestartExecutionOutcome.LifecycleManagerMissing, request);

        public static RuntimeRestartExecutionResult Failed(
            RuntimeRestartRequest request,
            Exception exception)
            => new RuntimeRestartExecutionResult(
                RuntimeRestartExecutionOutcome.Failed,
                request,
                exception: exception);
    }

    public sealed class RuntimeRestartStageProjectionOptions<TStage>
    {
        public RuntimeRestartStageProjectionOptions(
            TStage stage,
            string preparingReasonCode,
            string duplicateReasonCode,
            string completedReasonCode,
            string failedReasonCode,
            string timedOutReasonCode,
            string lifecycleManagerMissingReasonCode)
        {
            if (ReferenceEquals(stage, null))
                throw new ArgumentNullException(nameof(stage));
            if (string.IsNullOrWhiteSpace(failedReasonCode))
                throw new ArgumentException("Failed reason code is required.", nameof(failedReasonCode));
            if (string.IsNullOrWhiteSpace(timedOutReasonCode))
                throw new ArgumentException("Timed out reason code is required.", nameof(timedOutReasonCode));
            if (string.IsNullOrWhiteSpace(lifecycleManagerMissingReasonCode))
            {
                throw new ArgumentException(
                    "Lifecycle manager missing reason code is required.",
                    nameof(lifecycleManagerMissingReasonCode));
            }

            Stage = stage;
            PreparingReasonCode = Normalize(preparingReasonCode);
            DuplicateReasonCode = Normalize(duplicateReasonCode);
            CompletedReasonCode = Normalize(completedReasonCode);
            FailedReasonCode = Normalize(failedReasonCode);
            TimedOutReasonCode = Normalize(timedOutReasonCode);
            LifecycleManagerMissingReasonCode = Normalize(lifecycleManagerMissingReasonCode);
        }

        public TStage Stage { get; }
        public string PreparingReasonCode { get; }
        public string DuplicateReasonCode { get; }
        public string CompletedReasonCode { get; }
        public string FailedReasonCode { get; }
        public string TimedOutReasonCode { get; }
        public string LifecycleManagerMissingReasonCode { get; }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public static class RuntimeRestartStageProjector
    {
        public static bool TryProjectDispatch<TStage, TSnapshot>(
            RuntimeRestartDispatch dispatch,
            IRuntimePipelineStageStateProvider<TStage, TSnapshot> stageStateProvider,
            RuntimeRestartStageProjectionOptions<TStage> options,
            string diagnostic = null)
        {
            if (stageStateProvider == null)
                throw new ArgumentNullException(nameof(stageStateProvider));
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            if (!dispatch.IsAccepted)
            {
                stageStateProvider.StartStage(options.Stage, options.DuplicateReasonCode, diagnostic);
                return false;
            }

            stageStateProvider.StartStage(options.Stage, options.PreparingReasonCode, diagnostic);
            return true;
        }

        public static void ProjectOutcome<TStage, TSnapshot>(
            RuntimeRestartExecutionResult result,
            IRuntimePipelineStageStateProvider<TStage, TSnapshot> stageStateProvider,
            RuntimeRestartStageProjectionOptions<TStage> options,
            string diagnostic = null,
            Func<RuntimeRestartExecutionResult, string> timeoutDiagnosticResolver = null,
            Func<RuntimeRestartExecutionResult, string> lifecycleMissingDiagnosticResolver = null,
            Func<RuntimeRestartExecutionResult, Exception> failedExceptionResolver = null)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));
            if (stageStateProvider == null)
                throw new ArgumentNullException(nameof(stageStateProvider));
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            switch (result.Outcome)
            {
                case RuntimeRestartExecutionOutcome.Completed:
                    stageStateProvider.CompleteStage(options.Stage, options.CompletedReasonCode, diagnostic);
                    break;
                case RuntimeRestartExecutionOutcome.LifecycleManagerMissing:
                    stageStateProvider.FailStage(
                        options.Stage,
                        options.LifecycleManagerMissingReasonCode,
                        diagnostic: lifecycleMissingDiagnosticResolver?.Invoke(result) ?? diagnostic);
                    break;
                case RuntimeRestartExecutionOutcome.TimedOut:
                    stageStateProvider.Stop(
                        options.TimedOutReasonCode,
                        timeoutDiagnosticResolver?.Invoke(result) ?? diagnostic);
                    break;
                case RuntimeRestartExecutionOutcome.Failed:
                    stageStateProvider.FailStage(
                        options.Stage,
                        options.FailedReasonCode,
                        failedExceptionResolver?.Invoke(result) ?? result.Exception,
                        diagnostic);
                    break;
                case RuntimeRestartExecutionOutcome.Deduplicated:
                    stageStateProvider.StartStage(options.Stage, options.DuplicateReasonCode, diagnostic);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    public sealed class RuntimeRestartCoordinator : IRuntimeRestartCoordinator
    {
        private readonly object _sync = new object();
        private readonly Func<IRuntimeReadinessGate> _readinessGateFactory;
        private readonly Func<IRuntimeRestartLifecycleManager> _restartLifecycleManagerProvider;
        private Task<RuntimeRestartExecutionResult> _inFlightTask;
        private long _inFlightGeneration;

        public RuntimeRestartCoordinator(
            Func<IRuntimeReadinessGate> readinessGateFactory,
            Func<IRuntimeRestartLifecycleManager> restartLifecycleManagerProvider)
        {
            _readinessGateFactory = readinessGateFactory ?? throw new ArgumentNullException(nameof(readinessGateFactory));
            _restartLifecycleManagerProvider = restartLifecycleManagerProvider ?? throw new ArgumentNullException(nameof(restartLifecycleManagerProvider));
        }

        public RuntimeRestartDispatch Dispatch(RuntimeRestartCoordinatorRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            lock (_sync)
            {
                if (_inFlightTask != null && !_inFlightTask.IsCompleted)
                {
                    return RuntimeRestartDispatch.Deduplicated(
                        RuntimeRestartDuplicateReason.InFlight,
                        _inFlightTask);
                }

                var restartLifecycleManager = _restartLifecycleManagerProvider();
                if (restartLifecycleManager != null && restartLifecycleManager.Snapshot.IsInProgress)
                {
                    return RuntimeRestartDispatch.Deduplicated(
                        RuntimeRestartDuplicateReason.LifecycleInProgress,
                        Task.FromResult(
                            RuntimeRestartExecutionResult.Deduplicated(
                                request.RestartRequest,
                                RuntimeRestartDuplicateReason.LifecycleInProgress)));
                }

                var generation = ++_inFlightGeneration;
                _inFlightTask = ExecuteInternalAsync(request, generation);
                return RuntimeRestartDispatch.Started(_inFlightTask);
            }
        }

        private async Task<RuntimeRestartExecutionResult> ExecuteInternalAsync(
            RuntimeRestartCoordinatorRequest request,
            long generation)
        {
            IRuntimeReadinessGate readinessGate = null;
            var readinessWaitCompleted = false;

            try
            {
                readinessGate = _readinessGateFactory();
                if (readinessGate != null)
                {
                    await readinessGate
                        .WaitUntilReadyAsync(
                            request.ReadinessTimeout,
                            request.ReadinessPollInterval,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }

                readinessWaitCompleted = true;

                var restartLifecycleManager = _restartLifecycleManagerProvider();
                if (restartLifecycleManager == null)
                {
                    return RuntimeRestartExecutionResult.LifecycleManagerMissing(request.RestartRequest);
                }

                if (request.OnBeforeRestartAsync != null)
                {
                    await request.OnBeforeRestartAsync(CancellationToken.None).ConfigureAwait(false);
                }

                await restartLifecycleManager
                    .RestartAsync(request.RestartRequest, CancellationToken.None)
                    .ConfigureAwait(false);
                return RuntimeRestartExecutionResult.Completed(request.RestartRequest);
            }
            catch (OperationCanceledException) when (!readinessWaitCompleted)
            {
                return RuntimeRestartExecutionResult.TimedOut(
                    request.RestartRequest,
                    readinessGate?.GetRestartReadiness());
            }
            catch (Exception ex)
            {
                return RuntimeRestartExecutionResult.Failed(request.RestartRequest, ex);
            }
            finally
            {
                lock (_sync)
                {
                    if (_inFlightGeneration == generation)
                    {
                        _inFlightTask = null;
                    }
                }
            }
        }
    }
}
