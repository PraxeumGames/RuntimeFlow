using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RuntimeFlow.Contexts
{
    public sealed partial class RuntimePipeline
    {
        public async Task RunAsync(
            IGameSceneLoader sceneLoader,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (sceneLoader == null) throw new ArgumentNullException(nameof(sceneLoader));
            var flow = _flow ?? throw new FlowNotConfiguredException();
            _sceneLoader = sceneLoader;
            _logger.LogInformation("Running flow scenario {ScenarioType}", flow.GetType().Name);

            await ExecuteFlowOperationAsync(
                    RuntimeLoadingOperationKind.RunFlow,
                    RuntimeOperationCodes.RunFlow,
                    RuntimeExecutionState.Initializing,
                    startMessage: "Executing runtime flow.",
                    finalizingMessage: "Finalizing runtime flow.",
                    successMessage: "Runtime flow completed.",
                    cancelMessage: "Runtime flow canceled by caller.",
                    failMessage: "Runtime flow failed.",
                    sceneLoader,
                    progressNotifier ?? _defaultProgressNotifier,
                    (runner, ct) => flow.ExecuteAsync(runner, ct),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task RestartSessionByReplayingFlowAsync(
            IInitializationProgressNotifier? progressNotifier,
            CancellationToken cancellationToken)
        {
            var sceneLoader = _sceneLoader ?? throw new InvalidOperationException(
                "Cannot replay flow for session restart before the pipeline has been run at least once.");
            var flow = _flow ?? throw new FlowNotConfiguredException();

            await ExecuteFlowOperationAsync(
                    RuntimeLoadingOperationKind.RestartSession,
                    RuntimeOperationCodes.RestartSession,
                    RuntimeExecutionState.Recovering,
                    startMessage: "Replaying runtime flow for session restart.",
                    finalizingMessage: "Finalizing session restart.",
                    successMessage: "Session restarted.",
                    cancelMessage: "Session restart canceled by caller.",
                    failMessage: "Session restart failed.",
                    sceneLoader,
                    progressNotifier ?? _defaultProgressNotifier,
                    async (runner, ct) =>
                    {
                        using (RuntimeFlowReplayScope.Enter())
                        {
                            await _builder.ExecuteOnMainThreadAsync(
                                    token => flow.ExecuteAsync(runner, token),
                                    ct)
                                .ConfigureAwait(false);
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task ExecuteFlowOperationAsync(
            RuntimeLoadingOperationKind operationKind,
            string operationCode,
            RuntimeExecutionState startState,
            string startMessage,
            string finalizingMessage,
            string successMessage,
            string cancelMessage,
            string failMessage,
            IGameSceneLoader sceneLoader,
            IInitializationProgressNotifier? progressNotifier,
            Func<RuntimeFlowRunner, CancellationToken, Task> operation,
            CancellationToken cancellationToken)
        {
            var operationId = CreateLoadingOperationId(operationKind);

            SetStatus(startState, operationCode: operationCode, message: startMessage);
            PublishLoadingSnapshot(
                operationId,
                operationKind,
                RuntimeLoadingOperationStage.Preparing,
                RuntimeLoadingOperationState.Running,
                percent: 0d,
                currentStep: 0,
                totalSteps: 1,
                message: startMessage);

            _healthSupervisor.BeginRun();
            var runner = CreateFlowRunner(sceneLoader, progressNotifier);

            try
            {
                await operation(runner, cancellationToken).ConfigureAwait(false);
                PublishLoadingSnapshot(
                    operationId,
                    operationKind,
                    RuntimeLoadingOperationStage.Finalizing,
                    RuntimeLoadingOperationState.Running,
                    percent: 100d,
                    currentStep: 1,
                    totalSteps: 1,
                    message: finalizingMessage);
                PublishLoadingSnapshot(
                    operationId,
                    operationKind,
                    RuntimeLoadingOperationStage.Completed,
                    RuntimeLoadingOperationState.Completed,
                    percent: 100d,
                    currentStep: 1,
                    totalSteps: 1,
                    message: successMessage);
                SetStatus(RuntimeExecutionState.Ready, operationCode: operationCode, message: successMessage);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                PublishLoadingSnapshot(
                    operationId,
                    operationKind,
                    RuntimeLoadingOperationStage.Canceled,
                    RuntimeLoadingOperationState.Canceled,
                    percent: 0d,
                    currentStep: 0,
                    totalSteps: 1,
                    message: cancelMessage);
                SetStatus(RuntimeExecutionState.Degraded, operationCode: operationCode, message: cancelMessage);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipeline operation failed");
                PublishLoadingSnapshot(
                    operationId,
                    operationKind,
                    RuntimeLoadingOperationStage.Failed,
                    RuntimeLoadingOperationState.Failed,
                    percent: 0d,
                    currentStep: 0,
                    totalSteps: 1,
                    message: failMessage,
                    error: ex);
                SetStatus(RuntimeExecutionState.Failed, operationCode: operationCode, message: failMessage, error: ex);
                throw;
            }
        }

        private RuntimeFlowRunner CreateFlowRunner(
            IGameSceneLoader sceneLoader,
            IInitializationProgressNotifier? progressNotifier)
        {
            return new RuntimeFlowRunner(
                _builder,
                sceneLoader,
                progressNotifier,
                _loadingProgressObserver,
                CreateLoadingOperationId,
                _healthSupervisor,
                _errorClassifier,
                _retryPolicy,
                _retryObserver,
                _transitionHandler,
                _guards,
                OnRunnerStatusChanged);
        }

        private void OnRunnerStatusChanged(RuntimeExecutionState state, string? message)
        {
            SetStatus(state, operationCode: RuntimeOperationCodes.Recovery, message: message);
        }
    }
}
