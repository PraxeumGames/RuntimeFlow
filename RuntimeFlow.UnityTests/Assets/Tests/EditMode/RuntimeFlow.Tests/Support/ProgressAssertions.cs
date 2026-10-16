using System.Collections.Generic;
using NUnit.Framework;

namespace RuntimeFlow.Tests.Support
{
    /// <summary>Assertions for the weighted progress percentage and the snapshots it comes from.</summary>
    public static class ProgressAssertions
    {
        /// <summary>Asserts the percentage equals <paramref name="expected"/> within 0.05.</summary>
        public static void Percent(RuntimeFlowStatus status, double expected)
            => Assert.That(status.Percent, Is.EqualTo(expected).Within(0.05),
                $"expected {expected}% but the snapshot says {status.Percent}% ({status})");

        /// <summary>Asserts the sequence never decreases.</summary>
        public static void Monotonic(IReadOnlyList<double> samples)
        {
            for (var i = 1; i < samples.Count; i++)
            {
                Assert.That(samples[i], Is.GreaterThanOrEqualTo(samples[i - 1]),
                    $"progress went backwards at sample {i}: {samples[i - 1]} -> {samples[i]}");
            }
        }

        /// <summary>Asserts the run reports every service as finished.</summary>
        public static void AllCompleted(RuntimeFlowStatus status)
        {
            Assert.That(status.CompletedCount, Is.EqualTo(status.TotalCount), status.ToString());
            Percent(status, 100.0);
        }
    }
}
