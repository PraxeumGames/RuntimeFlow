using System;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

public sealed partial class UxImprovementsTests
{
    [Fact]
    public void Presets_Minimal_DoesNotThrow()
    {
        var exception = Record.Exception(() =>
            RuntimePipeline.Create(_ => { }, RuntimePipelinePresets.Minimal));

        Assert.Null(exception);
    }

    [Fact]
    public void Presets_Development_DisablesHealth()
    {
        var options = new RuntimePipelineOptions();
        RuntimePipelinePresets.Development(options);

        Assert.False(options.Health.Enabled);
    }

    [Fact]
    public void Presets_Production_EnablesHealthWithExpectedDefaults()
    {
        var options = new RuntimePipelineOptions();
        RuntimePipelinePresets.Production(options);

        Assert.True(options.Health.Enabled);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.Health.MinimumExpectedServiceDuration);
        Assert.Equal(TimeSpan.FromSeconds(5), options.Health.MinimumServiceTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Health.MaximumServiceTimeout);
        Assert.Equal(2.0, options.Health.SlowServiceMultiplier);
        Assert.Equal(1, options.Health.MaxAutoSessionRestartsPerRun);
    }

    [Fact]
    public void Presets_AllCanBeUsedWithRuntimePipelineCreate()
    {
        Assert.Null(Record.Exception(() => RuntimePipeline.Create(_ => { }, RuntimePipelinePresets.Minimal)));
        Assert.Null(Record.Exception(() => RuntimePipeline.Create(_ => { }, RuntimePipelinePresets.Development)));
        Assert.Null(Record.Exception(() => RuntimePipeline.Create(_ => { }, RuntimePipelinePresets.Production)));
    }
}
