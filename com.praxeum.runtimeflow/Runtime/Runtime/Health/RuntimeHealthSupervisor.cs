using System;
using System.Threading;

namespace RuntimeFlow.Contexts
{
    internal sealed class RuntimeHealthSupervisor
    {
        private readonly RuntimeHealthOptions _options;
        private readonly IRuntimeHealthBaselineStore _baselineStore;
        private readonly IRuntimeHealthEvaluator _evaluator;
        private readonly IRuntimeHealthObserver _observer;
        private readonly string _deviceProfile;
        private int _autoSessionRestartsThisRun;

        public RuntimeHealthSupervisor(
            RuntimeHealthOptions options,
            IRuntimeHealthBaselineStore baselineStore,
            IRuntimeHealthEvaluator evaluator,
            IRuntimeHealthObserver observer)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _baselineStore = baselineStore ?? throw new ArgumentNullException(nameof(baselineStore));
            _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
            _observer = observer ?? throw new ArgumentNullException(nameof(observer));
            _deviceProfile = (_options.DeviceProfileResolver?.Invoke() ?? "default").Trim();
            if (string.IsNullOrWhiteSpace(_deviceProfile))
                _deviceProfile = "default";
        }

        public static RuntimeHealthSupervisor Disabled { get; } = new RuntimeHealthSupervisor(
            new RuntimeHealthOptions { Enabled = false },
            InMemoryRuntimeHealthBaselineStore.Shared,
            DefaultRuntimeHealthEvaluator.Instance,
            NullRuntimeHealthObserver.Instance);

        public bool IsEnabled => _options.Enabled;
        internal RuntimeHealthOptions Options => _options;
        public int MaxAutoSessionRestartsPerRun => Math.Max(0, _options.MaxAutoSessionRestartsPerRun);

        public static RuntimeHealthSupervisor Create(RuntimePipelineOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            return new RuntimeHealthSupervisor(
                options.Health,
                options.HealthBaselineStore ?? InMemoryRuntimeHealthBaselineStore.Shared,
                options.HealthEvaluator ?? DefaultRuntimeHealthEvaluator.Instance,
                options.HealthObserver ?? NullRuntimeHealthObserver.Instance);
        }

        public void BeginRun()
        {
            _autoSessionRestartsThisRun = 0;
        }

        public TimeSpan GetServiceTimeout(GameContextType scope, Type serviceType)
        {
            if (!IsEnabled)
                return Timeout.InfiniteTimeSpan;

            if (_options.ServiceTimeoutOverrides.TryGetValue(serviceType, out var overrideTimeout))
                return overrideTimeout;

            var expected = _options.MinimumExpectedServiceDuration;
            if (_baselineStore.TryGetBaseline(_deviceProfile, scope, serviceType, out var baseline))
            {
                expected = Max(expected, baseline);
            }

            var timeoutMs = expected.TotalMilliseconds * Math.Max(1.0d, _options.SlowServiceMultiplier);
            var timeout = TimeSpan.FromMilliseconds(timeoutMs);
            timeout = Max(timeout, _options.MinimumServiceTimeout);
            timeout = Min(timeout, _options.MaximumServiceTimeout);
            return timeout;
        }

        public RuntimeHealthEvaluation RecordServiceSuccess(
            GameContextType scope,
            Type serviceType,
            TimeSpan duration,
            TimeSpan timeout)
        {
            if (!IsEnabled)
                return RuntimeHealthEvaluation.Healthy;

            _baselineStore.Record(_deviceProfile, scope, serviceType, duration);
            var metric = new RuntimeServiceHealthMetric(
                _deviceProfile, scope, serviceType, duration, timeout, succeeded: true, DateTimeOffset.UtcNow);
            _observer.OnServiceMetric(metric);

            var evaluation = _evaluator.EvaluateSuccess(metric);
            if (evaluation.Status != RuntimeHealthStatus.Healthy)
            {
                _observer.OnAnomaly(new RuntimeHealthAnomaly(evaluation.Status, scope, serviceType, evaluation.Message));
            }

            return evaluation;
        }

