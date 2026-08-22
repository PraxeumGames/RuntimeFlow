using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using RuntimeFlow.Pipeline;
using RuntimeFlow.Testing;

namespace RuntimeFlow.Tests
{
    /// <summary>
    /// Dogfood coverage for the RuntimeFlow.Testing harness: fluent overrides with typo
    /// protection, lifecycle fault injection, and ambient activation.
    /// </summary>
    public sealed class TestPipelineFacadeTests
    {
        public interface IRealService
        {
            string Name { get; }
        }

        private sealed class RealService : IRealService, ISessionInitializableService
        {
            public string Name => "real";

            public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private sealed class FakeService : IRealService
        {
            public string Name => "fake";
        }

        public interface IFakeInitService : IRealService, ISessionInitializableService, IAsyncInitializableService { }

        [Test]
        public async Task Override_TakesEffect_AndIsVerified()
        {
            await using var app = await TestPipeline.Create()
                .Override<IRealService, FakeService>()
                .StartAsync();

            var service = await app.SessionContext.ResolveAsync<IRealService>();
            Assert.AreEqual("fake", service.Name);
        }

        [Test]
        public void Override_ShadowedByLaterRegistration_FailsStartWithDescriptiveError()
        {
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await TestPipeline.Create()
                    .Configure(b => b.Session().Register<IRealService, FakeService>(DiLifetime.Singleton))
                    .Override<IRealService, FakeService>()
                    .Configure(b => b.Session().Register<IRealService, RealService>(DiLifetime.Singleton))
                    .StartAsync());
        }

        [Test]
        public async Task LifecycleFake_FailingInitialize_FailsStartupWithRecordedAttempts()
        {
            var fake = LifecycleFake.OfHandle<IFakeInitService>(
                null,
                cfg => cfg.FailInitializeAttempts(2));

            Exception? caught = null;
            try
            {
                await using var app = await TestPipeline.Create()
                    .Override(fake.Service)
                    .StartAsync();
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            Assert.IsNotNull(caught, $"Startup should fail; invocations: [{string.Join(", ", fake.Log.Invocations)}]");
            StringAssert.Contains("attempt 1", Flatten(caught!));
            Assert.That(fake.Log.Invocations, Does.Contain("initialize#1"));
        }

        [Test]
        public async Task Activate_SetsAmbientPipeline_DisposeClearsIt()
        {
            var previous = RuntimePipeline.ActivePipeline;
            try
            {
                var app = await TestPipeline.Create().StartAsync();
                app.Activate();
                Assert.AreSame(app.Pipeline, RuntimePipeline.ActivePipeline);

                await app.DisposeAsync();
                Assert.IsNull(RuntimePipeline.ActivePipeline);
            }
            finally
            {
                RuntimePipeline.ActivePipeline = previous;
            }
        }

        private static string Flatten(Exception ex)
        {
            return ex is AggregateException agg ? agg.InnerException?.Message ?? agg.Message : ex.Message;
        }

        [Test]
        public async Task Proxy_InvokesInitialize_AndFailsConfiguredAttempt()
        {
            var fake = LifecycleFake.OfHandle<IFakeInitService>(
                null,
                cfg => cfg.FailInitializeAttempts(1));

            var service = (IAsyncInitializableService)fake.Service;
            Assert.ThrowsAsync<InvalidOperationException>(
                async () => await service.InitializeAsync(CancellationToken.None));
            Assert.That(fake.Log.Invocations, Does.Contain("initialize#1"));
        }
    }
}
