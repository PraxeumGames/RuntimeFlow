using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    public enum PreBootstrapStageStatus
    {
        NotStarted = 0,
        Running = 1,
        Succeeded = 2,
        Failed = 3
    }

    public class PreBootstrapStagePolicy
    {
        public static readonly PreBootstrapStagePolicy Default = new PreBootstrapStagePolicy();

        public int MaxAttempts { get; set; } = 2;
        public int AttemptTimeoutMs { get; set; } = 15000;
        public int RetryDelayMs { get; set; } = 300;
        public bool UseExponentialBackoff { get; set; }
        public int BackoffMultiplierPercent { get; set; } = 200;
        public bool UseJitter { get; set; }
        public int JitterPercent { get; set; } = 20;
    }

    public enum PreBootstrapFailureKind
    {
        Unknown = 0,
        Timeout = 1,
        Canceled = 2,
        Transient = 3,
        Permanent = 4
    }

    public sealed class PreBootstrapFailureClassification
    {
        public static readonly PreBootstrapFailureClassification Transient =
            new PreBootstrapFailureClassification(PreBootstrapFailureKind.Transient, isRetryable: true);

        public static readonly PreBootstrapFailureClassification Permanent =
            new PreBootstrapFailureClassification(PreBootstrapFailureKind.Permanent, isRetryable: false);

        public static readonly PreBootstrapFailureClassification Timeout =
            new PreBootstrapFailureClassification(PreBootstrapFailureKind.Timeout, isRetryable: true);

        public PreBootstrapFailureClassification(
            PreBootstrapFailureKind failureKind,
            bool isRetryable,
            string reasonCode = null,
            string diagnostic = null)
        {
            FailureKind = failureKind;
            IsRetryable = isRetryable;
            ReasonCode = Normalize(reasonCode);
            Diagnostic = Normalize(diagnostic);
        }

        public PreBootstrapFailureKind FailureKind { get; }
        public bool IsRetryable { get; }
        public string ReasonCode { get; }
        public string Diagnostic { get; }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public interface IPreBootstrapFailureClassifier
    {
        PreBootstrapFailureClassification Classify(Exception exception);
    }

    public sealed class DefaultPreBootstrapFailureClassifier : IPreBootstrapFailureClassifier
    {
        public static readonly IPreBootstrapFailureClassifier Instance = new DefaultPreBootstrapFailureClassifier();

        private DefaultPreBootstrapFailureClassifier()
        {
        }

        public PreBootstrapFailureClassification Classify(Exception exception)
        {
            if (exception == null)
                return new PreBootstrapFailureClassification(PreBootstrapFailureKind.Unknown, isRetryable: false);

            if (exception is OperationCanceledException)
                return new PreBootstrapFailureClassification(PreBootstrapFailureKind.Canceled, isRetryable: false);

            if (exception is TimeoutException)
                return PreBootstrapFailureClassification.Timeout;

            return PreBootstrapFailureClassification.Transient;
        }
    }

    public sealed class PreBootstrapTransition<TStatus>
    {
        public PreBootstrapTransition(
            TStatus previousStatus,
            TStatus currentStatus,
            DateTimeOffset timestampUtc,
            int attempt = 0,
            string reasonCode = null,
            string diagnostic = null)
        {
            if (attempt < 0)
                throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "Attempt cannot be negative.");

            PreviousStatus = previousStatus;
            CurrentStatus = currentStatus;
            TimestampUtc = timestampUtc;
            Attempt = attempt;
            ReasonCode = Normalize(reasonCode);
            Diagnostic = Normalize(diagnostic);
        }

        public TStatus PreviousStatus { get; }
        public TStatus CurrentStatus { get; }
        public DateTimeOffset TimestampUtc { get; }
        public int Attempt { get; }
        public string ReasonCode { get; }
        public string Diagnostic { get; }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public sealed class PreBootstrapSnapshot<TStatus>
    {
        public PreBootstrapSnapshot(
            TStatus status,
            DateTimeOffset updatedAtUtc,
            int attempt = 0,
            string reasonCode = null,
            string diagnostic = null,
            string errorType = null,
            string errorMessage = null,
            PreBootstrapTransition<TStatus> lastTransition = null)
        {
            if (attempt < 0)
                throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "Attempt cannot be negative.");

            Status = status;
            UpdatedAtUtc = updatedAtUtc;
            Attempt = attempt;
            ReasonCode = Normalize(reasonCode);
            Diagnostic = Normalize(diagnostic);
            ErrorType = Normalize(errorType);
            ErrorMessage = Normalize(errorMessage);
            LastTransition = lastTransition;
        }

        public TStatus Status { get; }
        public DateTimeOffset UpdatedAtUtc { get; }
        public int Attempt { get; }
        public string ReasonCode { get; }
        public string Diagnostic { get; }
        public string ErrorType { get; }
        public string ErrorMessage { get; }
        public PreBootstrapTransition<TStatus> LastTransition { get; }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public interface IPreBootstrapStageService<TStatus>
    {
        TStatus Status { get; }
        Task EnsureCompletedAsync(CancellationToken cancellationToken = default);
    }

    public interface IPreBootstrapStageService : IPreBootstrapStageService<PreBootstrapStageStatus>
    {
    }

    public interface IPreBootstrapStageStateProvider<TStatus>
    {
        PreBootstrapSnapshot<TStatus> Snapshot { get; }
    }

    public interface IPreBootstrapTransitionObserver<TStatus>
    {
        void OnTransition(PreBootstrapTransition<TStatus> transition);
    }

    public readonly struct PreBootstrapProjectionStatusMap<TStatus>
    {
        private readonly IEqualityComparer<TStatus> _comparer;

        public PreBootstrapProjectionStatusMap(
            TStatus notStartedStatus,
            TStatus runningStatus,
            TStatus succeededStatus,
            TStatus failedStatus,
            IEqualityComparer<TStatus> comparer = null)
        {
            NotStartedStatus = notStartedStatus;
            RunningStatus = runningStatus;
            SucceededStatus = succeededStatus;
            FailedStatus = failedStatus;
            _comparer = comparer ?? EqualityComparer<TStatus>.Default;
        }

        public TStatus NotStartedStatus { get; }
        public TStatus RunningStatus { get; }
        public TStatus SucceededStatus { get; }
        public TStatus FailedStatus { get; }

        public bool IsNotStarted(TStatus status) => _comparer.Equals(status, NotStartedStatus);
        public bool IsRunning(TStatus status) => _comparer.Equals(status, RunningStatus);
        public bool IsSucceeded(TStatus status) => _comparer.Equals(status, SucceededStatus);
        public bool IsFailed(TStatus status) => _comparer.Equals(status, FailedStatus);
    }

    public static class PreBootstrapPipelineStageProjector
    {
        public static void Project<TStatus, TStage, TSnapshot>(
            IPreBootstrapStageService<TStatus> preBootstrapStageService,
            IRuntimePipelineStageStateProvider<TStage, TSnapshot> pipelineState,
            TStage pipelineStage,
            PreBootstrapProjectionStatusMap<TStatus> statusMap,
            Func<TStatus, string, string> reasonCodeResolver,
            string failedReasonCodeFallback = null,
            string failedDiagnosticFallback = null)
        {
            if (preBootstrapStageService == null || pipelineState == null)
            {
                return;
            }

            if (reasonCodeResolver == null)
                throw new ArgumentNullException(nameof(reasonCodeResolver));

            var snapshot = (preBootstrapStageService as IPreBootstrapStageStateProvider<TStatus>)?.Snapshot;
            var status = snapshot != null ? snapshot.Status : preBootstrapStageService.Status;
            if (statusMap.IsNotStarted(status))
            {
                return;
            }

            var reasonCode = Normalize(reasonCodeResolver(status, snapshot?.ReasonCode));
            var diagnostic = Normalize(snapshot?.Diagnostic);

            if (statusMap.IsRunning(status))
            {
                pipelineState.StartStage(pipelineStage, reasonCode, diagnostic);
                return;
            }

            if (statusMap.IsSucceeded(status))
            {
                pipelineState.CompleteStage(pipelineStage, reasonCode, diagnostic);
                return;
            }

            if (!statusMap.IsFailed(status))
            {
                return;
            }

            var failedReasonCode = reasonCode ?? Normalize(failedReasonCodeFallback);
            if (string.IsNullOrEmpty(failedReasonCode))
                throw new InvalidOperationException("Prebootstrap failed projection requires a reason code.");

            pipelineState.FailStage(
                pipelineStage,
                failedReasonCode,
                diagnostic: diagnostic ?? Normalize(failedDiagnosticFallback));
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

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
        private readonly IPreBootstrapTransitionObserver<TStatus> _transitionObserver;
        private readonly Random _jitterRandom = new Random();

        private TStatus _status;
        private int _attempt;
        private Task _executionTask;
        private PreBootstrapSnapshot<TStatus> _snapshot;
        private PreBootstrapTransition<TStatus> _lastTransition;

        protected PreBootstrapStageServiceBase(
            TStatus initialStatus,
            TStatus runningStatus,
            TStatus succeededStatus,
            TStatus failedStatus,
            PreBootstrapStagePolicy policy = null,
            IPreBootstrapFailureClassifier failureClassifier = null,
            IPreBootstrapTransitionObserver<TStatus> transitionObserver = null,
            Func<DateTimeOffset> timestampProvider = null)
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
            Exception lastException = null;

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
            string diagnostic,
            Exception error = null,
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
