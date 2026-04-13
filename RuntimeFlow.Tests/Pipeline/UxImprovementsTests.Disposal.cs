using System;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

public sealed partial class UxImprovementsTests
{
    [Fact]
    public async Task DisposeAsync_CanBeCalledMultipleTimes()
    {
        var pipeline = RuntimePipeline.Create(_ => { });

        await pipeline.DisposeAsync();
        var exception = await Record.ExceptionAsync(async () => await pipeline.DisposeAsync());

        Assert.Null(exception);
    }

    [Fact]
    public async Task AfterDispose_InitializeAsync_ThrowsObjectDisposedException()
    {
        var pipeline = RuntimePipeline.Create(_ => { });
        await pipeline.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pipeline.InitializeAsync());
    }

    [Fact]
    public async Task AfterDispose_RunAsync_ThrowsObjectDisposedException()
    {
        var pipeline = RuntimePipeline.Create(_ => { })
            .ConfigureFlow(new DelegateRuntimeFlowScenario((_, _) => Task.CompletedTask));
        await pipeline.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            pipeline.RunAsync(NoopSceneLoader.Instance));
    }

    [Fact]
    public async Task AfterDispose_LoadSceneAsync_ThrowsObjectDisposedException()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
        });
        await pipeline.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            pipeline.LoadSceneAsync<TestSceneScope>());
    }
}
