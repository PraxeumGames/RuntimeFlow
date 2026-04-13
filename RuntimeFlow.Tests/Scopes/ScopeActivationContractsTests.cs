using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using Xunit;

namespace RuntimeFlow.Tests;

public sealed class ScopeActivationContractsTests
{
    [Fact]
    public void ScopeActivationMarkers_InheritAsyncScopeActivationService()
    {
        Assert.True(typeof(IAsyncScopeActivationService).IsAssignableFrom(typeof(ISessionScopeActivationService)));
        Assert.True(typeof(IAsyncScopeActivationService).IsAssignableFrom(typeof(ISceneScopeActivationService)));
        Assert.True(typeof(IAsyncScopeActivationService).IsAssignableFrom(typeof(IModuleScopeActivationService)));
    }

    [Fact]
    public void AsyncScopeActivationContract_DeclaresExpectedLifecycleMethods()
    {
        var contractType = typeof(IAsyncScopeActivationService);
        var activatedMethod = contractType.GetMethod(nameof(IAsyncScopeActivationService.OnScopeActivatedAsync));
        var deactivatingMethod = contractType.GetMethod(nameof(IAsyncScopeActivationService.OnScopeDeactivatingAsync));

        Assert.NotNull(activatedMethod);
        Assert.NotNull(deactivatingMethod);
        Assert.Equal(typeof(Task), activatedMethod!.ReturnType);
        Assert.Equal(typeof(Task), deactivatingMethod!.ReturnType);

        var activatedParameters = activatedMethod.GetParameters();
        var deactivatingParameters = deactivatingMethod.GetParameters();
        Assert.Single(activatedParameters);
        Assert.Single(deactivatingParameters);
        Assert.Equal(typeof(CancellationToken), activatedParameters[0].ParameterType);
        Assert.Equal(typeof(CancellationToken), deactivatingParameters[0].ParameterType);
        Assert.Equal(2, contractType.GetMethods().Length);
    }

    [Fact]
    public void ScopeActivationMarkers_ExcludeGlobalScope()
    {
        var markerTypes = typeof(IAsyncScopeActivationService).Assembly
            .GetTypes()
            .Where(type => type.IsInterface
                           && type != typeof(IAsyncScopeActivationService)
                           && typeof(IAsyncScopeActivationService).IsAssignableFrom(type))
            .ToArray();

        Assert.Equal(3, markerTypes.Length);
        Assert.Contains(typeof(ISessionScopeActivationService), markerTypes);
        Assert.Contains(typeof(ISceneScopeActivationService), markerTypes);
        Assert.Contains(typeof(IModuleScopeActivationService), markerTypes);
        Assert.DoesNotContain(markerTypes, type => string.Equals(type.Name, "IGlobalScopeActivationService", StringComparison.Ordinal));
    }
}
