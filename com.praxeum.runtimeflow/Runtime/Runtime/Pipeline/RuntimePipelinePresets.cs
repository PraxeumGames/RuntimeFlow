using System;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Pre-built configuration presets for common RuntimePipeline scenarios.
    /// Use as the configureOptions parameter in RuntimePipeline.Create().
    /// </summary>
    public static class RuntimePipelinePresets
    {
        /// <summary>
        /// Minimal preset: all defaults, no health supervision, no special retry/SLO behavior.
        /// Suitable for simple applications or unit testing.
        /// </summary>
        public static readonly Action<RuntimePipelineOptions> Minimal = _ => { };

        /// <summary>
        /// Development preset: relaxed timeouts, no strict health enforcement.
        /// Suitable for local development and debugging.
        /// </summary>
        public static readonly Action<RuntimePipelineOptions> Development = options =>
        {
            options.Health.Enabled = false;
            options.RetryPolicy.MaxAttempts = 0;
        };

        /// <summary>
        /// Production preset: health supervision enabled with reasonable defaults,
        /// retry with backoff, and startup SLO tracking.
        /// </summary>
        public static readonly Action<RuntimePipelineOptions> Production = options =>
        {
            options.Health.Enabled = true;
            options.Health.MinimumExpectedServiceDuration = TimeSpan.FromMilliseconds(500);
            options.Health.MinimumServiceTimeout = TimeSpan.FromSeconds(5);
            options.Health.MaximumServiceTimeout = TimeSpan.FromSeconds(30);
            options.Health.SlowServiceMultiplier = 2.0;
            options.Health.MaxAutoSessionRestartsPerRun = 1;
            options.RetryPolicy.MaxAttempts = 2;
        };
    }
}
