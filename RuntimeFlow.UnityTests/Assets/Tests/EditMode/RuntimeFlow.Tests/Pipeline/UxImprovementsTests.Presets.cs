using NUnit.Framework;
using System;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{
    public sealed partial class UxImprovementsTests
    {
        [Test]
        public void Presets_Minimal_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
                RuntimePipeline.Create(_ => { }, RuntimePipelinePresets.Minimal));
        }

        [Test]
        public void Presets_Development_DisablesHealth()
        {
            var options = new RuntimePipelineOptions();
            RuntimePipelinePresets.Development(options);

            Assert.That(options.Health.Enabled, Is.False);
        }

        [Test]
        public void Presets_Production_EnablesHealthWithExpectedDefaults()
        {
            var options = new RuntimePipelineOptions();
            RuntimePipelinePresets.Production(options);

            Assert.That(options.Health.Enabled, Is.True);
            Assert.That(options.Health.MinimumExpectedServiceDuration, Is.EqualTo(TimeSpan.FromMilliseconds(500)));
            Assert.That(options.Health.MinimumServiceTimeout, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(options.Health.MaximumServiceTimeout, Is.EqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(options.Health.SlowServiceMultiplier, Is.EqualTo(2.0));
            Assert.That(options.Health.MaxAutoSessionRestartsPerRun, Is.EqualTo(1));
        }

        [Test]
        public void Presets_AllCanBeUsedWithRuntimePipelineCreate()
        {
            Assert.DoesNotThrow(() => RuntimePipeline.Create(_ => { }, RuntimePipelinePresets.Minimal));
            Assert.DoesNotThrow(() => RuntimePipeline.Create(_ => { }, RuntimePipelinePresets.Development));
            Assert.DoesNotThrow(() => RuntimePipeline.Create(_ => { }, RuntimePipelinePresets.Production));
        }
    }

}
