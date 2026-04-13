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

}
