using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace RuntimeFlow.Contexts
{
    public enum RuntimeRestartLifecycleStage
    {
        Idle = 0,
        WaitingReadiness = 1,
        GuardValidation = 2,
        Restarting = 3,
        ReplayingFlow = 4,
        Completed = 5,
        Failed = 6
    }

    public sealed class RuntimeRestartRequest
    {
        public RuntimeRestartRequest(string reasonCode = null, string diagnostic = null)
        {
            ReasonCode = Normalize(reasonCode);
            Diagnostic = Normalize(diagnostic);
        }

        public string ReasonCode { get; }
        public string Diagnostic { get; }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public sealed class RuntimeRestartLifecycleSnapshot
    {
        public RuntimeRestartLifecycleSnapshot(
            RuntimeRestartLifecycleStage stage,
            DateTimeOffset updatedAtUtc,
            string reasonCode = null,
            string diagnostic = null,
            string errorType = null,
            string errorMessage = null)
        {
            Stage = stage;
            UpdatedAtUtc = updatedAtUtc;
            ReasonCode = Normalize(reasonCode);
            Diagnostic = Normalize(diagnostic);
            ErrorType = Normalize(errorType);
            ErrorMessage = Normalize(errorMessage);
        }

        public RuntimeRestartLifecycleStage Stage { get; }
        public DateTimeOffset UpdatedAtUtc { get; }
        public string ReasonCode { get; }
        public string Diagnostic { get; }
        public string ErrorType { get; }
        public string ErrorMessage { get; }

        public bool IsInProgress => Stage == RuntimeRestartLifecycleStage.WaitingReadiness
                                    || Stage == RuntimeRestartLifecycleStage.GuardValidation
                                    || Stage == RuntimeRestartLifecycleStage.Restarting
                                    || Stage == RuntimeRestartLifecycleStage.ReplayingFlow;

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public sealed class RuntimeRestartReadiness
    {
        public RuntimeRestartReadiness(
            bool isReady,
            DateTimeOffset updatedAtUtc,
            string blockingReasonCode = null,
            string blockingReason = null)
        {
            IsReady = isReady;
            UpdatedAtUtc = updatedAtUtc;
            BlockingReasonCode = Normalize(blockingReasonCode);
            BlockingReason = Normalize(blockingReason);
        }

        public bool IsReady { get; }
        public DateTimeOffset UpdatedAtUtc { get; }
        public string BlockingReasonCode { get; }
        public string BlockingReason { get; }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public sealed class RuntimeRestartGuardContext
    {
        public RuntimeRestartGuardContext(
            RuntimeRestartRequest request,
            RuntimeRestartLifecycleSnapshot snapshot = null,
            IRuntimeExecutionContext executionContext = null,
            RuntimeStatus runtimeStatus = null,
            RuntimeReadinessStatus runtimeReadinessStatus = null,
            RuntimeRestartReadiness restartReadiness = null)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
            Snapshot = snapshot;
            ExecutionContext = executionContext;
            RuntimeStatus = runtimeStatus;
            RuntimeReadinessStatus = runtimeReadinessStatus;
            RestartReadiness = restartReadiness;
        }

        public RuntimeRestartRequest Request { get; }
        public RuntimeRestartLifecycleSnapshot Snapshot { get; }
        public IRuntimeExecutionContext ExecutionContext { get; }
        public RuntimeStatus RuntimeStatus { get; }
        public RuntimeReadinessStatus RuntimeReadinessStatus { get; }
        public RuntimeRestartReadiness RestartReadiness { get; }
    }

    public interface IRuntimeRestartReadinessProvider
    {
        RuntimeRestartReadiness GetRestartReadiness();
    }

    public interface IRuntimeRestartGuard
    {
        Task<RuntimeFlowGuardResult> EvaluateAsync(
            RuntimeRestartGuardContext context,
            CancellationToken cancellationToken = default);
    }

    public interface IRuntimeRestartLifecycleManager : IRuntimeRestartReadinessProvider
    {
        RuntimeRestartLifecycleSnapshot Snapshot { get; }
        Task RestartAsync(RuntimeRestartRequest request, CancellationToken cancellationToken = default);
    }

    public interface IRuntimeReadinessGate : IRuntimeRestartReadinessProvider
    {
        IDisposable Block(string reasonCode, string reason = null);
        Task<RuntimeRestartReadiness> WaitUntilReadyAsync(
            TimeSpan timeout,
            TimeSpan? pollInterval = null,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Composable readiness gate for runtime restart/replay orchestration.
    /// Supports runtime readiness projection and explicit temporary blockers.
    /// </summary>
    public sealed class RuntimeReadinessGate : IRuntimeReadinessGate
    {
        private readonly object _sync = new object();
        private readonly Func<DateTimeOffset> _timestampProvider;
        private readonly Func<RuntimeReadinessStatus> _runtimeReadinessProvider;
        private readonly Func<IRuntimeExecutionContext> _executionContextProvider;
        private readonly Func<RuntimeRestartLifecycleSnapshot> _restartLifecycleSnapshotProvider;
        private readonly HashSet<string> _blockers = new HashSet<string>(StringComparer.Ordinal);
        private string _blockingReasonCode;
        private string _blockingReason;

        public RuntimeReadinessGate(
            Func<RuntimeReadinessStatus> runtimeReadinessProvider = null,
            Func<IRuntimeExecutionContext> executionContextProvider = null,
            Func<RuntimeRestartLifecycleSnapshot> restartLifecycleSnapshotProvider = null,
            Func<DateTimeOffset> timestampProvider = null)
        {
            _runtimeReadinessProvider = runtimeReadinessProvider;
            _executionContextProvider = executionContextProvider;
            _restartLifecycleSnapshotProvider = restartLifecycleSnapshotProvider;
            _timestampProvider = timestampProvider ?? (() => DateTimeOffset.UtcNow);
        }

        public RuntimeRestartReadiness GetRestartReadiness()
        {
            lock (_sync)
            {
                if (_blockers.Count > 0)
                {
                    return new RuntimeRestartReadiness(
                        isReady: false,
                        updatedAtUtc: _timestampProvider(),
                        blockingReasonCode: _blockingReasonCode ?? "readiness.blocked",
                        blockingReason: _blockingReason ?? "Readiness gate has active blockers.");
                }
            }

            var lifecycleSnapshot = _restartLifecycleSnapshotProvider?.Invoke();
            if (lifecycleSnapshot != null && lifecycleSnapshot.IsInProgress)
            {
                return new RuntimeRestartReadiness(
                    isReady: false,
                    updatedAtUtc: _timestampProvider(),
                    blockingReasonCode: "restart.in_progress",
                    blockingReason: "Restart lifecycle is already in progress.");
            }

            var executionContext = _executionContextProvider?.Invoke();
            if (executionContext != null
                && executionContext.State == RuntimeExecutionState.Recovering)
            {
                return new RuntimeRestartReadiness(
                    isReady: false,
                    updatedAtUtc: _timestampProvider(),
                    blockingReasonCode: "runtime.recovering",
                    blockingReason: "Runtime is currently recovering.");
            }

            var runtimeReadiness = _runtimeReadinessProvider?.Invoke();
            if (runtimeReadiness != null && !runtimeReadiness.IsReady)
            {
                return new RuntimeRestartReadiness(
                    isReady: false,
                    updatedAtUtc: runtimeReadiness.UpdatedAtUtc,
                    blockingReasonCode: runtimeReadiness.BlockingReasonCode ?? "runtime.not_ready",
                    blockingReason: runtimeReadiness.BlockingReason ?? "Runtime is not ready.");
            }

            return new RuntimeRestartReadiness(
                isReady: true,
                updatedAtUtc: _timestampProvider());
        }

        public IDisposable Block(string reasonCode, string reason = null)
        {
            var normalizedReasonCode = string.IsNullOrWhiteSpace(reasonCode)
                ? "readiness.blocked"
                : reasonCode.Trim();
            var normalizedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            var blockerId = Guid.NewGuid().ToString("N");

            lock (_sync)
            {
                _blockers.Add(blockerId);
                _blockingReasonCode = normalizedReasonCode;
                _blockingReason = normalizedReason;
            }

            return new BlockToken(this, blockerId);
        }

        public async Task<RuntimeRestartReadiness> WaitUntilReadyAsync(
            TimeSpan timeout,
            TimeSpan? pollInterval = null,
            CancellationToken cancellationToken = default)
        {
            var timeoutMs = timeout == Timeout.InfiniteTimeSpan
                ? -1
                : Math.Max(1, (int)Math.Min(int.MaxValue, timeout.TotalMilliseconds));
            var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);
            if (interval <= TimeSpan.Zero)
                interval = TimeSpan.FromMilliseconds(50);

            if (timeoutMs == 0)
            {
                return GetRestartReadiness();
            }

            using (var timeoutCts = timeoutMs < 0 ? null : new CancellationTokenSource(timeoutMs))
            using (var linkedCts = timeoutCts == null
                       ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                       : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
            {
                while (true)
                {
                    linkedCts.Token.ThrowIfCancellationRequested();
                    var readiness = GetRestartReadiness();
                    if (readiness.IsReady)
                        return readiness;

                    await Task.Delay(interval, linkedCts.Token).ConfigureAwait(false);
                }
            }
        }

        private void ReleaseBlock(string blockerId)
        {
            lock (_sync)
            {
                if (!_blockers.Remove(blockerId))
                    return;

                if (_blockers.Count == 0)
                {
                    _blockingReasonCode = null;
                    _blockingReason = null;
                }
            }
        }

        private sealed class BlockToken : IDisposable
        {
            private readonly RuntimeReadinessGate _owner;
            private readonly string _blockerId;
            private int _disposed;

            public BlockToken(RuntimeReadinessGate owner, string blockerId)
            {
                _owner = owner;
                _blockerId = blockerId;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                _owner.ReleaseBlock(_blockerId);
            }
        }
    }

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
                    taskToAwait = RunRestartInternalAsync(request);
                    _inFlightRestartTask = taskToAwait;
                }
            }

            return WaitWithCancellationAsync(taskToAwait, cancellationToken);
        }

        private async Task RunRestartInternalAsync(RuntimeRestartRequest request)
        {
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
                        .EvaluateAsync(CreateGuardContext(request, restartReadiness), CancellationToken.None)
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
                await _restartOperation(request, CancellationToken.None).ConfigureAwait(false);

                if (_replayOperation != null)
                {
                    UpdateSnapshot(RuntimeRestartLifecycleStage.ReplayingFlow, request.ReasonCode, request.Diagnostic);
                    await _replayOperation(request, CancellationToken.None).ConfigureAwait(false);
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
