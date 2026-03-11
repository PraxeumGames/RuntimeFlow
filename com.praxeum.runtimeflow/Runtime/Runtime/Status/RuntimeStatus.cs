using System;

namespace RuntimeFlow.Contexts
{
    public enum RuntimeExecutionState
    {
        ColdStart = 0,
        Initializing = 1,
        Ready = 2,
        Degraded = 3,
        Recovering = 4,
        Failed = 5
    }

    public sealed class RuntimeStatus
    {
        public RuntimeStatus(
            RuntimeExecutionState state,
            DateTimeOffset updatedAtUtc,
            string? currentOperationCode = null,
            string? message = null,
            string? blockingReasonCode = null,
            string? lastErrorType = null,
            string? lastErrorMessage = null)
        {
            State = state;
            UpdatedAtUtc = updatedAtUtc;
            CurrentOperationCode = currentOperationCode;
            Message = message;
            BlockingReasonCode = blockingReasonCode;
            LastErrorType = lastErrorType;
            LastErrorMessage = lastErrorMessage;
        }

        public RuntimeExecutionState State { get; }
        public DateTimeOffset UpdatedAtUtc { get; }
        public string? CurrentOperationCode { get; }
        public string? Message { get; }
        public string? BlockingReasonCode { get; }
        public string? LastErrorType { get; }
        public string? LastErrorMessage { get; }
        public bool IsReady => State == RuntimeExecutionState.Ready || State == RuntimeExecutionState.Degraded;
    }

    public sealed class RuntimeReadinessStatus
    {
        public RuntimeReadinessStatus(
            bool isReady,
            DateTimeOffset updatedAtUtc,
            string? currentOperationCode = null,
            string? blockingReasonCode = null,
            string? blockingReason = null)
        {
            IsReady = isReady;
            UpdatedAtUtc = updatedAtUtc;
            CurrentOperationCode = currentOperationCode;
            BlockingReasonCode = blockingReasonCode;
            BlockingReason = blockingReason;
        }

        public bool IsReady { get; }
        public DateTimeOffset UpdatedAtUtc { get; }
        public string? CurrentOperationCode { get; }
        public string? BlockingReasonCode { get; }
        public string? BlockingReason { get; }
    }
}
