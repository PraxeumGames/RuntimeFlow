using System;
using System.Runtime.InteropServices;

namespace RuntimeFlow.Contexts
{
    public enum RuntimeHealthStatus
    {
        Healthy = 0,
        Degraded = 1,
        Critical = 2
    }

    public sealed class RuntimeHealthOptions
    {
        public bool Enabled { get; set; } = true;
        public TimeSpan MinimumExpectedServiceDuration { get; set; } = TimeSpan.FromMilliseconds(250);
        public TimeSpan MinimumServiceTimeout { get; set; } = TimeSpan.FromSeconds(5);
        public TimeSpan MaximumServiceTimeout { get; set; } = TimeSpan.FromSeconds(90);
        public double SlowServiceMultiplier { get; set; } = 4.0;
        public int MaxAutoSessionRestartsPerRun { get; set; } = 1;
        /// <summary>
        /// Timeout for a single initialization wave. If all services in a wave remain in-flight
        /// beyond this duration, a diagnostic warning is logged. Does not abort — the outer
        /// cancellation token controls hard failure.
        /// Set to <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to disable (default).
        /// </summary>
        public TimeSpan WaveStallTimeout { get; set; } = System.Threading.Timeout.InfiniteTimeSpan;

        public Func<string>? DeviceProfileResolver { get; set; } = DefaultDeviceProfileResolver;

        private static string DefaultDeviceProfileResolver()
        {
            return $"{RuntimeInformation.OSArchitecture}/{RuntimeInformation.ProcessArchitecture}/{Environment.ProcessorCount}c";
        }
    }

    public interface IRuntimeHealthBaselineStore
    {
        bool TryGetBaseline(string deviceProfile, GameContextType scope, Type serviceType, out TimeSpan baseline);
        void Record(string deviceProfile, GameContextType scope, Type serviceType, TimeSpan duration);
    }

    public interface IRuntimeHealthEvaluator
    {
        RuntimeHealthEvaluation EvaluateSuccess(RuntimeServiceHealthMetric metric);
        RuntimeHealthEvaluation EvaluateFailure(RuntimeServiceHealthMetric metric, Exception exception);
    }

    public interface IRuntimeHealthObserver
    {
        void OnServiceMetric(RuntimeServiceHealthMetric metric);
        void OnAnomaly(RuntimeHealthAnomaly anomaly);
        void OnRecoveryTriggered(RuntimeHealthAnomaly anomaly, int attempt, int maxAttempts);
    }

    public sealed class RuntimeServiceHealthMetric
    {
        public RuntimeServiceHealthMetric(
            string deviceProfile,
            GameContextType scope,
            Type serviceType,
            TimeSpan duration,
            TimeSpan expectedMaxDuration,
            bool succeeded,
            DateTimeOffset timestamp)
        {
            DeviceProfile = deviceProfile;
            Scope = scope;
            ServiceType = serviceType;
            Duration = duration;
            ExpectedMaxDuration = expectedMaxDuration;
            Succeeded = succeeded;
            Timestamp = timestamp;
        }

        public string DeviceProfile { get; }
        public GameContextType Scope { get; }
        public Type ServiceType { get; }
        public TimeSpan Duration { get; }
        public TimeSpan ExpectedMaxDuration { get; }
        public bool Succeeded { get; }
        public DateTimeOffset Timestamp { get; }
    }

    public sealed class RuntimeHealthEvaluation
    {
        public RuntimeHealthEvaluation(RuntimeHealthStatus status, string message)
        {
            Status = status;
            Message = message;
        }

        public RuntimeHealthStatus Status { get; }
        public string Message { get; }

        public static readonly RuntimeHealthEvaluation Healthy = new(RuntimeHealthStatus.Healthy, "Healthy");
    }

    public sealed class RuntimeHealthAnomaly
    {
        public RuntimeHealthAnomaly(
            RuntimeHealthStatus status,
            GameContextType scope,
            Type serviceType,
            string message,
            Exception? exception = null)
        {
            Status = status;
            Scope = scope;
            ServiceType = serviceType;
            Message = message;
            Exception = exception;
        }

        public RuntimeHealthStatus Status { get; }
        public GameContextType Scope { get; }
        public Type ServiceType { get; }
        public string Message { get; }
        public Exception? Exception { get; }
    }

    public sealed class RuntimeHealthCriticalException : TimeoutException
    {
        public RuntimeHealthCriticalException(
            GameContextType scope,
            Type serviceType,
            TimeSpan threshold,
            TimeSpan elapsed,
            Exception? innerException = null)
            : base(
                $"Health anomaly: service '{serviceType.Name}' in scope '{scope}' exceeded timeout. " +
                $"Elapsed={elapsed.TotalMilliseconds:F0}ms, Threshold={threshold.TotalMilliseconds:F0}ms.",
                innerException)
        {
            Scope = scope;
            ServiceType = serviceType;
            Threshold = threshold;
            Elapsed = elapsed;
        }

        public GameContextType Scope { get; }
        public Type ServiceType { get; }
        public TimeSpan Threshold { get; }
        public TimeSpan Elapsed { get; }
    }
}
