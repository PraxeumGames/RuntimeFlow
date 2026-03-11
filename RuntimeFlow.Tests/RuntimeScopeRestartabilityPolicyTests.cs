using System;
using RuntimeFlow.Contexts;
using Xunit;

namespace RuntimeFlow.Tests;

public sealed class RuntimeScopeRestartabilityPolicyTests
{
    [Fact]
    public void IsRestartable_GlobalScope_ReturnsFalse()
    {
        Assert.False(RuntimeScopeRestartabilityPolicy.IsRestartable(GameContextType.Global));
    }

    [Theory]
    [InlineData(GameContextType.Session, RuntimeLoadingOperationKind.RestartSession)]
    [InlineData(GameContextType.Scene, RuntimeLoadingOperationKind.ReloadScene)]
    [InlineData(GameContextType.Module, RuntimeLoadingOperationKind.ReloadModule)]
    public void ResolveOperationKind_RestartableScope_ReturnsExpectedOperationKind(
        GameContextType scopeType,
        RuntimeLoadingOperationKind expectedOperationKind)
    {
        Assert.True(RuntimeScopeRestartabilityPolicy.IsRestartable(scopeType));
        Assert.Equal(expectedOperationKind, RuntimeScopeRestartabilityPolicy.ResolveOperationKind(scopeType));
    }

    [Fact]
    public void ResolveOperationKind_GlobalScope_ThrowsDeterministicException()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            RuntimeScopeRestartabilityPolicy.ResolveOperationKind(GameContextType.Global));

        Assert.Equal(RuntimeScopeRestartabilityPolicy.GlobalScopeNonRestartableMessage, exception.Message);
    }
}
