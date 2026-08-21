using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Loading;
using RuntimeFlow.Status;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Pipeline
{
    public sealed partial class RuntimePipeline
    {
        private Task<T> ExecuteScopeOperationAsync<T>(RuntimeLoadingOperationKind kind, string code, Type? scopeKey, RuntimeExecutionState startState, bool splitPerScope, string startMessage, string successMessage, string cancelMessage, string failMessage, Func<IInitializationProgressNotifier, CancellationToken, Task<T>> op, IInitializationProgressNotifier? notifier, CancellationToken ct, bool invalidateTransitions = true)
            => _operationExecutor.ExecuteScopeOperationAsync(kind, code, scopeKey, startState, splitPerScope, startMessage, successMessage, cancelMessage, failMessage, op, notifier, ct, invalidateTransitions);
        private Task ExecuteScopeOperationAsync(RuntimeLoadingOperationKind kind, string code, Type? scopeKey, RuntimeExecutionState startState, bool splitPerScope, string startMessage, string successMessage, string cancelMessage, string failMessage, Func<IInitializationProgressNotifier, CancellationToken, Task> op, IInitializationProgressNotifier? notifier, CancellationToken ct, bool invalidateTransitions = true)
            => _operationExecutor.ExecuteScopeOperationAsync(kind, code, scopeKey, startState, splitPerScope, startMessage, successMessage, cancelMessage, failMessage, op, notifier, ct, invalidateTransitions);

        private string CreateLoadingOperationId(RuntimeLoadingOperationKind operationKind)
            => _operationExecutor.CreateLoadingOperationId(operationKind);

        private void PublishLoadingSnapshot(string operationId, RuntimeLoadingOperationKind kind, RuntimeLoadingOperationStage stage, RuntimeLoadingOperationState state, double percent, int currentStep, int totalSteps, string? message, Type? scopeKey = null, string? scopeName = null, Exception? error = null)
            => _operationExecutor.Publish(operationId, kind, stage, state, scopeKey, scopeName, percent, currentStep, totalSteps, message, error);
    }
}
