using System;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

public sealed partial class UxImprovementsTests
{
    [Fact]
    public void ScopeState_AfterDeclare_IsNotLoaded()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineGlobalScope();
            builder.DefineSessionScope();
        });

        Assert.Equal(ScopeLifecycleState.NotLoaded, pipeline.GetScopeState<GlobalScope>());
        Assert.Equal(ScopeLifecycleState.NotLoaded, pipeline.GetScopeState<SessionScope>());
        Assert.Equal(ScopeLifecycleState.NotLoaded, pipeline.GetScopeState<TestSceneScope>());
        Assert.Equal(ScopeLifecycleState.NotLoaded, pipeline.GetScopeState<TestModuleScope>());
    }

    [Fact]
    public async Task ScopeState_AfterBuildAsync_GlobalAndSessionAreActive()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineGlobalScope();
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<ITestSessionService>(
                new AttemptControlledSessionService((_, _) => Task.CompletedTask));
        });

        await pipeline.InitializeAsync();

        Assert.Equal(ScopeLifecycleState.Active, pipeline.GetScopeState<GlobalScope>());
        Assert.Equal(ScopeLifecycleState.Active, pipeline.GetScopeState<SessionScope>());
    }

    [Fact]
    public async Task ScopeState_AfterLoadSceneAndModule_AllScopesAreActive()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineGlobalScope();
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<ITestSessionService>(
                new AttemptControlledSessionService((_, _) => Task.CompletedTask));
            builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(
                new AttemptControlledSceneService((_, _) => Task.CompletedTask))));
            builder.Module(new TestModuleScope(m => m.RegisterInstance<ITestModuleService>(
                new AttemptControlledModuleService((_, _) => Task.CompletedTask))));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadModuleAsync<TestModuleScope>();

        Assert.Equal(ScopeLifecycleState.Active, pipeline.GetScopeState<GlobalScope>());
        Assert.Equal(ScopeLifecycleState.Active, pipeline.GetScopeState<SessionScope>());
        Assert.Equal(ScopeLifecycleState.Active, pipeline.GetScopeState<TestSceneScope>());
        Assert.Equal(ScopeLifecycleState.Active, pipeline.GetScopeState<TestModuleScope>());
    }

    [Fact]
    public async Task ScopeState_AfterReloadSession_SessionBecomesActive()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<ITestSessionService>(
                new AttemptControlledSessionService((_, _) => Task.CompletedTask));
        });

        await pipeline.InitializeAsync();
        await pipeline.ReloadScopeAsync<SessionScope>();

        Assert.Equal(ScopeLifecycleState.Active, pipeline.GetScopeState<SessionScope>());
    }

    [Fact]
    public async Task ScopeState_AfterFailedInit_IsFailed()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<ITestSessionService>(
                new FailingSessionService());
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.InitializeAsync());

        Assert.Equal(ScopeLifecycleState.Failed, pipeline.GetScopeState<SessionScope>());
    }

    [Fact]
    public void ScopeState_UndeclaredScope_ReturnsNotLoaded()
    {
        var pipeline = RuntimePipeline.Create(_ => { });

        Assert.Equal(ScopeLifecycleState.NotLoaded, pipeline.GetScopeState<UndeclaredScope>());
    }
}
