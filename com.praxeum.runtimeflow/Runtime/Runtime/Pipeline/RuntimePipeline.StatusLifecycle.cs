using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RuntimeFlow.Contexts
{
    public sealed partial class RuntimePipeline
    {
        public ValueTask DisposeAsync()
        {
            return DisposeAsyncCore(CancellationToken.None);
        }

        internal async ValueTask DisposeAsync(CancellationToken cancellationToken)
        {
            await DisposeAsyncCore(cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask DisposeAsyncCore(CancellationToken cancellationToken)
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                await _builder.DisposeAllScopesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort disposal
            }

            _logger.LogDebug("Pipeline disposed");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RuntimePipeline));
        }

        private void SetStatus(
            RuntimeExecutionState state,
            string? operationCode = null,
            string? message = null,
            Exception? error = null)
        {
            lock (_statusSync)
            {
                SetStatusUnsafe(state, operationCode, message, error);
            }
        }

        private void SetStatusUnsafe(
            RuntimeExecutionState state,
            string? operationCode = null,
            string? message = null,
            Exception? error = null)
        {
            var blockingReasonCode = state switch
            {
                RuntimeExecutionState.ColdStart => RuntimeOperationCodes.ColdStart,
                RuntimeExecutionState.Initializing => operationCode ?? "initializing",
                RuntimeExecutionState.Recovering => operationCode ?? "recovering",
                RuntimeExecutionState.Failed => operationCode ?? "failed",
                _ => null
            };

            var status = new RuntimeStatus(
                state,
                DateTimeOffset.UtcNow,
                currentOperationCode: operationCode,
                message: message,
                blockingReasonCode: blockingReasonCode,
                lastErrorType: error?.GetType().Name,
                lastErrorMessage: error?.Message);

            _status = status;
            _executionContextManager.UpdateFromStatus(
                DetermineExecutionPhase(state, operationCode),
                status,
                RuntimeFlowReplayScope.IsActive);
        }

        private static RuntimeExecutionPhase DetermineExecutionPhase(
            RuntimeExecutionState state,
            string? operationCode)
        {
            if (state == RuntimeExecutionState.Recovering)
                return RuntimeExecutionPhase.Restart;

            if (string.Equals(operationCode, RuntimeOperationCodes.RestartSession, StringComparison.Ordinal)
                || string.Equals(operationCode, RuntimeOperationCodes.Recovery, StringComparison.Ordinal))
            {
                return RuntimeExecutionPhase.Restart;
            }

            if (string.Equals(operationCode, RuntimeOperationCodes.RunFlow, StringComparison.Ordinal)
                || string.Equals(operationCode, RuntimeOperationCodes.LoadScene, StringComparison.Ordinal)
                || string.Equals(operationCode, RuntimeOperationCodes.LoadModule, StringComparison.Ordinal)
                || string.Equals(operationCode, RuntimeOperationCodes.ReloadScene, StringComparison.Ordinal)
                || string.Equals(operationCode, RuntimeOperationCodes.ReloadModule, StringComparison.Ordinal))
            {
                return RuntimeExecutionPhase.Flow;
            }

            if (state == RuntimeExecutionState.ColdStart
                || string.Equals(operationCode, RuntimeOperationCodes.ColdStart, StringComparison.Ordinal)
                || string.Equals(operationCode, RuntimeOperationCodes.Initialize, StringComparison.Ordinal))
            {
                return RuntimeExecutionPhase.Bootstrap;
            }

            return RuntimeFlowReplayScope.IsActive
                ? RuntimeExecutionPhase.Restart
                : RuntimeExecutionPhase.Flow;
        }
    }
}
