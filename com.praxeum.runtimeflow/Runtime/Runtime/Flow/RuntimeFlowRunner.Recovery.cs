using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    internal sealed partial class RuntimeFlowRunner
    {
        private async Task EvaluateGuardsAsync(
            RuntimeFlowGuardStage stage,
            Type? scopeKey,
            GameContextType? targetScopeType,
            CancellationToken cancellationToken)
        {
            if (_guards == null || _guards.Count == 0) return;

            var context = new RuntimeFlowGuardContext(stage, this, scopeKey, targetScopeType);
            foreach (var guard in _guards)
            {
                var result = await guard.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
                if (!result.IsAllowed)
                    throw new RuntimeFlowGuardFailedException(stage, result.ReasonCode!, result.Reason);
            }
        }

        private static ScopeNotDeclaredException CreateScopeTypeNotDeclaredException(Type scopeType)
        {
            return new ScopeNotDeclaredException(scopeType);
        }

        private async Task ExecuteWithRecoveryAsync(
            Func<IInitializationProgressNotifier, CancellationToken, Task> operation,
            bool retryAfterRecovery,
            string operationCode,
            RuntimeLoadingOperationKind loadingOperationKind,
            bool splitOperationPerScope,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(operationCode))
                throw new ArgumentException("Operation code is required.", nameof(operationCode));

            var maxAttempts = _retryPolicy.Enabled ? Math.Max(1, _retryPolicy.MaxAttempts) : 1;
            var attempt = 0;

            while (true)
            {
                attempt++;
                var operationId = _operationIdFactory(loadingOperationKind);
                var notifier = CreateProgressNotifier(operationId, loadingOperationKind, splitOperationPerScope);
                PublishOperationSnapshot(
                    operationId,
                    loadingOperationKind,
                    RuntimeLoadingOperationStage.Preparing,
                    RuntimeLoadingOperationState.Running,
                    percent: 0d,
                    currentStep: 0,
                    totalSteps: 1,
                    message: $"Executing operation '{operationCode}'.");

                try
                {
                    await operation(notifier, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    PublishOperationSnapshot(
                        operationId,
                        loadingOperationKind,
                        RuntimeLoadingOperationStage.Canceled,
                        RuntimeLoadingOperationState.Canceled,
                        percent: 0d,
                        currentStep: 0,
                        totalSteps: 1,
                        message: $"Operation '{operationCode}' canceled by caller.");
                    throw;
                }
                catch (RuntimeHealthCriticalException critical)
                {
                    PublishOperationSnapshot(
                        operationId,
                        loadingOperationKind,
                        RuntimeLoadingOperationStage.Failed,
                        RuntimeLoadingOperationState.Failed,
                        percent: 0d,
                        currentStep: 0,
                        totalSteps: 1,
                        message: critical.Message,
                        error: critical);
                    var recovered = await TryAutoRecoverSessionAsync(critical, cancellationToken).ConfigureAwait(false);
                    if (!recovered)
                        throw;

                    if (!retryAfterRecovery)
                        return;

                    if (attempt >= maxAttempts)
                        throw;
                }
                catch (Exception ex)
                {
                    PublishOperationSnapshot(
                        operationId,
                        loadingOperationKind,
                        RuntimeLoadingOperationStage.Failed,
                        RuntimeLoadingOperationState.Failed,
                        percent: 0d,
                        currentStep: 0,
                        totalSteps: 1,
                        message: ex.Message,
                        error: ex);
                    var classification = _errorClassifier.Classify(ex);
                    var canRetry = _retryPolicy.Enabled
                                   && classification.IsRetryable
                                   && attempt < maxAttempts;

                    var delay = canRetry
                        ? ComputeRetryDelay(attempt)
                        : TimeSpan.Zero;

                    _retryObserver.OnRetryDecision(new RuntimeRetryDecision(
                        operationCode,
                        attempt,
                        maxAttempts,
                        delay,
                        classification,
                        ex,
                        canRetry));

                    if (!canRetry)
                        throw;

                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private IInitializationProgressNotifier CreateProgressNotifier(
            string operationId,
            RuntimeLoadingOperationKind operationKind,
            bool splitOperationPerScope)
        {
            var loadingNotifier = new RuntimeLoadingProgressNotifierAdapter(
                _loadingProgressObserver,
                operationKind,
                operationId,
                splitOperationPerScope: splitOperationPerScope);
            return new CompositeInitializationProgressNotifier(_progressNotifier, loadingNotifier);
        }

        private void PublishOperationSnapshot(
            string operationId,
            RuntimeLoadingOperationKind operationKind,
            RuntimeLoadingOperationStage stage,
            RuntimeLoadingOperationState state,
            double percent,
            int currentStep,
            int totalSteps,
            string? message,
            Exception? error = null)
        {
            _loadingProgressObserver.OnLoadingProgress(
                new RuntimeLoadingOperationSnapshot(
                    operationId: operationId,
                    operationKind: operationKind,
                    stage: stage,
                    state: state,
                    scopeKey: null,
                    scopeName: null,
                    percent: percent,
                    currentStep: currentStep,
                    totalSteps: totalSteps,
                    message: message,
                    timestampUtc: DateTimeOffset.UtcNow,
                    errorType: error?.GetType().Name,
                    errorMessage: error?.Message));
        }

        private TimeSpan ComputeRetryDelay(int attempt)
        {
            var initialMs = Math.Max(0.0d, _retryPolicy.InitialBackoff.TotalMilliseconds);
            var multiplier = Math.Max(1.0d, _retryPolicy.BackoffMultiplier);
            var maxMs = Math.Max(initialMs, _retryPolicy.MaxBackoff.TotalMilliseconds);
            var exponent = Math.Max(0, attempt - 1);
            var delayMs = initialMs * Math.Pow(multiplier, exponent);
            delayMs = Math.Min(delayMs, maxMs);

            if (_retryPolicy.UseJitter && delayMs > 1.0d)
            {
                var jitterFactor = 0.85d + JitterRandom.NextDouble() * 0.30d;
                delayMs *= jitterFactor;
            }

            return TimeSpan.FromMilliseconds(delayMs);
        }

        private async Task<bool> TryAutoRecoverSessionAsync(
            RuntimeHealthCriticalException critical,
            CancellationToken cancellationToken)
        {
            if (!_builder.CanRestartSession())
                return false;

            var anomaly = new RuntimeHealthAnomaly(
                RuntimeHealthStatus.Critical,
                critical.Scope,
                critical.ServiceType,
                critical.Message,
                critical);

            if (!_healthSupervisor.TryBeginSessionRecovery(anomaly, out _, out _))
                return false;

            _statusObserver?.Invoke(
                RuntimeExecutionState.Recovering,
                $"Recovering session after critical anomaly in '{critical.ServiceType.Name}'.");
            await _builder.RestartSessionAsync(_progressNotifier, cancellationToken).ConfigureAwait(false);
            _statusObserver?.Invoke(
                RuntimeExecutionState.Degraded,
                $"Session recovered after critical anomaly in '{critical.ServiceType.Name}'.");
            return true;
        }
    }
}
