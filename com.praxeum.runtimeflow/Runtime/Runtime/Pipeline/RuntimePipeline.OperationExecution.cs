using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RuntimeFlow.Contexts
{
    public sealed partial class RuntimePipeline
    {
        private async Task<T> ExecuteScopeOperationAsync<T>(
            RuntimeLoadingOperationKind operationKind,
            string operationCode,
            Type? scopeKey,
            RuntimeExecutionState startState,
            bool splitPerScope,
            string startMessage,
            string successMessage,
            string cancelMessage,
            string failMessage,
            Func<IInitializationProgressNotifier, CancellationToken, Task<T>> operation,
            IInitializationProgressNotifier? progressNotifier,
            CancellationToken cancellationToken)
        {
            var operationId = CreateLoadingOperationId(operationKind);
            var notifier = CreateProgressNotifier(progressNotifier, operationKind, operationId, splitPerScope);

            SetStatus(startState, operationCode: operationCode, message: startMessage);
            PublishLoadingSnapshot(
                operationId, operationKind,
                RuntimeLoadingOperationStage.Preparing, RuntimeLoadingOperationState.Running,
                scopeKey: scopeKey, scopeName: scopeKey?.Name,
                percent: 0d, currentStep: 0, totalSteps: 1,
                message: startMessage);

            try
            {
                var result = await operation(notifier, cancellationToken).ConfigureAwait(false);
                SetStatus(RuntimeExecutionState.Ready, operationCode: operationCode, message: successMessage);
                return result;
            }
            catch (OperationCanceledException)
            {
                PublishLoadingSnapshot(
                    operationId, operationKind,
                    RuntimeLoadingOperationStage.Canceled, RuntimeLoadingOperationState.Canceled,
                    scopeKey: scopeKey, scopeName: scopeKey?.Name,
                    percent: 0d, currentStep: 0, totalSteps: 1,
                    message: cancelMessage);
                // Only downgrade status if the pipeline hasn't already moved to a terminal state
                // (e.g., a concurrent reload may have already succeeded and set Ready).
                lock (_statusSync)
                {
                    if (_status.State != RuntimeExecutionState.Ready)
                        SetStatusUnsafe(RuntimeExecutionState.Degraded, operationCode: operationCode, message: cancelMessage);
                }
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipeline operation failed");
                PublishLoadingSnapshot(
                    operationId, operationKind,
                    RuntimeLoadingOperationStage.Failed, RuntimeLoadingOperationState.Failed,
                    scopeKey: scopeKey, scopeName: scopeKey?.Name,
                    percent: 0d, currentStep: 0, totalSteps: 1,
                    message: failMessage, error: ex);
                SetStatus(RuntimeExecutionState.Failed, operationCode: operationCode, message: failMessage, error: ex);
                throw;
            }
        }

        private async Task ExecuteScopeOperationAsync(
            RuntimeLoadingOperationKind operationKind,
            string operationCode,
            Type? scopeKey,
            RuntimeExecutionState startState,
            bool splitPerScope,
            string startMessage,
            string successMessage,
            string cancelMessage,
            string failMessage,
            Func<IInitializationProgressNotifier, CancellationToken, Task> operation,
            IInitializationProgressNotifier? progressNotifier,
            CancellationToken cancellationToken)
        {
            await ExecuteScopeOperationAsync<object?>(
                operationKind, operationCode, scopeKey, startState, splitPerScope,
                startMessage, successMessage, cancelMessage, failMessage,
                async (notifier, ct) => { await operation(notifier, ct).ConfigureAwait(false); return null; },
                progressNotifier, cancellationToken).ConfigureAwait(false);
        }

        private IInitializationProgressNotifier CreateProgressNotifier(
            IInitializationProgressNotifier? progressNotifier,
            RuntimeLoadingOperationKind operationKind,
            string operationId,
            bool splitOperationPerScope = false)
        {
            var baseNotifier = progressNotifier ?? _defaultProgressNotifier ?? NullInitializationProgressNotifier.Instance;
            var loadingNotifier = new RuntimeLoadingProgressNotifierAdapter(
                _loadingProgressObserver,
                operationKind,
                operationId,
                splitOperationPerScope: splitOperationPerScope);
            return new CompositeInitializationProgressNotifier(baseNotifier, loadingNotifier);
        }

        private string CreateLoadingOperationId(RuntimeLoadingOperationKind operationKind)
        {
            var sequence = Interlocked.Increment(ref _loadingOperationSequence);
            var operationCode = operationKind switch
            {
                RuntimeLoadingOperationKind.Initialize => RuntimeOperationCodes.Initialize,
                RuntimeLoadingOperationKind.LoadScene => RuntimeOperationCodes.LoadScene,
                RuntimeLoadingOperationKind.LoadModule => RuntimeOperationCodes.LoadModule,
                RuntimeLoadingOperationKind.ReloadModule => RuntimeOperationCodes.ReloadModule,
                RuntimeLoadingOperationKind.RestartSession => RuntimeOperationCodes.RestartSession,
                RuntimeLoadingOperationKind.ReloadScene => RuntimeOperationCodes.ReloadScene,
                RuntimeLoadingOperationKind.RunFlow => RuntimeOperationCodes.RunFlow,
                _ => "loading"
            };

            return $"{operationCode}-{sequence:D6}";
        }

        private void PublishLoadingSnapshot(
            string operationId,
            RuntimeLoadingOperationKind operationKind,
            RuntimeLoadingOperationStage stage,
            RuntimeLoadingOperationState state,
            Type? scopeKey = null,
            string? scopeName = null,
            double percent = 0d,
            int currentStep = 0,
            int totalSteps = 0,
            string? message = null,
            Exception? error = null)
        {
            _loadingProgressObserver.OnLoadingProgress(
                new RuntimeLoadingOperationSnapshot(
                    operationId: operationId,
                    operationKind: operationKind,
                    stage: stage,
                    state: state,
                    scopeKey: scopeKey,
                    scopeName: scopeName,
                    percent: percent,
                    currentStep: currentStep,
                    totalSteps: totalSteps,
                    message: message,
                    timestampUtc: DateTimeOffset.UtcNow,
                    errorType: error?.GetType().Name,
                    errorMessage: error?.Message));
        }
    }
}
