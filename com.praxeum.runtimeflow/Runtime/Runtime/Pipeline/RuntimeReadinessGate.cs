using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Composable readiness gate for runtime restart/replay orchestration.
    /// Supports runtime readiness projection and explicit temporary blockers.
    /// </summary>
    public sealed class RuntimeReadinessGate : IRuntimeReadinessGate
    {
        private readonly object _sync = new object();
        private readonly Func<DateTimeOffset> _timestampProvider;
        private readonly Func<RuntimeReadinessStatus>? _runtimeReadinessProvider;
        private readonly Func<IRuntimeExecutionContext?>? _executionContextProvider;
        private readonly Func<RuntimeRestartLifecycleSnapshot?>? _restartLifecycleSnapshotProvider;
        private readonly HashSet<string> _blockers = new HashSet<string>(StringComparer.Ordinal);
        private string? _blockingReasonCode;
        private string? _blockingReason;

        public RuntimeReadinessGate(
            Func<RuntimeReadinessStatus>? runtimeReadinessProvider = null,
            Func<IRuntimeExecutionContext?>? executionContextProvider = null,
            Func<RuntimeRestartLifecycleSnapshot?>? restartLifecycleSnapshotProvider = null,
            Func<DateTimeOffset>? timestampProvider = null)
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

        public IDisposable Block(string reasonCode, string? reason = null)
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
}
