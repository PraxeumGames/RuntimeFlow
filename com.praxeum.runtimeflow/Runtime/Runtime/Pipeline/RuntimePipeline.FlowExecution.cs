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
            if (_flow == null)
                throw new FlowNotConfiguredException();
            _sceneLoader = sceneLoader;
            _logger.LogInformation("Running flow scenario {ScenarioType}", _flow.GetType().Name);
            var operationId = CreateLoadingOperationId(RuntimeLoadingOperationKind.RunFlow);

            SetStatus(RuntimeExecutionState.Initializing, operationCode: RuntimeOperationCodes.RunFlow, message: "Executing runtime flow.");
            PublishLoadingSnapshot(
                operationId,
                RuntimeLoadingOperationKind.RunFlow,
                RuntimeLoadingOperationStage.Preparing,
                RuntimeLoadingOperationState.Running,
                percent: 0d,
                currentStep: 0,
                totalSteps: 1,
                message: "Executing runtime flow.");

            _healthSupervisor.BeginRun();
            var runner = new RuntimeFlowRunner(
                _builder,
                sceneLoader,
                progressNotifier ?? _defaultProgressNotifier,
                _loadingProgressObserver,
                CreateLoadingOperationId,
                _healthSupervisor,
                _errorClassifier,
                _retryPolicy,
                _retryObserver,
                _transitionHandler,
                _guards,
                OnRunnerStatusChanged);

            try
            {
                await _flow.ExecuteAsync(runner, cancellationToken).ConfigureAwait(false);
                PublishLoadingSnapshot(
                    operationId,
                    RuntimeLoadingOperationKind.RunFlow,
                    RuntimeLoadingOperationStage.Finalizing,
                    RuntimeLoadingOperationState.Running,
                    percent: 100d,
                    currentStep: 1,
                    totalSteps: 1,
                    message: "Finalizing runtime flow.");
                PublishLoadingSnapshot(
                    operationId,
                    RuntimeLoadingOperationKind.RunFlow,
                    RuntimeLoadingOperationStage.Completed,
                    RuntimeLoadingOperationState.Completed,
                    percent: 100d,
                    currentStep: 1,
                    totalSteps: 1,
                    message: "Runtime flow completed.");
                SetStatus(RuntimeExecutionState.Ready, operationCode: RuntimeOperationCodes.RunFlow, message: "Runtime flow completed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                PublishLoadingSnapshot(
                    operationId,
                    RuntimeLoadingOperationKind.RunFlow,
                    RuntimeLoadingOperationStage.Canceled,
                    RuntimeLoadingOperationState.Canceled,
                    percent: 0d,
                    currentStep: 0,
                    totalSteps: 1,
                    message: "Runtime flow canceled by caller.");
                SetStatus(RuntimeExecutionState.Degraded, operationCode: RuntimeOperationCodes.RunFlow, message: "Runtime flow canceled by caller.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipeline operation failed");
                PublishLoadingSnapshot(
                    operationId,
                    RuntimeLoadingOperationKind.RunFlow,
                    RuntimeLoadingOperationStage.Failed,
                    RuntimeLoadingOperationState.Failed,
                    percent: 0d,
                    currentStep: 0,
                    totalSteps: 1,
                    message: "Runtime flow failed.",
                    error: ex);
                SetStatus(RuntimeExecutionState.Failed, operationCode: RuntimeOperationCodes.RunFlow, message: "Runtime flow failed.", error: ex);
                throw;
            }
        }

        private async Task RestartSessionByReplayingFlowAsync(
            IInitializationProgressNotifier? progressNotifier,
            CancellationToken cancellationToken)
        {
            var sceneLoader = _sceneLoader ?? throw new InvalidOperationException(
                "Cannot replay flow for session restart before the pipeline has been run at least once.");
            var flow = _flow ?? throw new FlowNotConfiguredException();
            var operationId = CreateLoadingOperationId(RuntimeLoadingOperationKind.RestartSession);

            SetStatus(RuntimeExecutionState.Recovering, operationCode: RuntimeOperationCodes.RestartSession, message: "Replaying runtime flow for session restart.");
            PublishLoadingSnapshot(
                operationId,
                RuntimeLoadingOperationKind.RestartSession,
                RuntimeLoadingOperationStage.Preparing,
                RuntimeLoadingOperationState.Running,
                percent: 0d,
                currentStep: 0,
                totalSteps: 1,
                message: "Replaying runtime flow for session restart.");

            _healthSupervisor.BeginRun();
            var runner = new RuntimeFlowRunner(
                _builder,
                sceneLoader,
                progressNotifier ?? _defaultProgressNotifier,
                _loadingProgressObserver,
                CreateLoadingOperationId,
                _healthSupervisor,
                _errorClassifier,
                _retryPolicy,
                _retryObserver,
                _transitionHandler,
                _guards,
                OnRunnerStatusChanged);

            try
            {
                using (RuntimeFlowReplayScope.Enter())
                {
                    await _builder.ExecuteOnMainThreadAsync(
                            token => flow.ExecuteAsync(runner, token),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                PublishLoadingSnapshot(
                    operationId,
                    RuntimeLoadingOperationKind.RestartSession,
                    RuntimeLoadingOperationStage.Finalizing,
                    RuntimeLoadingOperationState.Running,
                    percent: 100d,
                    currentStep: 1,
                    totalSteps: 1,
                    message: "Finalizing session restart.");
                PublishLoadingSnapshot(
                    operationId,
                    RuntimeLoadingOperationKind.RestartSession,
                    RuntimeLoadingOperationStage.Completed,
                    RuntimeLoadingOperationState.Completed,
                    percent: 100d,
                    currentStep: 1,
                    totalSteps: 1,
                    message: "Session restarted.");
                SetStatus(RuntimeExecutionState.Ready, operationCode: RuntimeOperationCodes.RestartSession, message: "Session restarted.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                PublishLoadingSnapshot(
                    operationId,
                    RuntimeLoadingOperationKind.RestartSession,
                    RuntimeLoadingOperationStage.Canceled,
                    RuntimeLoadingOperationState.Canceled,
                    percent: 0d,
                    currentStep: 0,
                    totalSteps: 1,
                    message: "Session restart canceled by caller.");
                SetStatus(RuntimeExecutionState.Degraded, operationCode: RuntimeOperationCodes.RestartSession, message: "Session restart canceled by caller.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipeline operation failed");
                PublishLoadingSnapshot(
                    operationId,
                    RuntimeLoadingOperationKind.RestartSession,
                    RuntimeLoadingOperationStage.Failed,
                    RuntimeLoadingOperationState.Failed,
                    percent: 0d,
                    currentStep: 0,
                    totalSteps: 1,
                    message: "Session restart failed.",
                    error: ex);
                SetStatus(RuntimeExecutionState.Failed, operationCode: RuntimeOperationCodes.RestartSession, message: "Session restart failed.", error: ex);
                throw;
            }
        }

        private void OnRunnerStatusChanged(RuntimeExecutionState state, string? message)
        {
            SetStatus(state, operationCode: RuntimeOperationCodes.Recovery, message: message);
        }
    }
}
