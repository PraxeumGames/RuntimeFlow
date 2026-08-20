using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{
    public sealed class BootstrapResultSyncDisposeTests
    {
        private interface ITeardownTrackingSessionService : ISessionInitializableService, ISessionDisposableService
        {
        }

        private sealed class TeardownTrackingSessionService : ITeardownTrackingSessionService
        {
            private readonly ManualResetEventSlim _teardownCompleted;

            public TeardownTrackingSessionService(ManualResetEventSlim teardownCompleted)
            {
                _teardownCompleted = teardownCompleted;
            }

            public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task DisposeAsync(CancellationToken cancellationToken)
            {
                _teardownCompleted.Set();
                return Task.CompletedTask;
            }
        }

        [Test]
        public async Task SyncDispose_CompletesPipelineTeardown_WithoutBlocking()
        {
            var teardownCompleted = new ManualResetEventSlim(false);
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().RegisterInstance<ITeardownTrackingSessionService>(
                    new TeardownTrackingSessionService(teardownCompleted));
            });

            await pipeline.InitializeAsync();

            using var cts = new CancellationTokenSource();
            var result = new BootstrapResult(pipeline, rootContainer: null!, cts);

            result.Dispose();

            Assert.That(
                teardownCompleted.Wait(TimeSpan.FromSeconds(5)),
                Is.True,
                "Synchronous Dispose() must not deadlock and must complete pipeline teardown.");
        }

        [Test]
        public void SyncDispose_IsIdempotent()
        {
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
            });

            using var cts = new CancellationTokenSource();
            var result = new BootstrapResult(pipeline, rootContainer: null!, cts);

            result.Dispose();
            Assert.DoesNotThrow(() => result.Dispose());
        }
    }
}