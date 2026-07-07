using NUnit.Framework;
using System;
using RuntimeFlow.Contexts;
using RuntimeFlow.Initialization.Graph;

namespace RuntimeFlow.Tests
{

public sealed partial class ScopeActivationEngineTests
{
    [Test]
    public void ShouldUseCompiledInitializationGraph_EmptyVersion_ReturnsFalse()
    {
        var result = GameContextBuilder.ShouldUseCompiledInitializationGraph(string.Empty);

        Assert.IsFalse(result);
    }

    [Test]
    public void ShouldUseCompiledInitializationGraph_MatchingVersion_ReturnsTrue()
    {
        var result = GameContextBuilder.ShouldUseCompiledInitializationGraph(InitializationGraphRules.Version);

        Assert.IsTrue(result);
    }

    [Test]
    public void ShouldUseCompiledInitializationGraph_MismatchedVersion_ThrowsClearException()
    {
        var actualVersion = "compiled-constructor-v1";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            GameContextBuilder.ShouldUseCompiledInitializationGraph(actualVersion));

        Assert.That(exception.Message, Does.Contain("Compiled initialization graph rule version mismatch."));
        Assert.That(exception.Message, Does.Contain($"Expected '{InitializationGraphRules.Version}'"));
        Assert.That(exception.Message, Does.Contain($"actual '{actualVersion}'"));
    }
}

}
