using RuntimeFlow.Contexts;
using RuntimeFlow.Initialization.Graph;

namespace RuntimeFlow.Tests;

public sealed partial class ScopeActivationEngineTests
{
    [Fact]
    public void ShouldUseCompiledInitializationGraph_EmptyVersion_ReturnsFalse()
    {
        var result = GameContextBuilder.ShouldUseCompiledInitializationGraph(string.Empty);

        Assert.False(result);
    }

    [Fact]
    public void ShouldUseCompiledInitializationGraph_MatchingVersion_ReturnsTrue()
    {
        var result = GameContextBuilder.ShouldUseCompiledInitializationGraph(InitializationGraphRules.Version);

        Assert.True(result);
    }

    [Fact]
    public void ShouldUseCompiledInitializationGraph_MismatchedVersion_ThrowsClearException()
    {
        var actualVersion = "compiled-constructor-v1";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            GameContextBuilder.ShouldUseCompiledInitializationGraph(actualVersion));

        Assert.Contains("Compiled initialization graph rule version mismatch.", exception.Message);
        Assert.Contains($"Expected '{InitializationGraphRules.Version}'", exception.Message);
        Assert.Contains($"actual '{actualVersion}'", exception.Message);
    }
}
