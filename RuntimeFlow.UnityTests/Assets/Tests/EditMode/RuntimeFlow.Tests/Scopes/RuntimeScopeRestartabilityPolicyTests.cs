using NUnit.Framework;
using System;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{

public sealed class RuntimeScopeRestartabilityPolicyTests
{
    [Test]
    public void IsRestartable_GlobalScope_ReturnsFalse()
    {
        Assert.IsFalse(RuntimeScopeRestartabilityPolicy.IsRestartable(GameContextType.Global));
    }

    [TestCase(GameContextType.Session, RuntimeLoadingOperationKind.RestartSession)]
    [TestCase(GameContextType.Scene, RuntimeLoadingOperationKind.ReloadScene)]
    [TestCase(GameContextType.Module, RuntimeLoadingOperationKind.ReloadModule)]
    public void ResolveOperationKind_RestartableScope_ReturnsExpectedOperationKind(
        GameContextType scopeType,
        RuntimeLoadingOperationKind expectedOperationKind)
    {
        Assert.IsTrue(RuntimeScopeRestartabilityPolicy.IsRestartable(scopeType));
        Assert.AreEqual(expectedOperationKind, RuntimeScopeRestartabilityPolicy.ResolveOperationKind(scopeType));
    }

    [Test]
    public void ResolveOperationKind_GlobalScope_ThrowsDeterministicException()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            RuntimeScopeRestartabilityPolicy.ResolveOperationKind(GameContextType.Global));

        Assert.AreEqual(RuntimeScopeRestartabilityPolicy.GlobalScopeNonRestartableMessage, exception.Message);
    }
}

}
