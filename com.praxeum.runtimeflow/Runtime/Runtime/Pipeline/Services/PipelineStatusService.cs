using System;
using System.Threading;
using RuntimeFlow.Flow;
using RuntimeFlow.Status;

namespace RuntimeFlow.Pipeline
{
    internal sealed class PipelineStatusService
    {
        private readonly object _sync = new();
        private RuntimeStatus _status;
        private readonly RuntimeExecutionContextManager _executionContextManager;

        public PipelineStatusService(RuntimeStatus initialStatus)
        {
            _status = initialStatus;
            _executionContextManager = new RuntimeExecutionContextManager(
                RuntimeExecutionPhase.Bootstrap, initialStatus.State, initialStatus.CurrentOperationCode, RuntimeFlowReplayScope.IsActive, () => DateTimeOffset.UtcNow);
        }

        public RuntimeStatus GetStatus() { lock (_sync) return _status; }

        public void SetStatus(RuntimeExecutionState state, string? operationCode = null, string? message = null, Exception? error = null)
        {
            lock (_sync) SetStatusUnsafe(state, operationCode, message, error);
        }

        private void SetStatusUnsafe(RuntimeExecutionState state, string? operationCode, string? message, Exception? error)
        {
            var blockingReasonCode = state switch
            {
                RuntimeExecutionState.ColdStart => RuntimeOperationCodes.ColdStart,
                RuntimeExecutionState.Initializing => operationCode ?? "initializing",
                RuntimeExecutionState.Recovering => operationCode ?? "recovering",
                RuntimeExecutionState.Failed => operationCode ?? "failed",
                _ => null
            };
            var status = new RuntimeStatus(state, DateTimeOffset.UtcNow, operationCode, message, blockingReasonCode, error?.GetType().Name, error?.Message);
            _status = status;
            _executionContextManager.UpdateFromStatus(DetermineExecutionPhase(state, operationCode), status, RuntimeFlowReplayScope.IsActive);
        }

        public IRuntimeExecutionContext GetExecutionContext() => _executionContextManager.GetExecutionContext();
        public RuntimeExecutionContextManager ExecutionContextManager => _executionContextManager;

        private static RuntimeExecutionPhase DetermineExecutionPhase(RuntimeExecutionState state, string? operationCode)
        {
            if (state == RuntimeExecutionState.Recovering) return RuntimeExecutionPhase.Restart;
            if (string.Equals(operationCode, RuntimeOperationCodes.RestartSession, StringComparison.Ordinal) || string.Equals(operationCode, RuntimeOperationCodes.Recovery, StringComparison.Ordinal))
                return RuntimeExecutionPhase.Restart;
            if (string.Equals(operationCode, RuntimeOperationCodes.RunFlow, StringComparison.Ordinal) || string.Equals(operationCode, RuntimeOperationCodes.LoadScene, StringComparison.Ordinal) || string.Equals(operationCode, RuntimeOperationCodes.LoadModule, StringComparison.Ordinal) || string.Equals(operationCode, RuntimeOperationCodes.ReloadScene, StringComparison.Ordinal) || string.Equals(operationCode, RuntimeOperationCodes.ReloadModule, StringComparison.Ordinal))
                return RuntimeExecutionPhase.Flow;
            if (state == RuntimeExecutionState.ColdStart || string.Equals(operationCode, RuntimeOperationCodes.ColdStart, StringComparison.Ordinal) || string.Equals(operationCode, RuntimeOperationCodes.Initialize, StringComparison.Ordinal))
                return RuntimeExecutionPhase.Bootstrap;
            return RuntimeFlowReplayScope.IsActive ? RuntimeExecutionPhase.Restart : RuntimeExecutionPhase.Flow;
        }
    }
}
