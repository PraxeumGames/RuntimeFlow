using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Internal;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Testing
{
    [TestFixture]
    public sealed class TestFlowDeadlineTests
    {
        [Test]
        [Timeout(10000)]
        public async Task UnsupportedStartupDeadlineIsRejectedBeforeCreatingAHost()
        {
            var before = FlowRegistry.Live.Count;
            await using var flow = TestFlow.Create(_ => { }, _ => { });
            Assert.Throws<ArgumentOutOfRangeException>(() => flow.WithStartupTimeout(TimeSpan.MaxValue));
            Assert.Throws<InvalidOperationException>(() => { _ = flow.Host; });
            Assert.That(FlowRegistry.Live.Count, Is.EqualTo(before), "An invalid timer value must not leave a registered host.");
            await flow.WithStartupTimeout(TimeSpan.FromSeconds(1)).StartAsync();
            Assert.That(flow.Result!.Outcome, Is.EqualTo(StartupOutcome.Completed), "Rejected configuration must leave the flow usable.");
        }

        public sealed class DisposalGate
        {
            public readonly TaskCompletionSource<bool> Entered =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Started =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Release =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool FailInitialize;
            public bool Finished;
            public bool GlobalDisposed;
        }

        public sealed class TrackedGlobal : IAsyncInitializable, IAsyncDisposable
        {
            private readonly DisposalGate _gate;
            public TrackedGlobal(DisposalGate gate) => _gate = gate;
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync()
            {
                _gate.GlobalDisposed = true;
                return default;
            }
        }

        public sealed class HungStartupWithSlowDisposal : IAsyncInitializable, IAsyncDisposable
        {
            private readonly DisposalGate _gate;
            public HungStartupWithSlowDisposal(DisposalGate gate) => _gate = gate;

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _gate.Started.TrySetResult(true);
                return _gate.FailInitialize
                    ? Task.FromException(new InvalidOperationException("original startup failure"))
                    : Task.Delay(Timeout.Infinite, cancellationToken);
            }

            public async ValueTask DisposeAsync()
            {
                _gate.Entered.TrySetResult(true);
                await _gate.Release.Task;
                _gate.Finished = true;
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task StartupDeadlineMustSurfaceWhileFailedStartupCleanupIsStillPending()
        {
            var gate = new DisposalGate();
            var flow = TestFlow.Create(builder =>
                {
                    builder.RegisterInstance(gate);
                    builder.RegisterInitializable<TrackedGlobal>();
                }, builder =>
                {
                    builder.RegisterInstance(gate);
                    builder.RegisterInitializable<HungStartupWithSlowDisposal>();
                })
                .WithStartupTimeout(TimeSpan.FromMilliseconds(50));
            var starting = flow.StartAsync();
            try
            {
                var disposalEntered = await Task.WhenAny(gate.Entered.Task, Task.Delay(2000));
                Assert.That(disposalEntered, Is.SameAs(gate.Entered.Task),
                    "The startup deadline must enter cleanup; this confirms the intended path was reached.");

                var finished = await Task.WhenAny(starting, Task.Delay(300));
                Assert.That(finished, Is.SameAs(starting),
                    "WithStartupTimeout promises a TimeoutException for a hung startup, but StartAsync still awaits an unbounded DisposeAsync after its 50 ms deadline.");
                await AsyncTestAssert.ThrowsAsync<TimeoutException>(() => starting);
                Assert.That(gate.GlobalDisposed, Is.False, "Global dependencies must outlive pending session cleanup.");
            }
            finally
            {
                // Release even after the assertion fails: the proof must never leave Unity hung.
                gate.Release.TrySetResult(true);
                try { await starting; }
                catch (TimeoutException) { }
                await flow.DisposeAsync();
                Assert.That(gate.Finished, Is.True);
                Assert.That(gate.GlobalDisposed, Is.True);
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task FailedStartupPreservesItsOriginalErrorWhenCleanupExceedsTheDeadline()
        {
            var gate = new DisposalGate { FailInitialize = true };
            var flow = TestFlow.Create(_ => { }, builder =>
            {
                builder.RegisterInstance(gate);
                builder.RegisterInitializable<HungStartupWithSlowDisposal>();
            }).WithStartupTimeout(TimeSpan.FromMilliseconds(200));
            var starting = flow.StartAsync();
            try
            {
                var finished = await Task.WhenAny(starting, Task.Delay(2000));
                Assert.That(finished, Is.SameAs(starting), "The startup deadline also bounds failed-startup cleanup.");
                var error = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => starting);
                Assert.That(error.InnerException!.Message, Is.EqualTo("original startup failure"));
                Assert.That(gate.Entered.Task.IsCompleted, Is.True);
                Assert.That(gate.Finished, Is.False);
            }
            finally
            {
                gate.Release.TrySetResult(true);
                try { await starting; } catch (RuntimeFlowException) { }
                await flow.DisposeAsync();
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task FailedStartupWaitsForNormalCleanupWithinTheDeadline()
        {
            var gate = new DisposalGate { FailInitialize = true };
            var flow = TestFlow.Create(_ => { }, builder =>
            {
                builder.RegisterInstance(gate);
                builder.RegisterInitializable<HungStartupWithSlowDisposal>();
            }).WithStartupTimeout(TimeSpan.FromSeconds(2));
            var starting = flow.StartAsync();
            try
            {
                var entered = await Task.WhenAny(gate.Entered.Task, Task.Delay(1000));
                Assert.That(entered, Is.SameAs(gate.Entered.Task));
                Assert.That(starting.IsCompleted, Is.False, "Normal failed startup still waits for cleanup.");
                gate.Release.TrySetResult(true);
                await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => starting);
                Assert.That(gate.Finished, Is.True);
            }
            finally
            {
                gate.Release.TrySetResult(true);
                try { await starting; } catch (RuntimeFlowException) { }
                await flow.DisposeAsync();
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task CallerCancellationRemainsCancellationWhenCleanupExceedsTheDeadline()
        {
            var gate = new DisposalGate();
            var flow = TestFlow.Create(_ => { }, builder =>
            {
                builder.RegisterInstance(gate);
                builder.RegisterInitializable<HungStartupWithSlowDisposal>();
            }).WithStartupTimeout(TimeSpan.FromMilliseconds(300));
            using var cancellation = new CancellationTokenSource();
            var starting = flow.StartAsync(cancellation.Token);
            try
            {
                Assert.That(await Task.WhenAny(gate.Started.Task, Task.Delay(2000)), Is.SameAs(gate.Started.Task));
                cancellation.Cancel();
                Assert.That(await Task.WhenAny(starting, Task.Delay(2000)), Is.SameAs(starting));
                await AsyncTestAssert.ThrowsAsync<OperationCanceledException>(() => starting);
                Assert.That(gate.Finished, Is.False);
            }
            finally
            {
                gate.Release.TrySetResult(true);
                try { await starting; } catch (OperationCanceledException) { }
                await flow.DisposeAsync();
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task OverrideSeesInterfacesAddedAfterExistsInspection()
        {
            var probe = new TestFlowTests.Probe();
            var fake = new TestFlowTests.FakeBackend(probe);
            await using var flow = await TestFlow.Create(_ => { }, builder =>
            {
                builder.RegisterInstance(probe);
                var registration = builder.Register<TestFlowTests.RealBackend>(Lifetime.Singleton);
                builder.Exists(typeof(TestFlowTests.IBackend), includeInterfaceTypes: true);
                registration.AsSelf().AsImplementedInterfaces();
                builder.RegisterInitializable<TestFlowTests.Consumer>();
            }).Override<TestFlowTests.IBackend>(fake).StartAsync();

            Assert.That(flow.Resolve<TestFlowTests.IBackend>(), Is.SameAs(fake));
            Assert.That(probe.Constructed, Is.Zero);
        }
    }
}
