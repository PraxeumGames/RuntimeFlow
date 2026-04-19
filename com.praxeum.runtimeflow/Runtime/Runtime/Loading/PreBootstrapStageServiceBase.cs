using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Generic prebootstrap state machine with retries, timeout and failure classification.
    /// </summary>
    public abstract class PreBootstrapStageServiceBase<TStatus> :
        IPreBootstrapStageService<TStatus>,
        IPreBootstrapStageStateProvider<TStatus>
    {
        private readonly object _sync = new object();
        private readonly Func<DateTimeOffset> _timestampProvider;
        private readonly IPreBootstrapFailureClassifier _failureClassifier;
        private readonly PreBootstrapStagePolicy _policy;
        private readonly IPreBootstrapTransitionObserver<TStatus>? _transitionObserver;
        private readonly Random _jitterRandom = new Random();

        private TStatus _status;
        private int _attempt;
        private Task? _executionTask; // lazily created on first EnsureCompletedAsync call
        private PreBootstrapSnapshot<TStatus> _snapshot;
        private PreBootstrapTransition<TStatus>? _lastTransition; // null until first transition

        protected PreBootstrapStageServiceBase(
            TStatus initialStatus,
            TStatus runningStatus,
            TStatus succeededStatus,
            TStatus failedStatus,
            PreBootstrapStagePolicy? policy = null,
            IPreBootstrapFailureClassifier? failureClassifier = null,
            IPreBootstrapTransitionObserver<TStatus>? transitionObserver = null,
            Func<DateTimeOffset>? timestampProvider = null)
        {
            RunningStatus = runningStatus;
            SucceededStatus = succeededStatus;
            FailedStatus = failedStatus;
            _policy = policy ?? PreBootstrapStagePolicy.Default;
            _failureClassifier = failureClassifier ?? DefaultPreBootstrapFailureClassifier.Instance;
            _transitionObserver = transitionObserver;
            _timestampProvider = timestampProvider ?? (() => DateTimeOffset.UtcNow);
            _status = initialStatus;
            _snapshot = new PreBootstrapSnapshot<TStatus>(_status, _timestampProvider());
        }

        public TStatus Status
        {
            get
            {
                lock (_sync)
                {
                    return _status;
                }
            }
        }

        public PreBootstrapSnapshot<TStatus> Snapshot
        {
            get
            {
                lock (_sync)
                {
                    return _snapshot;
                }
            }
        }

        protected TStatus RunningStatus { get; }
        protected TStatus SucceededStatus { get; }
        protected TStatus FailedStatus { get; }

        public Task EnsureCompletedAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var executionTask = GetOrCreateExecutionTask();
            return WaitWithCancellationAsync(executionTask, cancellationToken);
        }

        protected abstract Task ExecuteAttemptAsync(int attempt, CancellationToken cancellationToken);

        protected virtual int ResolveAttemptTimeoutMs(int attempt)
        {
            return Math.Max(1, _policy.AttemptTimeoutMs);
        }

        protected virtual int ResolveRetryDelayMs(int attempt)
        {
            var baseDelayMs = Math.Max(0, _policy.RetryDelayMs);
            if (baseDelayMs == 0)
                return 0;

            var resolvedDelay = baseDelayMs;
            if (_policy.UseExponentialBackoff && attempt > 1)
            {
                var multiplier = Math.Max(100, _policy.BackoffMultiplierPercent) / 100d;
                resolvedDelay = (int)Math.Min(
                    int.MaxValue,
                    Math.Round(baseDelayMs * Math.Pow(multiplier, attempt - 1)));
            }

            if (_policy.UseJitter && resolvedDelay > 0)
            {
                var jitterPercent = Math.Max(0, _policy.JitterPercent);
                if (jitterPercent > 0)
                {
                    var spread = resolvedDelay * (jitterPercent / 100d);
                    var min = Math.Max(0, resolvedDelay - spread);
                    var max = resolvedDelay + spread;
                    lock (_jitterRandom)
                    {
                        resolvedDelay = (int)Math.Round(min + _jitterRandom.NextDouble() * (max - min));
                    }
                }
            }

            return Math.Max(0, resolvedDelay);
        }

        protected virtual PreBootstrapFailureClassification ClassifyFailure(Exception exception)
        {
            return _failureClassifier.Classify(exception);
        }

        protected virtual bool ShouldRetry(
            int attempt,
            int maxAttempts,
            PreBootstrapFailureClassification classification)
        {
            return attempt < maxAttempts && classification.IsRetryable;
        }

        protected virtual string ResolveFailureReasonCode(
            PreBootstrapFailureClassification classification)
        {
            if (!string.IsNullOrWhiteSpace(classification?.ReasonCode))
                return classification.ReasonCode;

            switch (classification?.FailureKind)
            {
                case PreBootstrapFailureKind.Timeout:
                    return "prebootstrap.timeout";
                case PreBootstrapFailureKind.Canceled:
                    return "prebootstrap.canceled";
                case PreBootstrapFailureKind.Transient:
                    return "prebootstrap.failed.transient";
                case PreBootstrapFailureKind.Permanent:
                    return "prebootstrap.failed.permanent";
                default:
                    return "prebootstrap.failed";
            }
        }

        protected virtual string ResolveRetryScheduledReasonCode() => "prebootstrap.retry.scheduled";
        protected virtual string ResolveRetryExhaustedReasonCode() => "prebootstrap.retry.exhausted";
        protected virtual string ResolveSucceededReasonCode() => "prebootstrap.succeeded";
        protected virtual string ResolveStartedReasonCode() => "prebootstrap.started";

        private Task GetOrCreateExecutionTask()
        {
            lock (_sync)
            {
                if (_executionTask != null)
                    return _executionTask;

                _executionTask = RunExecutionAsync();
                return _executionTask;
            }
        }

        private async Task RunExecutionAsync()
        {
            var maxAttempts = Math.Max(1, _policy.MaxAttempts);
            Exception? lastException = null;

            TransitionTo(RunningStatus, reasonCode: ResolveStartedReasonCode(), diagnostic: null, attempt: 1);

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var timeoutMs = ResolveAttemptTimeoutMs(attempt);
                    using (var timeoutCts = new CancellationTokenSource(timeoutMs))
                    {
                        try
                        {
                            await ExecuteAttemptAsync(attempt, timeoutCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException ex)
                        {
                            lastException = ex;
                            var classification = timeoutCts.IsCancellationRequested
                                ? new PreBootstrapFailureClassification(
                                    PreBootstrapFailureKind.Timeout,
                                    isRetryable: true,
                                    reasonCode: "prebootstrap.timeout")
                                : ClassifyFailure(ex);

                            var shouldRetry = ShouldRetry(attempt, maxAttempts, classification);
                            if (!shouldRetry)
                            {
                                TransitionTo(
                                    FailedStatus,
                                    reasonCode: ResolveFailureReasonCode(classification),
                                    diagnostic: classification.Diagnostic ?? ex.Message,
                                    error: ex,
                                    attempt: attempt);
                                throw;
                            }

                            await DelayBeforeRetryAsync(attempt, maxAttempts).ConfigureAwait(false);
                            continue;
                        }
                    }

                    TransitionTo(SucceededStatus, reasonCode: ResolveSucceededReasonCode(), diagnostic: null, attempt: attempt);
                    return;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    var classification = ClassifyFailure(ex);
                    var shouldRetry = ShouldRetry(attempt, maxAttempts, classification);
                    if (!shouldRetry)
                    {
                        TransitionTo(
                            FailedStatus,
                            reasonCode: ResolveFailureReasonCode(classification),
                            diagnostic: classification.Diagnostic ?? ex.Message,
                            error: ex,
                            attempt: attempt);
                        throw;
                    }

                    await DelayBeforeRetryAsync(attempt, maxAttempts).ConfigureAwait(false);
                }
            }

            TransitionTo(
                FailedStatus,
                reasonCode: ResolveRetryExhaustedReasonCode(),
                diagnostic: lastException?.Message ?? "Prebootstrap retries exhausted.",
                error: lastException,
                attempt: maxAttempts);
            throw lastException ?? new InvalidOperationException("Prebootstrap failed without exception details.");
        }

        private async Task DelayBeforeRetryAsync(int attempt, int maxAttempts)
        {
            var delayMs = ResolveRetryDelayMs(attempt);
            TransitionTo(
                RunningStatus,
                reasonCode: ResolveRetryScheduledReasonCode(),
                diagnostic: $"Retrying prebootstrap attempt {attempt + 1}/{maxAttempts}.",
                attempt: attempt + 1);

            if (delayMs <= 0)
                return;

            await Task.Delay(delayMs).ConfigureAwait(false);
        }

        private void TransitionTo(
            TStatus newStatus,
            string reasonCode,
            string? diagnostic,
            Exception? error = null,
            int attempt = 0)
        {
            PreBootstrapTransition<TStatus> transition;
            lock (_sync)
            {
                var previous = _status;
                _status = newStatus;
                _attempt = Math.Max(0, attempt);
                transition = new PreBootstrapTransition<TStatus>(
                    previousStatus: previous,
                    currentStatus: newStatus,
                    timestampUtc: _timestampProvider(),
                    attempt: _attempt,
                    reasonCode: reasonCode,
                    diagnostic: diagnostic);
                _lastTransition = transition;
                _snapshot = new PreBootstrapSnapshot<TStatus>(
                    status: newStatus,
                    updatedAtUtc: transition.TimestampUtc,
                    attempt: _attempt,
                    reasonCode: transition.ReasonCode,
                    diagnostic: transition.Diagnostic,
                    errorType: error?.GetType().Name,
                    errorMessage: error?.Message,
                    lastTransition: _lastTransition);
            }

            _transitionObserver?.OnTransition(transition);
        }

        private static async Task WaitWithCancellationAsync(Task task, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return;
            }

            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var completedTask = await Task.WhenAny(task, cancellationTask).ConfigureAwait(false);
            if (completedTask == cancellationTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            await task.ConfigureAwait(false);
        }
    }
}
