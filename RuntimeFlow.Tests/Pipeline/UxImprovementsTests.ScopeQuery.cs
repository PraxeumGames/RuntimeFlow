using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

public sealed partial class UxImprovementsTests
{
    [Fact]
    public async Task IsScopeActive_ReturnsTrue_AfterInitialization()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<ITestSessionService>(
                new AttemptControlledSessionService((_, _) => Task.CompletedTask));
        });

        await pipeline.InitializeAsync();

        Assert.True(pipeline.IsScopeActive<SessionScope>());
    }

    [Fact]
    public void IsScopeActive_ReturnsFalse_BeforeInitialization()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
        });

        Assert.False(pipeline.IsScopeActive<SessionScope>());
    }

    [Fact]
    public async Task CanReloadScope_ReturnsTrue_ForActiveSessionSceneModule()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
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

        Assert.True(pipeline.CanReloadScope<SessionScope>());
        Assert.True(pipeline.CanReloadScope<TestSceneScope>());
        Assert.True(pipeline.CanReloadScope<TestModuleScope>());
    }

    [Fact]
    public async Task CanReloadScope_ReturnsFalse_ForGlobalScope()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineGlobalScope();
        });

        await pipeline.InitializeAsync();

        Assert.False(pipeline.CanReloadScope<GlobalScope>());
    }

    [Fact]
    public void CanReloadScope_ReturnsFalse_ForNonActiveScope()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
        });

        Assert.False(pipeline.CanReloadScope<SessionScope>());
    }

    [Fact]
    public async Task CanReloadScope_ReturnsFalse_AfterDispose()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<ITestSessionService>(
                new AttemptControlledSessionService((_, _) => Task.CompletedTask));
        });

        await pipeline.InitializeAsync();
        await pipeline.DisposeAsync();

        Assert.False(pipeline.CanReloadScope<SessionScope>());
    }
}
