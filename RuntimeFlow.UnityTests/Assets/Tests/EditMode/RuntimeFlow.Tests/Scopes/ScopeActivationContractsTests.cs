using NUnit.Framework;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{

public sealed class ScopeActivationContractsTests
{
    [Test]
    public void ScopeActivationMarkers_InheritAsyncScopeActivationService()
    {
        Assert.IsTrue(typeof(IAsyncScopeActivationService).IsAssignableFrom(typeof(ISessionScopeActivationService)));
        Assert.IsTrue(typeof(IAsyncScopeActivationService).IsAssignableFrom(typeof(ISceneScopeActivationService)));
        Assert.IsTrue(typeof(IAsyncScopeActivationService).IsAssignableFrom(typeof(IModuleScopeActivationService)));
    }

    [Test]
    public void AsyncScopeActivationContract_DeclaresExpectedLifecycleMethods()
    {
        var contractType = typeof(IAsyncScopeActivationService);
        var activatedMethod = contractType.GetMethod(nameof(IAsyncScopeActivationService.OnScopeActivatedAsync));
        var deactivatingMethod = contractType.GetMethod(nameof(IAsyncScopeActivationService.OnScopeDeactivatingAsync));

        Assert.IsNotNull(activatedMethod);
        Assert.IsNotNull(deactivatingMethod);
        Assert.AreEqual(typeof(Task), activatedMethod!.ReturnType);
        Assert.AreEqual(typeof(Task), deactivatingMethod!.ReturnType);

        var activatedParameters = activatedMethod.GetParameters();
        var deactivatingParameters = deactivatingMethod.GetParameters();
        Assert.That(activatedParameters, Has.Length.EqualTo(1));
        Assert.That(deactivatingParameters, Has.Length.EqualTo(1));
        Assert.AreEqual(typeof(CancellationToken), activatedParameters[0].ParameterType);
        Assert.AreEqual(typeof(CancellationToken), deactivatingParameters[0].ParameterType);
        Assert.AreEqual(2, contractType.GetMethods().Length);
    }

    [Test]
    public void ScopeActivationMarkers_ExcludeGlobalScope()
    {
        var markerTypes = typeof(IAsyncScopeActivationService).Assembly
            .GetTypes()
            .Where(type => type.IsInterface
                           && type != typeof(IAsyncScopeActivationService)
                           && typeof(IAsyncScopeActivationService).IsAssignableFrom(type))
            .ToArray();

        Assert.That(markerTypes, Has.Length.EqualTo(3));
        Assert.That(markerTypes, Does.Contain(typeof(ISessionScopeActivationService)));
        Assert.That(markerTypes, Does.Contain(typeof(ISceneScopeActivationService)));
        Assert.That(markerTypes, Does.Contain(typeof(IModuleScopeActivationService)));
        Assert.That(markerTypes.Any(type => string.Equals(type.Name, "IGlobalScopeActivationService", StringComparison.Ordinal)), Is.False);
    }
}

}
