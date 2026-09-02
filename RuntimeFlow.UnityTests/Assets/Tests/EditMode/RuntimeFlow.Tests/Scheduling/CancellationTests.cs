using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Scheduling
{
    /// <summary>Caller cancellation stops the run, cancels every service token and never hangs teardown.</summary>
    [TestFixture]
    public sealed class CancellationTests
    {
        public sealed class Gated : ControlledService { }

        public sealed class Later : ControlledService
        {
            public Later(Gated gated) => Gated = gated;

            public Gated Gated { get; }
        }

        /// <summary>A service that ignores its cancellation token, so teardown must abandon it.</summary>
        public sealed class Stubborn : IAsyncInitializable
        {
            private readonly TaskCompletionSource<bool> _started =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource<bool> Gate { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Started => _started.Task;

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _started.TrySetResult(true);
                return Gate.Task;
            }
        }

        private CapturingLogger _log = null!;
        private RuntimeFlowOptions _options = null!;

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _options = TestScope.Options(_log);
        }

        [Test]
        [Timeout(10000)]
        public async Task CancellingTheCallerTokenThrowsOperationCanceled()
        {
            var container = TestScope.Build(b => b.Add<Gated>());
            var gated = container.Resolve<Gated>();
            var run = ScopeRun.Create(container, "session", _options);
            using var cts = new CancellationTokenSource();

            var running = run.RunAsync(false, 0, cts.Token);
            await gated.Started;
            cts.Cancel();

            await AsyncTestAssert.ThrowsAsync<OperationCanceledException>(() => running);
            Assert.That(run.State, Is.EqualTo(RunState.Cancelled));
            Assert.That(run.GetStatus().Service("Gated").State, Is.EqualTo(ServiceState.Cancelled));
        }

        [Test]
        [Timeout(10000)]
        public async Task CancellationReachesTheServiceToken()
        {
            var container = TestScope.Build(b => b.Add<Gated>());
            var gated = container.Resolve<Gated>();
            var run = ScopeRun.Create(container, "session", _options);
            using var cts = new CancellationTokenSource();

            var running = run.RunAsync(false, 0, cts.Token);
            await gated.Started;
            Assert.That(gated.Token.IsCancellationRequested, Is.False);

            cts.Cancel();
            await AsyncTestAssert.ThrowsAsync<OperationCanceledException>(() => running);

            Assert.That(gated.Token.IsCancellationRequested, Is.True);
        }

        [Test]
        [Timeout(10000)]
        public async Task ATokenCancelledBeforeTheRunStopsItImmediately()
        {
            var container = TestScope.Build(b => b.Add<Gated>());
            var run = ScopeRun.Create(container, "session", _options);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await AsyncTestAssert.ThrowsAsync<OperationCanceledException>(() => run.RunAsync(false, 0, cts.Token));

            Assert.That(run.GetStatus().Service("Gated").State, Is.EqualTo(ServiceState.Skipped));
        }

        [Test]
        [Timeout(10000)]
        public async Task CancelAsyncStopsARunningGraphWithoutThrowing()
        {
            var container = TestScope.Build(b =>
            {
                b.Add<Gated>();
                b.Add<Later>();
            });
            var gated = container.Resolve<Gated>();
            var run = ScopeRun.Create(container, "session", _options);

            var running = run.RunAsync();
            await gated.Started;

            await AsyncTestAssert.DoesNotThrowAsync(() => run.CancelAsync());
            await AsyncTestAssert.ThrowsAsync<OperationCanceledException>(() => running);

            var status = run.GetStatus();
            Assert.That(status.State, Is.EqualTo(RunState.Cancelled));
            Assert.That(status.Service("Later").State, Is.EqualTo(ServiceState.Skipped));
        }

        [Test]
        [Timeout(10000)]
        public async Task AServiceThatIgnoresItsTokenIsLoggedAndAbandoned()
        {
            var container = TestScope.Build(b => b.RegisterInstance(new Stubborn()).AsImplementedInterfaces());
            var stubborn = (Stubborn)container.Resolve<IAsyncInitializable>();
            _options.CancellationGrace = TimeSpan.FromMilliseconds(100);
            var run = ScopeRun.Create(container, "session", _options);
            using var cts = new CancellationTokenSource();

            var running = run.RunAsync(false, 0, cts.Token);
            await stubborn.Started;
            cts.Cancel();

            await AsyncTestAssert.ThrowsAsync<OperationCanceledException>(() => running);

            var message = _log.Find(LogLevel.Error, "still running");
            Assert.That(message, Is.Not.Null, _log.Dump());
            Assert.That(message, Does.StartWith("[RuntimeFlow] session: 1 services still running 0.1s after cancellation: Stubborn ("));
            Assert.That(message, Does.EndWith("Continuing teardown; they must observe their CancellationToken."));

            stubborn.Gate.TrySetResult(true);
        }

        [Test]
        [Timeout(10000)]
        public async Task DisposeAsyncCancelsEveryServiceToken()
        {
            var container = TestScope.Build(b => b.Add<Gated>());
            var gated = container.Resolve<Gated>();
            var run = ScopeRun.Create(container, "session", _options);

            var running = run.RunAsync();
            await gated.Started;

            await run.DisposeAsync();

            await AsyncTestAssert.ThrowsAsync<OperationCanceledException>(() => running);
            Assert.That(gated.Token.IsCancellationRequested, Is.True);
            Assert.That(run.State, Is.EqualTo(RunState.Disposed));
        }
    }
}
