using System;
using System.Collections.Generic;
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

            public int Disposals { get; private set; }

            public ValueTask DisposeAsync()
            {
                Disposals++;
                return new ValueTask();
            }
        }

        public interface IOverloadedDisposable : IAsyncDisposable
        {
            ValueTask DisposeAsync(CancellationToken cancellationToken);

            ValueTask<string> DisposeAsync(string value);
        }

        public sealed class OverloadedDisposableStub : IOverloadedDisposable
        {
            public CancellationToken? ReceivedToken { get; private set; }
            public string? ReceivedValue { get; private set; }
            public int LifecycleDisposals { get; private set; }

            public ValueTask DisposeAsync()
            {
                LifecycleDisposals++;
                return default;
            }

            public ValueTask DisposeAsync(CancellationToken cancellationToken)
            {
                ReceivedToken = cancellationToken;
                return default;
            }

            public ValueTask<string> DisposeAsync(string value)
            {
                ReceivedValue = value;
                return new ValueTask<string>($"stub:{value}");
            }
        }

        public interface IRepository : IAsyncInitializable
        {
            Task SaveAsync();

            Task<int> CountAsync();

            Task<string> NameAsync();
        }

        private static InitContext? NoContext => null;

        private readonly RunTracker _tracker = new RunTracker();

        /// <summary>Disposes every run and container this fixture created, so nothing leaks into the next test.</summary>
        [TearDown]
        public void DisposeTrackedRuns() => _tracker.DisposeAll();

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

        [TestCase(1)]
        [TestCase(2)]
        [Timeout(10000)]
        public async Task OverlappingInitializeCallsKeepTheirOwnAttemptNumbersAndOutcomes(int failCount)
        {
            var factoryAttempts = new List<int>();
            var stub = new Stub();
            var handle = LifecycleFake.OfHandle<IFakeService>(stub, cfg =>
                cfg.DelayInitialize(TimeSpan.FromMilliseconds(50))
                    .FailInitializeAttempts(failCount, attempt =>
                    {
                        lock (factoryAttempts) factoryAttempts.Add(attempt);
                        return new InvalidOperationException($"initialize attempt {attempt}");
                    }));

            var first = handle.Service.InitializeAsync(NoContext!, CancellationToken.None);
            var second = handle.Service.InitializeAsync(NoContext!, CancellationToken.None);

            try
            {
                await Task.WhenAll(first, second);
            }
            catch (InvalidOperationException)
            {
                // Check each call's own outcome and attempt number below.
            }

            var firstError = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(() => first);
            Assert.That(firstError.Message, Is.EqualTo("initialize attempt 1"));

            if (failCount == 1)
            {
                await AsyncTestAssert.DoesNotThrowAsync(() => second);
                Assert.That(factoryAttempts, Is.EqualTo(new[] { 1 }));
                Assert.That(stub.Calls, Is.EqualTo(1));
            }
            else
            {
                var secondError = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(() => second);
                Assert.That(secondError.Message, Is.EqualTo("initialize attempt 2"));
                Assert.That(factoryAttempts, Is.EquivalentTo(new[] { 1, 2 }));
                Assert.That(stub.Calls, Is.Zero);
            }

            Assert.That(handle.Log.Invocations, Is.EqualTo(new[] { "initialize#1", "initialize#2" }));
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

            // The failure travels in the returned ValueTask, not out of the call: a synchronous throw
            // would escape every `await service.DisposeAsync()` the framework itself writes.
            await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(
                async () => await handle.Service.DisposeAsync());
            await handle.Service.DisposeAsync();

            Assert.That(handle.Log.Invocations, Is.EqualTo(new[] { "disposeAsync#1", "disposeAsync#2" }));
        }

        [Test]
        [Timeout(10000)]
        public async Task DisposeAsyncIsForwardedToTheStub()
        {
            var stub = new Stub();
            var handle = LifecycleFake.OfHandle<IFakeService>(stub);

            await handle.Service.DisposeAsync();

            Assert.That(stub.Disposals, Is.EqualTo(1), "the stub observes its own disposal");
            Assert.That(handle.Log.Invocations, Is.EqualTo(new[] { "disposeAsync#1" }));
        }

        [Test]
        [Timeout(10000)]
        public async Task AFailedDisposalNeverReachesTheStub()
        {
            var stub = new Stub();
            var handle = LifecycleFake.OfHandle<IFakeService>(stub, cfg => cfg.FailDisposeAttempts(1));

            await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(
                async () => await handle.Service.DisposeAsync());

            Assert.That(stub.Disposals, Is.Zero);

            await handle.Service.DisposeAsync();
            Assert.That(stub.Disposals, Is.EqualTo(1));
        }

        [Test]
        [Timeout(10000)]
        public async Task DisposeAsyncCancellationTokenOverloadForwardsTheTokenAndPreservesLifecycleFailureBudget()
        {
            var stub = new OverloadedDisposableStub();
            var handle = LifecycleFake.OfHandle<IOverloadedDisposable>(stub, cfg => cfg.FailDisposeAttempts(1));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await handle.Service.DisposeAsync(cts.Token);

            Assert.That(stub.ReceivedToken, Is.EqualTo(cts.Token));
            Assert.That(handle.Log.Invocations, Is.EqualTo(new[] { "DisposeAsync" }));

            await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(
                async () => await handle.Service.DisposeAsync());
            await handle.Service.DisposeAsync();

            Assert.That(stub.LifecycleDisposals, Is.EqualTo(1));
            Assert.That(handle.Log.Invocations, Is.EqualTo(new[] { "DisposeAsync", "disposeAsync#1", "disposeAsync#2" }));
        }

        [Test]
        [Timeout(10000)]
        public async Task GenericDisposeAsyncOverloadForwardsItsArgumentAndResult()
        {
            var stub = new OverloadedDisposableStub();
            var handle = LifecycleFake.OfHandle<IOverloadedDisposable>(stub);

            var result = await handle.Service.DisposeAsync("payload");

            Assert.That(stub.ReceivedValue, Is.EqualTo("payload"));
            Assert.That(result, Is.EqualTo("stub:payload"));
            Assert.That(handle.Log.Invocations, Is.EqualTo(new[] { "DisposeAsync" }));
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
            var container = _tracker.Build(b => b.RegisterInstance(handle.Service).As<IAsyncInitializable>());

            var run = _tracker.Create(container, "session", TestScope.Options(log));
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

        [Test]
        [Timeout(10000)]
        public async Task AStubLessFakeReturnsCompletedTasksForTaskMembers()
        {
            var fake = LifecycleFake.Of<IRepository>();

            await fake.SaveAsync();
            Assert.That(await fake.CountAsync(), Is.EqualTo(0));
            Assert.That(await fake.NameAsync(), Is.Null);
        }
    }
}
