using System;
using VContainer;
using System.Threading;

namespace RuntimeFlow.Contexts
{
    public enum RuntimeErrorKind
    {
        Transient = 0,
        Permanent = 1,
        Configuration = 2
    }

    public sealed class RuntimeErrorClassification
    {
        public RuntimeErrorClassification(
            RuntimeErrorKind kind,
            string code,
            string message,
            bool isRetryable)
        {
            Kind = kind;
            Code = string.IsNullOrWhiteSpace(code) ? "GBRT-UNKNOWN" : code;
            Message = string.IsNullOrWhiteSpace(message) ? "Runtime error." : message;
            IsRetryable = isRetryable;
        }

        public RuntimeErrorKind Kind { get; }
        public string Code { get; }
        public string Message { get; }
        public bool IsRetryable { get; }
    }

    public interface IRuntimeErrorClassifier
    {
        RuntimeErrorClassification Classify(Exception exception);
    }

    public sealed class RuntimeRetryPolicyOptions
    {
        public bool Enabled { get; set; } = true;
        public int MaxAttempts { get; set; } = 2;
        public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromMilliseconds(250);
        public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(3);
        public double BackoffMultiplier { get; set; } = 2.0;
        public bool UseJitter { get; set; } = true;
    }

    public sealed class RuntimeRetryDecision
    {
        public RuntimeRetryDecision(
            string operationCode,
            int attempt,
            int maxAttempts,
            TimeSpan delay,
            RuntimeErrorClassification classification,
            Exception exception,
            bool willRetry)
        {
            OperationCode = operationCode;
            Attempt = attempt;
            MaxAttempts = maxAttempts;
            Delay = delay;
            Classification = classification;
            Exception = exception;
            WillRetry = willRetry;
            Timestamp = DateTimeOffset.UtcNow;
        }

        public string OperationCode { get; }
        public int Attempt { get; }
        public int MaxAttempts { get; }
        public TimeSpan Delay { get; }
        public RuntimeErrorClassification Classification { get; }
        public Exception Exception { get; }
        public bool WillRetry { get; }
        public DateTimeOffset Timestamp { get; }
    }

    public interface IRuntimeRetryObserver
    {
        void OnRetryDecision(RuntimeRetryDecision decision);
    }

    internal sealed class DefaultRuntimeErrorClassifier : IRuntimeErrorClassifier
    {
        public static readonly IRuntimeErrorClassifier Instance = new DefaultRuntimeErrorClassifier();

        public RuntimeErrorClassification Classify(Exception exception)
        {
            if (exception == null) throw new ArgumentNullException(nameof(exception));

            if (exception is RuntimeHealthCriticalException)
            {
                return new RuntimeErrorClassification(
                    RuntimeErrorKind.Transient,
                    "GBRT-TRANSIENT-HEALTH-TIMEOUT",
                    "Critical health timeout detected.",
                    isRetryable: true);
            }

            if (exception is TimeoutException)
            {
                return new RuntimeErrorClassification(
                    RuntimeErrorKind.Transient,
                    "GBRT-TRANSIENT-TIMEOUT",
                    "Operation timed out.",
                    isRetryable: true);
            }

            if (exception is VContainerException)
            {
                return new RuntimeErrorClassification(
                    RuntimeErrorKind.Configuration,
                    "GBRT-CONFIG-DI",
                    "Dependency injection configuration is invalid.",
                    isRetryable: false);
            }

            if (exception is InvalidOperationException)
            {
                return new RuntimeErrorClassification(
                    RuntimeErrorKind.Configuration,
                    "GBRT-CONFIG-INVALID-OPERATION",
                    "Invalid runtime configuration or execution order.",
                    isRetryable: false);
            }

            return new RuntimeErrorClassification(
                RuntimeErrorKind.Permanent,
                "GBRT-PERMANENT-UNHANDLED",
                "Unhandled runtime exception.",
                isRetryable: false);
        }
    }

    internal sealed class NullRuntimeRetryObserver : IRuntimeRetryObserver
    {
        public static readonly IRuntimeRetryObserver Instance = new NullRuntimeRetryObserver();

        public void OnRetryDecision(RuntimeRetryDecision decision)
        {
        }
    }
}