        public RuntimeHealthAnomaly RecordServiceFailure(
            GameContextType scope,
            Type serviceType,
            TimeSpan duration,
            TimeSpan timeout,
            Exception exception)
        {
            var metric = new RuntimeServiceHealthMetric(
                _deviceProfile, scope, serviceType, duration, timeout, succeeded: false, DateTimeOffset.UtcNow);
            _observer.OnServiceMetric(metric);

            var evaluation = _evaluator.EvaluateFailure(metric, exception);
            var anomaly = new RuntimeHealthAnomaly(evaluation.Status, scope, serviceType, evaluation.Message, exception);
            _observer.OnAnomaly(anomaly);
            return anomaly;
        }

        public bool TryBeginSessionRecovery(RuntimeHealthAnomaly anomaly, out int attempt, out int maxAttempts)
        {
            attempt = 0;
            maxAttempts = MaxAutoSessionRestartsPerRun;

            if (!IsEnabled || anomaly.Status != RuntimeHealthStatus.Critical || maxAttempts <= 0)
                return false;
            if (_autoSessionRestartsThisRun >= maxAttempts)
                return false;

            _autoSessionRestartsThisRun++;
            attempt = _autoSessionRestartsThisRun;
            _observer.OnRecoveryTriggered(anomaly, attempt, maxAttempts);
            return true;
        }

        private static TimeSpan Max(TimeSpan left, TimeSpan right)
        {
            return left >= right ? left : right;
        }

        private static TimeSpan Min(TimeSpan left, TimeSpan right)
        {
            return left <= right ? left : right;
        }

        private sealed class NullRuntimeHealthObserver : IRuntimeHealthObserver
        {
            public static readonly IRuntimeHealthObserver Instance = new NullRuntimeHealthObserver();

            public void OnServiceMetric(RuntimeServiceHealthMetric metric) { }
            public void OnAnomaly(RuntimeHealthAnomaly anomaly) { }
            public void OnRecoveryTriggered(RuntimeHealthAnomaly anomaly, int attempt, int maxAttempts) { }
        }

        private sealed class DefaultRuntimeHealthEvaluator : IRuntimeHealthEvaluator
        {
            public static readonly IRuntimeHealthEvaluator Instance = new DefaultRuntimeHealthEvaluator();

            public RuntimeHealthEvaluation EvaluateSuccess(RuntimeServiceHealthMetric metric)
            {
                if (metric.ExpectedMaxDuration == Timeout.InfiniteTimeSpan)
                    return RuntimeHealthEvaluation.Healthy;

                if (metric.Duration > metric.ExpectedMaxDuration)
                {
                    return new RuntimeHealthEvaluation(
                        RuntimeHealthStatus.Critical,
                        $"Service '{metric.ServiceType.Name}' exceeded expected timeout.");
                }

                var degradedThreshold = TimeSpan.FromTicks((long)(metric.ExpectedMaxDuration.Ticks * 0.75d));
                if (metric.Duration >= degradedThreshold)
                {
                    return new RuntimeHealthEvaluation(
                        RuntimeHealthStatus.Degraded,
                        $"Service '{metric.ServiceType.Name}' is close to timeout threshold.");
                }

                return RuntimeHealthEvaluation.Healthy;
            }

            public RuntimeHealthEvaluation EvaluateFailure(RuntimeServiceHealthMetric metric, Exception exception)
            {
                if (exception is RuntimeHealthCriticalException)
                {
                    return new RuntimeHealthEvaluation(
                        RuntimeHealthStatus.Critical,
                        $"Critical health anomaly in service '{metric.ServiceType.Name}'.");
                }

                return new RuntimeHealthEvaluation(
                    RuntimeHealthStatus.Degraded,
                    $"Service '{metric.ServiceType.Name}' failed: {exception.GetType().Name}.");
            }
        }
    }
}
