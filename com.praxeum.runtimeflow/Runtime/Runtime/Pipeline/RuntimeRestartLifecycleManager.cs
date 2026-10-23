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
        private readonly IRuntimeRestartGuard? _guard;
        private readonly IRuntimeExecutionContextProvider? _executionContextProvider;
        private readonly IRuntimePipelineStateQuery? _pipelineStateQuery;
        private readonly Func<RuntimeRestartRequest, CancellationToken, Task> _restartOperation;
        private readonly Func<RuntimeRestartRequest, CancellationToken, Task>? _replayOperation;

        private RuntimeRestartLifecycleSnapshot _snapshot;
        private Task? _inFlightRestartTask;
        private Task? _latestRestartTask;
        private long _restartGeneration;
        private TaskCompletionSource<bool>? _currentRestartCompletion;
        private long _completedCount;
        private long _failedCount;
        private long _deduplicatedRequestCount;
        private DateTimeOffset _lastCompletedAtUtc;

        public RuntimeRestartLifecycleManager(
            Func<RuntimeRestartRequest, CancellationToken, Task> restartOperation,
            Func<RuntimeRestartRequest, CancellationToken, Task>? replayOperation = null,
            IRuntimeReadinessGate? readinessGate = null,
            IRuntimeRestartGuard? guard = null,
            IRuntimeExecutionContextProvider? executionContextProvider = null,
            IRuntimePipelineStateQuery? pipelineStateQuery = null,
            Func<DateTimeOffset>? timestampProvider = null)
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

        /// <summary>Grows with every restart run, a superseding one included.</summary>
        internal long RestartGeneration
        {
            get
            {
                lock (_sync)
                {
                    return _restartGeneration;
                }
            }
        }

        internal Task? GetLatestRestartTask()
        {
            lock (_sync)
            {
                return _inFlightRestartTask ?? _latestRestartTask;
            }
        }

        public Task RestartAsync(RuntimeRestartRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            Task taskToAwait;
            lock (_sync)
            {
                // Only a restart that already rebuilds the session is replaced; one still waiting for readiness
                // or its guard absorbs the request, so the readiness check keeps holding
                if (!_snapshot.IsRebuildingSession)
                    request = request.WithoutSupersede();

                if (_inFlightRestartTask != null && !_inFlightRestartTask.IsCompleted && !request.SupersedesInFlight)
                {
                    _deduplicatedRequestCount++;
                    taskToAwait = _inFlightRestartTask;
                }
                else
                {
                    // The completion exists before the run starts: a run it supersedes may end inline while
                    // this one is starting and has to find what to wait for
                    var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _currentRestartCompletion = completion;
                    taskToAwait = RunRestartInternalAsync(request, ++_restartGeneration, completion, cancellationToken);
                    _inFlightRestartTask = taskToAwait;
                    _latestRestartTask = taskToAwait;
                }
            }

            return WaitWithCancellationAsync(taskToAwait, cancellationToken);
        }

        private async Task RunRestartInternalAsync(
            RuntimeRestartRequest request,
            long generation,
            TaskCompletionSource<bool> completion,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled();
                cancellationToken.ThrowIfCancellationRequested();
            }

            Task? superseded = null;
            try
            {
                UpdateSnapshot(generation, RuntimeRestartLifecycleStage.WaitingReadiness, request.ReasonCode, request.Diagnostic);
                var restartReadiness = GetRestartReadiness(includeInFlightCheck: false);
                if (!restartReadiness.IsReady && !CanRestartBeforeReady(request) && !request.SupersedesInFlight)
                {
                    throw new InvalidOperationException(
                        restartReadiness.BlockingReason
                        ?? $"Restart is blocked by '{restartReadiness.BlockingReasonCode ?? "unknown"}'.");
                }

                UpdateSnapshot(generation, RuntimeRestartLifecycleStage.GuardValidation, request.ReasonCode, request.Diagnostic);
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

                    if (await EndWithSupersedingRestartAsync(generation, completion).ConfigureAwait(false))
                        return;
                }

                UpdateSnapshot(generation, RuntimeRestartLifecycleStage.Restarting, request.ReasonCode, request.Diagnostic);
                await _restartOperation(request, cancellationToken).ConfigureAwait(false);
                if (await EndWithSupersedingRestartAsync(generation, completion).ConfigureAwait(false))
                    return;

                if (_replayOperation != null)
                {
                    UpdateSnapshot(generation, RuntimeRestartLifecycleStage.ReplayingFlow, request.ReasonCode, request.Diagnostic);
                    await _replayOperation(request, cancellationToken).ConfigureAwait(false);
                }

                lock (_sync)
                {
                    // Checked together with the write: a newer restart owns the snapshot from its very start
                    if (_restartGeneration != generation)
                    {
                        superseded = _currentRestartCompletion!.Task;
                    }
                    else
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

                if (superseded != null)
                    await superseded.ConfigureAwait(false);

                completion.TrySetResult(true);
            }
            catch (Exception) when (TryGetSupersedingRestart(generation, out var superseding))
            {
                // A newer restart abandoned this one's rebuild: this request is done when that one is
                try
                {
                    await superseding.ConfigureAwait(false);
                    completion.TrySetResult(true);
                }
                catch (Exception supersedingFailure)
                {
                    completion.TrySetException(supersedingFailure);
                    throw;
                }
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
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
                    if (_restartGeneration == generation)
                        _inFlightRestartTask = null;
                }
            }
        }

        /// <summary>
        /// Ends this run together with the restart that superseded it, if any: that one now rebuilds the session
        /// and owns the snapshot, so this run must neither go on with its own steps nor report a result first.
        /// </summary>
        private async Task<bool> EndWithSupersedingRestartAsync(long generation, TaskCompletionSource<bool> completion)
        {
            if (!TryGetSupersedingRestart(generation, out var superseding))
                return false;

            await superseding.ConfigureAwait(false);
            completion.TrySetResult(true);
            return true;
        }

        private bool TryGetSupersedingRestart(long generation, out Task superseding)
        {
            lock (_sync)
            {
                superseding = _currentRestartCompletion!.Task;
                return _restartGeneration != generation;
            }
        }

        private bool CanRestartBeforeReady(RuntimeRestartRequest request)
        {
            if (!request.AllowBeforeReady)
            {
                return false;
            }

            var executionContext = _executionContextProvider?.GetExecutionContext();
            if (executionContext?.State == RuntimeExecutionState.Initializing)
            {
                return true;
            }

            var runtimeStatus = _pipelineStateQuery?.GetRuntimeStatus();
            return runtimeStatus?.State == RuntimeExecutionState.Initializing;
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
            long generation,
            RuntimeRestartLifecycleStage stage,
            string? reasonCode,
            string? diagnostic)
        {
            lock (_sync)
            {
                // A superseded run must not overwrite the stage of the restart that replaced it
                if (_restartGeneration != generation)
                    return;

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
