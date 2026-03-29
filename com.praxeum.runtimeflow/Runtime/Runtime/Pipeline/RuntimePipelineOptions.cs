using System;

namespace RuntimeFlow.Contexts
{
    public sealed class RuntimePipelineOptions
    {
        public IInitializationExecutionScheduler? ExecutionScheduler { get; set; }
        public RuntimeHealthOptions Health { get; } = new();
        public IRuntimeHealthBaselineStore? HealthBaselineStore { get; set; }
        public IRuntimeHealthEvaluator? HealthEvaluator { get; set; }
        public IRuntimeHealthObserver? HealthObserver { get; set; }
        public RuntimeRetryPolicyOptions RetryPolicy { get; } = new();
        public IRuntimeErrorClassifier? ErrorClassifier { get; set; }
        public IRuntimeRetryObserver? RetryObserver { get; set; }
        public IRuntimeLoadingProgressObserver? LoadingProgressObserver { get; set; }
        public bool ReplayFlowOnSessionRestart { get; set; }
    }
}
