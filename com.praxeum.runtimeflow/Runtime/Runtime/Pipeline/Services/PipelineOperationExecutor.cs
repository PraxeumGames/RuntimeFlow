using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RuntimeFlow.Loading;
using RuntimeFlow.Status;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Pipeline
{
    internal sealed class PipelineOperationExecutor
    {
        private long _loadingOperationSequence;
        private readonly IRuntimeLoadingProgressObserver _loadingProgressObserver;
        private readonly IInitializationProgressNotifier? _defaultProgressNotifier;
        private readonly PipelineStatusService _statusService;
        private readonly Action _invalidateTransitions;
        private readonly ILogger _logger;

        public PipelineOperationExecutor(
            IRuntimeLoadingProgressObserver loadingProgressObserver,
            IInitializationProgressNotifier? defaultProgressNotifier,
            PipelineStatusService statusService,
            Action invalidateTransitions,
            ILogger logger)
        {
            _loadingProgressObserver = loadingProgressObserver;
            _defaultProgressNotifier = defaultProgressNotifier;
            _statusService = statusService;
            _invalidateTransitions = invalidateTransitions;
            _logger = logger;
        }

        internal async Task<T> ExecuteScopeOperationAsync<T>(
            RuntimeLoadingOperationKind operationKind, string operationCode, Type? scopeKey, RuntimeExecutionState startState, bool splitPerScope,
            string startMessage, string successMessage, string cancelMessage, string failMessage,
            Func<IInitializationProgressNotifier, CancellationToken, Task<T>> operation,
            IInitializationProgressNotifier? progressNotifier, CancellationToken cancellationToken, bool invalidateTransitions = true)
        {
            if (invalidateTransitions) _invalidateTransitions();
            var operationId = CreateLoadingOperationId(operationKind);
            var notifier = CreateProgressNotifier(progressNotifier, operationKind, operationId, splitPerScope);
            _statusService.SetStatus(startState, operationCode, startMessage);
            Publish(operationId, operationKind, RuntimeLoadingOperationStage.Preparing, RuntimeLoadingOperationState.Running, scopeKey, scopeKey?.Name, 0d, 0, 1, startMessage);
            try
            {
                var result = await operation(notifier, cancellationToken).ConfigureAwait(false);
                _statusService.SetStatus(RuntimeExecutionState.Ready, operationCode, successMessage);
                return result;
            }
            catch (OperationCanceledException)
            {
                Publish(operationId, operationKind, RuntimeLoadingOperationStage.Canceled, RuntimeLoadingOperationState.Canceled, scopeKey, scopeKey?.Name, 0d, 0, 1, cancelMessage);
                var current = _statusService.GetStatus();
                if (current.State != RuntimeExecutionState.Ready)
                    _statusService.SetStatus(RuntimeExecutionState.Degraded, operationCode, cancelMessage);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipeline operation failed");
                Publish(operationId, operationKind, RuntimeLoadingOperationStage.Failed, RuntimeLoadingOperationState.Failed, scopeKey, scopeKey?.Name, 0d, 0, 1, failMessage, ex);
                _statusService.SetStatus(RuntimeExecutionState.Failed, operationCode, failMessage, ex);
                throw;
            }
        }

        internal Task ExecuteScopeOperationAsync(
            RuntimeLoadingOperationKind operationKind, string operationCode, Type? scopeKey, RuntimeExecutionState startState, bool splitPerScope,
            string startMessage, string successMessage, string cancelMessage, string failMessage,
            Func<IInitializationProgressNotifier, CancellationToken, Task> operation,
            IInitializationProgressNotifier? progressNotifier, CancellationToken cancellationToken, bool invalidateTransitions = true)
        {
            return ExecuteScopeOperationAsync<object?>(operationKind, operationCode, scopeKey, startState, splitPerScope, startMessage, successMessage, cancelMessage, failMessage,
                async (notifier, ct) => { await operation(notifier, ct).ConfigureAwait(false); return null; }, progressNotifier, cancellationToken, invalidateTransitions);
        }

        private IInitializationProgressNotifier CreateProgressNotifier(IInitializationProgressNotifier? progressNotifier, RuntimeLoadingOperationKind operationKind, string operationId, bool splitPerScope)
        {
            var baseNotifier = progressNotifier ?? _defaultProgressNotifier ?? NullInitializationProgressNotifier.Instance;
            var loadingNotifier = new RuntimeLoadingProgressNotifierAdapter(_loadingProgressObserver, operationKind, operationId, null, splitPerScope);
            return new CompositeInitializationProgressNotifier(baseNotifier, loadingNotifier);
        }

        internal string CreateLoadingOperationId(RuntimeLoadingOperationKind operationKind)
        {
            var seq = Interlocked.Increment(ref _loadingOperationSequence);
            var code = operationKind switch
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
            return $"{code}-{seq:D6}";
        }

        internal void Publish(string operationId, RuntimeLoadingOperationKind kind, RuntimeLoadingOperationStage stage, RuntimeLoadingOperationState state, Type? scopeKey, string? scopeName, double percent, int currentStep, int totalSteps, string? message, Exception? error = null)
        {
            _loadingProgressObserver.OnLoadingProgress(new RuntimeLoadingOperationSnapshot(operationId, kind, stage, state, scopeKey, scopeName, percent, currentStep, totalSteps, message, DateTimeOffset.UtcNow, error?.GetType().Name, error?.Message));
        }
    }
}
