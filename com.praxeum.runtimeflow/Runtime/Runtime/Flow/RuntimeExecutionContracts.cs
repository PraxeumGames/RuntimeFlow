using System;
using System.Threading;

namespace RuntimeFlow.Contexts
{
    public enum RuntimeExecutionPhase
    {
        Unknown = 0,
        Bootstrap = 1,
        Flow = 2,
        Restart = 3
    }

    public interface IRuntimeExecutionContext
    {
        RuntimeExecutionPhase Phase { get; }
        bool IsReplay { get; }
        RuntimeExecutionState State { get; }
        string CurrentOperationCode { get; }
        DateTimeOffset UpdatedAtUtc { get; }
    }

    public sealed class RuntimeExecutionContextSnapshot : IRuntimeExecutionContext
    {
        public RuntimeExecutionContextSnapshot(
            RuntimeExecutionPhase phase,
            bool isReplay,
            RuntimeExecutionState state,
            DateTimeOffset updatedAtUtc,
            string currentOperationCode = null)
        {
            Phase = phase;
            IsReplay = isReplay;
            State = state;
            UpdatedAtUtc = updatedAtUtc;
            CurrentOperationCode = Normalize(currentOperationCode);
        }

        public RuntimeExecutionPhase Phase { get; }
        public bool IsReplay { get; }
        public RuntimeExecutionState State { get; }
        public string CurrentOperationCode { get; }
        public DateTimeOffset UpdatedAtUtc { get; }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public interface IRuntimeExecutionContextProvider
    {
        IRuntimeExecutionContext GetExecutionContext();
    }

    /// <summary>
    /// Async-flow scoped replay marker.
    /// Use <see cref="Enter"/> around replay execution to project replay intent
    /// into status/context providers without leaking state globally.
    /// </summary>
    public static class RuntimeFlowReplayScope
    {
        private static readonly AsyncLocal<int> ReplayDepth = new AsyncLocal<int>();

        public static bool IsActive => ReplayDepth.Value > 0;

        public static IDisposable Enter()
        {
            ReplayDepth.Value = ReplayDepth.Value + 1;
            return new ScopeToken();
        }

        private sealed class ScopeToken : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                var current = ReplayDepth.Value;
                ReplayDepth.Value = current <= 0 ? 0 : current - 1;
            }
        }
    }

    /// <summary>
    /// Thread-safe execution context state store for runtime lifecycle.
    /// </summary>
    public sealed class RuntimeExecutionContextManager : IRuntimeExecutionContextProvider
    {
        private readonly object _sync = new object();
        private readonly Func<DateTimeOffset> _timestampProvider;
        private RuntimeExecutionContextSnapshot _snapshot;

        public RuntimeExecutionContextManager(
            RuntimeExecutionPhase initialPhase = RuntimeExecutionPhase.Unknown,
            RuntimeExecutionState initialState = RuntimeExecutionState.ColdStart,
            string currentOperationCode = null,
            bool initialIsReplay = false,
            Func<DateTimeOffset> timestampProvider = null)
        {
            _timestampProvider = timestampProvider ?? (() => DateTimeOffset.UtcNow);
            _snapshot = new RuntimeExecutionContextSnapshot(
                initialPhase,
                initialIsReplay,
                initialState,
                _timestampProvider(),
                currentOperationCode);
        }

        public RuntimeExecutionContextSnapshot Snapshot
        {
            get
            {
                lock (_sync)
                {
                    return _snapshot;
                }
            }
        }

        public IRuntimeExecutionContext GetExecutionContext()
        {
            return Snapshot;
        }

        public RuntimeExecutionContextSnapshot Update(
            RuntimeExecutionPhase phase,
            RuntimeExecutionState state,
            string currentOperationCode = null,
            bool? isReplay = null)
        {
            lock (_sync)
            {
                _snapshot = new RuntimeExecutionContextSnapshot(
                    phase,
                    isReplay ?? RuntimeFlowReplayScope.IsActive,
                    state,
                    _timestampProvider(),
                    currentOperationCode);
                return _snapshot;
            }
        }

        public RuntimeExecutionContextSnapshot UpdateFromStatus(
            RuntimeExecutionPhase phase,
            RuntimeStatus status,
            bool? isReplay = null)
        {
            if (status == null) throw new ArgumentNullException(nameof(status));

            lock (_sync)
            {
                _snapshot = new RuntimeExecutionContextSnapshot(
                    phase,
                    isReplay ?? RuntimeFlowReplayScope.IsActive,
                    status.State,
                    status.UpdatedAtUtc,
                    status.CurrentOperationCode);
                return _snapshot;
            }
        }
    }
}
