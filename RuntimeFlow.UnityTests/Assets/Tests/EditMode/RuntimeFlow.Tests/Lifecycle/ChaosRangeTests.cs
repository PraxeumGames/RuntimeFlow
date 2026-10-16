using System.Linq;
using NUnit.Framework;

namespace RuntimeFlow.Tests.Lifecycle
{
    public sealed class ChaosRangeTests
    {
        [Test]
        public void UnsetRangeProducesOnlyTheNoSweepSentinel()
        {
            Assert.That(RestartChaosTests.ParseSweepBatches(null), Is.EqualTo(new[] { -1 }));
        }

        [Test]
        public void RegularRunKeepsTheThreeFastBatches()
        {
            Assert.That(RestartChaosTests.FastBatches, Is.EqualTo(new[] { 0, 1, 2 }));
        }

        [TestCase("0,3", "0,1,2")]
        [TestCase("12,2", "12,13")]
        [TestCase("85899344,1", "85899344")]
        public void ValidRangeProducesEveryRequestedBatch(string configuredRange, string expectedBatches)
        {
            var expected = expectedBatches.Split(',').Select(value => int.Parse(value)).ToArray();
            Assert.That(RestartChaosTests.ParseSweepBatches(configuredRange).ToArray(), Is.EqualTo(expected));
        }

        [TestCase("")]
        [TestCase(",1")]
        [TestCase("1,")]
        [TestCase("1")]
        [TestCase("1,2,3")]
        [TestCase("-1,1")]
        [TestCase("0,-1")]
        [TestCase("0,0")]
        [TestCase("+1,1")]
        [TestCase(" 1,1")]
        [TestCase("1, 1")]
        [TestCase("2147483648,1")]
        [TestCase("0,2147483648")]
        [TestCase("85899345,1")]
        [TestCase("85899344,2")]
        public void InvalidRangeFailsWithItsValue(string configuredRange)
        {
            var error = Assert.Throws<System.FormatException>(() =>
                RestartChaosTests.ParseSweepBatches(configuredRange).ToArray());

            Assert.That(error!.Message, Does.Contain(configuredRange));
            Assert.That(error.Message, Does.Contain("RUNTIMEFLOW_CHAOS_BATCHES"));
        }
    }
}
