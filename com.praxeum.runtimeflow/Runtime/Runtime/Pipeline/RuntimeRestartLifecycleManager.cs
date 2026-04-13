using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Default restart lifecycle orchestration with:
    /// - readiness gate validation
    /// - guard evaluation
    /// - in-flight deduplication
    /// - completion/failure tracking
    /// </summary>
    public sealed class RuntimeRestartLifecycleManager : IRuntimeRestartLifecycleManager
    {
        private readonly object _sync = new object();
        private readonly Func<DateTimeOffset> _timestampProvider;
        private readonly IRuntimeReadinessGate _readinessGate;
        private readonly IRuntimeRestartGuard _guard;
        private readonly IRuntimeExecutionContextProvider _executionContextProvider;
        private readonly IRuntimePipelineStateQuery _pipelineStateQuery;
        private readonly Func<RuntimeRestartRequest, CancellationToken, Task> _restartOperation;
        private readonly Func<RuntimeRestartRequest, CancellationToken, Task> _replayOperation;

        private RuntimeRestartLifecycleSnapshot _snapshot;
        private Task _inFlightRestartTask;
        private long _completedCount;
        private long _failedCount;
        private long _deduplicatedRequestCount;
        private DateTimeOffset _lastCompletedAtUtc;

        public RuntimeRestartLifecycleManager(
            Func<RuntimeRestartRequest, CancellationToken, Task> restartOperation,
            Func<RuntimeRestartRequest, CancellationToken, Task> replayOperation = null,
            IRuntimeReadinessGate readinessGate = null,
            IRuntimeRestartGuard guard = null,
            IRuntimeExecutionContextProvider executionContextProvider = null,
            IRuntimePipelineStateQuery pipelineStateQuery = null,
            Func<DateTimeOffset> timestampProvider = null)
        {
            _restartOperation = restartOperation ?? throw new ArgumentNullException(nameof(restartOperation));
            _replayOperation = replayOperation;
            _readinessGate = readinessGate ?? new RuntimeReadinessGate();
            _guard = guard;
            _executionContextProvider = executionContextProvider;
            _pipelineStateQuery = pipelineStateQuery;
            _timestampProvider = timestampProvider ?? (() => DateTimeOffset.UtcNow);
            _snapshot = new RuntimeRestartLifecycleSnapshot(RuntimeRestartLifecycleStage.Idle, _timestampProvider());
        }

        public RuntimeRestartLifecycleSnapshot Snapshot
        {
            get
            {
                lock (_sync)
                {
                    return _snapshot;
                }
            }
        }

        public RuntimeRestartReadiness GetRestartReadiness()
        {
            return GetRestartReadiness(includeInFlightCheck: true);
        }

        public Task RestartAsync(RuntimeRestartRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            Task taskToAwait;
            lock (_sync)
            {
                if (_inFlightRestartTask != null && !_inFlightRestartTask.IsCompleted)
                {
                    _deduplicatedRequestCount++;
                    taskToAwait = _inFlightRestartTask;
                }
                else
                {
                    taskToAwait = RunRestartInternalAsync(request, cancellationToken);
                    _inFlightRestartTask = taskToAwait;
                }
            }

            return WaitWithCancellationAsync(taskToAwait, cancellationToken);
        }

        private async Task RunRestartInternalAsync(RuntimeRestartRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                UpdateSnapshot(RuntimeRestartLifecycleStage.WaitingReadiness, request.ReasonCode, request.Diagnostic);
                var restartReadiness = GetRestartReadiness(includeInFlightCheck: false);
                if (!restartReadiness.IsReady)
                {
                    throw new InvalidOperationException(
                        restartReadiness.BlockingReason
                        ?? $"Restart is blocked by '{restartReadiness.BlockingReasonCode ?? "unknown"}'.");
                }

                UpdateSnapshot(RuntimeRestartLifecycleStage.GuardValidation, request.ReasonCode, request.Diagnostic);
                if (_guard != null)
                {
                    var guardResult = await _guard
                        .EvaluateAsync(CreateGuardContext(request, restartReadiness), cancellationToken)
                        .ConfigureAwait(false);
                    if (!guardResult.IsAllowed)
                    {
                        throw new RuntimeFlowGuardFailedException(
                            RuntimeFlowGuardStage.BeforeSessionRestart,
                            guardResult.ReasonCode ?? "restart.guard.denied",
                            guardResult.Reason);
                    }
                }

                UpdateSnapshot(RuntimeRestartLifecycleStage.Restarting, request.ReasonCode, request.Diagnostic);
                await _restartOperation(request, cancellationToken).ConfigureAwait(false);

                if (_replayOperation != null)
                {
                    UpdateSnapshot(RuntimeRestartLifecycleStage.ReplayingFlow, request.ReasonCode, request.Diagnostic);
                    await _replayOperation(request, cancellationToken).ConfigureAwait(false);
                }

                lock (_sync)
                {
                    _completedCount++;
                    _lastCompletedAtUtc = _timestampProvider();
                    _snapshot = new RuntimeRestartLifecycleSnapshot(
                        RuntimeRestartLifecycleStage.Completed,
                        _lastCompletedAtUtc,
                        reasonCode: request.ReasonCode ?? "restart.completed",
                        diagnostic: BuildCompletionDiagnostic());
                }
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _failedCount++;
                    _snapshot = new RuntimeRestartLifecycleSnapshot(
                        RuntimeRestartLifecycleStage.Failed,
                        _timestampProvider(),
                        reasonCode: request.ReasonCode ?? "restart.failed",
                        diagnostic: request.Diagnostic,
                        errorType: ex.GetType().Name,
                        errorMessage: ex.Message);
                }

                throw;
            }
            finally
            {
                lock (_sync)
                {
                    _inFlightRestartTask = null;
                }
            }
        }

        private RuntimeRestartReadiness GetRestartReadiness(bool includeInFlightCheck)
        {
            var baseReadiness = _readinessGate.GetRestartReadiness();
            if (!includeInFlightCheck)
                return baseReadiness;

            lock (_sync)
            {
                if (_inFlightRestartTask != null && !_inFlightRestartTask.IsCompleted)
                {
                    return new RuntimeRestartReadiness(
                        isReady: false,
                        updatedAtUtc: _timestampProvider(),
                        blockingReasonCode: "restart.in_flight",
                        blockingReason: "Restart request is already in progress.");
                }
            }

            return baseReadiness;
        }

        private RuntimeRestartGuardContext CreateGuardContext(
            RuntimeRestartRequest request,
            RuntimeRestartReadiness readiness)
        {
            return new RuntimeRestartGuardContext(
                request: request,
                snapshot: Snapshot,
                executionContext: _executionContextProvider?.GetExecutionContext(),
                runtimeStatus: _pipelineStateQuery?.GetRuntimeStatus(),
                runtimeReadinessStatus: _pipelineStateQuery?.GetReadinessStatus(),
                restartReadiness: readiness);
        }

        private void UpdateSnapshot(
            RuntimeRestartLifecycleStage stage,
            string reasonCode,
            string diagnostic)
        {
            lock (_sync)
            {
                _snapshot = new RuntimeRestartLifecycleSnapshot(
                    stage,
                    _timestampProvider(),
                    reasonCode,
                    diagnostic);
            }
        }

        private string BuildCompletionDiagnostic()
        {
            return $"completed={_completedCount};failed={_failedCount};deduplicated={_deduplicatedRequestCount};lastCompletedAt={_lastCompletedAtUtc:O}";
        }

        private static async Task WaitWithCancellationAsync(Task task, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return;
            }

            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var completedTask = await Task.WhenAny(task, cancellationTask).ConfigureAwait(false);
            if (completedTask == cancellationTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            await task.ConfigureAwait(false);
        }
    }
}
