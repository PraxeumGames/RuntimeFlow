using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Testing
{
    /// <summary>The fault-injection fake used by framework and consumer tests.</summary>
    [TestFixture]
    public sealed class LifecycleFakeTests
    {
        public interface IFakeService : IAsyncInitializable, IAsyncDisposable { }

        public sealed class Stub : IFakeService
        {
            public int Calls { get; private set; }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                Calls++;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => new ValueTask();
        }

        private static InitContext? NoContext => null;

        [Test]
        [Timeout(10000)]
        public async Task TheFakeRecordsEveryInitializeAttempt()
        {
            var handle = LifecycleFake.OfHandle<IFakeService>();

            await handle.Service.InitializeAsync(NoContext!, CancellationToken.None);
            await handle.Service.InitializeAsync(NoContext!, CancellationToken.None);

            Assert.That(handle.Log.Invocations, Is.EqualTo(new[] { "initialize#1", "initialize#2" }));
            Assert.That(handle.Log.WasInvoked("initialize"), Is.True);
        }

        [Test]
        [Timeout(10000)]
        public async Task FailInitializeAttemptsFailsTheFirstNCalls()
        {
            var handle = LifecycleFake.OfHandle<IFakeService>(configure: cfg => cfg.FailInitializeAttempts(2));

            var first = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(
                () => handle.Service.InitializeAsync(NoContext!, CancellationToken.None));
            await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(
                () => handle.Service.InitializeAsync(NoContext!, CancellationToken.None));
            await AsyncTestAssert.DoesNotThrowAsync(
                () => handle.Service.InitializeAsync(NoContext!, CancellationToken.None));

            Assert.That(first.Message, Is.EqualTo("LifecycleFake: initialize attempt 1 configured to fail."));
        }

        [Test]
        [Timeout(10000)]
        public async Task HangWaitsForTheCancellationToken()
        {
            var fake = LifecycleFake.Of<IFakeService>(configure: cfg => cfg.Hang());
            using var cts = new CancellationTokenSource();

            var running = fake.InitializeAsync(NoContext!, cts.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            Assert.That(running.IsCompleted, Is.False);

            cts.Cancel();
            await AsyncTestAssert.ThrowsAsync<OperationCanceledException>(() => running);
        }

        [Test]
        [Timeout(10000)]
        public async Task DelayInitializePostponesCompletion()
        {
            var fake = LifecycleFake.Of<IFakeService>(configure: cfg => cfg.DelayInitialize(TimeSpan.FromMilliseconds(100)));

            var running = fake.InitializeAsync(NoContext!, CancellationToken.None);
            Assert.That(running.IsCompleted, Is.False);
            await running;

            Assert.That(running.IsCompletedSuccessfully, Is.True);
        }

        [Test]
        [Timeout(10000)]
        public async Task DisposeAsyncIsInterceptedAndCanFail()
        {
            var handle = LifecycleFake.OfHandle<IFakeService>(configure: cfg => cfg.FailDisposeAttempts(1));

            Assert.Throws<InvalidOperationException>(() => { handle.Service.DisposeAsync(); });
            await handle.Service.DisposeAsync();

            Assert.That(handle.Log.Invocations, Is.EqualTo(new[] { "disposeAsync#1", "disposeAsync#2" }));
        }

        [Test]
        [Timeout(10000)]
        public async Task TheStubIsCalledWhenInitializationSucceeds()
        {
            var stub = new Stub();
            var handle = LifecycleFake.OfHandle<IFakeService>(stub);

            await handle.Service.InitializeAsync(NoContext!, CancellationToken.None);

            Assert.That(stub.Calls, Is.EqualTo(1));
            Assert.That(handle.Log.WasInvoked("initialize"), Is.True);
        }

        [Test]
        [Timeout(10000)]
        public async Task TheFakeParticipatesInARealRun()
        {
            var handle = LifecycleFake.OfHandle<IFakeService>();
            var log = new CapturingLogger();
            var container = TestScope.Build(b => b.RegisterInstance(handle.Service).As<IAsyncInitializable>());

            var run = ScopeRun.Create(container, "session", TestScope.Options(log));
            var result = await run.RunAsync();
            await run.DisposeAsync();

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(handle.Log.Invocations, Is.EqualTo(new[] { "initialize#1", "disposeAsync#1" }));
        }

        [Test]
        public void OnlyInterfacesCanBeFaked()
        {
            Assert.Throws<ArgumentException>(() => LifecycleFake.Of<Stub>());
        }
    }
}
