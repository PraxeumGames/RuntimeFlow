using System;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

public sealed partial class UxImprovementsTests
{
    [Fact]
    public void ScopeNotDeclaredException_HasCorrectScopeType()
    {
        var exception = new ScopeNotDeclaredException(typeof(UndeclaredScope));

        Assert.Equal(typeof(UndeclaredScope), exception.ScopeType);
    }

    [Fact]
    public void ScopeNotRestartableException_HasCorrectMessage()
    {
        var exception = new ScopeNotRestartableException(typeof(GlobalScope));

        Assert.Equal(typeof(GlobalScope), exception.ScopeType);
    }

    [Fact]
    public void FlowNotConfiguredException_HasCorrectMessage()
    {
        var exception = new FlowNotConfiguredException();

        Assert.Contains("ConfigureFlow", exception.Message);
    }

    [Fact]
    public void ScopeRegistrationException_HasDiagnosticCode()
    {
        var exception = new ScopeRegistrationException("GBSR3001", "Duplicate scope declaration.");

        Assert.Equal("GBSR3001", exception.DiagnosticCode);
    }

    [Fact]
    public async Task ReloadScopeAsync_UndeclaredScope_ThrowsScopeNotDeclaredException()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<ITestSessionService>(
                new AttemptControlledSessionService((_, _) => Task.CompletedTask));
        });

        await pipeline.InitializeAsync();

        var exception = await Assert.ThrowsAsync<ScopeNotDeclaredException>(() =>
            pipeline.ReloadScopeAsync<UndeclaredScope>());

        Assert.Equal(typeof(UndeclaredScope), exception.ScopeType);
    }

    [Fact]
    public async Task ReloadScopeAsync_GlobalScope_ThrowsScopeNotRestartableException()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineGlobalScope();
        });

        await pipeline.InitializeAsync();

        var exception = await Assert.ThrowsAsync<ScopeNotRestartableException>(() =>
            pipeline.ReloadScopeAsync<GlobalScope>());

        Assert.Equal(typeof(GlobalScope), exception.ScopeType);
    }

    [Fact]
    public async Task RunAsync_WithoutConfigureFlow_ThrowsFlowNotConfiguredException()
    {
        var pipeline = RuntimePipeline.Create(_ => { });

        await Assert.ThrowsAsync<FlowNotConfiguredException>(() =>
            pipeline.RunAsync(NoopSceneLoader.Instance));
    }
}
