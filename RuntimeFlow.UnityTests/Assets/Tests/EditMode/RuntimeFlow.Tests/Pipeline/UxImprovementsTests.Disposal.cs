using NUnit.Framework;
using System;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{
    public sealed partial class UxImprovementsTests
    {
        [Test]
        public async Task DisposeAsync_CanBeCalledMultipleTimes()
        {
            var pipeline = RuntimePipeline.Create(_ => { });

            await pipeline.DisposeAsync();

            await AsyncTestAssert.DoesNotThrowAsync(async () => await pipeline.DisposeAsync());
        }

        [Test]
        public async Task AfterDispose_InitializeAsync_ThrowsObjectDisposedException()
        {
            var pipeline = RuntimePipeline.Create(_ => { });
            await pipeline.DisposeAsync();

            await AsyncTestAssert.ThrowsAsync<ObjectDisposedException>(async () => await pipeline.InitializeAsync());
        }

        [Test]
        public async Task AfterDispose_RunAsync_ThrowsObjectDisposedException()
        {
            var pipeline = RuntimePipeline.Create(_ => { })
                .ConfigureFlow(new DelegateRuntimeFlowScenario((_, _) => Task.CompletedTask));
            await pipeline.DisposeAsync();

            await AsyncTestAssert.ThrowsAsync<ObjectDisposedException>(async () =>
                await pipeline.RunAsync(NoopSceneLoader.Instance));
        }

        [Test]
        public async Task AfterDispose_LoadSceneAsync_ThrowsObjectDisposedException()
        {
            var pipeline = RuntimePipeline.Create(builder =>
            {
            });
            await pipeline.DisposeAsync();

            await AsyncTestAssert.ThrowsAsync<ObjectDisposedException>(async () =>
                await pipeline.LoadSceneAsync<TestSceneScope>());
        }
    }

}
