using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Testing;
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

        /// <summary>Blocks until cancelled and remembers the thread its cancellation continuation ran on.</summary>
        public sealed class Blocking : IAsyncInitializable
        {
            private readonly TaskCompletionSource<bool> _started =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Started => _started.Task;

            public int ContinuationThread { get; private set; }

            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _started.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                finally
                {
                    ContinuationThread = Thread.CurrentThread.ManagedThreadId;
                }
            }
        }

        /// <summary>Stamps the thread of every observer callback, so a callback off the main thread shows up.</summary>
        public sealed class ThreadWatchingObserver : IRuntimeFlowObserver
        {
            public List<string> OffThread { get; } = new List<string>();

            public int MainThread { get; set; }

            public void OnRunStarted(string scope, bool isRestart) => Check(nameof(OnRunStarted));

            public void OnServiceStarted(ServiceStatus service) => Check(nameof(OnServiceStarted));

            public void OnServiceCompleted(ServiceStatus service) => Check(nameof(OnServiceCompleted));

            public void OnServiceFailed(ServiceStatus service, Exception error) => Check(nameof(OnServiceFailed));

            public void OnRunCompleted(string scope, StartupResult result) => Check(nameof(OnRunCompleted));

            public void OnRunHalted(string scope, StartupResult result) => Check(nameof(OnRunHalted));

            public void OnRunFailed(string scope, RuntimeFlowException error) => Check(nameof(OnRunFailed));

            private void Check(string callback)
            {
                var thread = Thread.CurrentThread.ManagedThreadId;
                if (thread != MainThread) OffThread.Add($"{callback} on thread {thread}");
            }
        }

        /// <summary>Captures the framework diagnostics and the thread each of them was written from.</summary>
        public sealed class ThreadWatchingLogger : ILogger
        {
            public List<string> OffThread { get; } = new List<string>();

            public int MainThread { get; set; }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var thread = Thread.CurrentThread.ManagedThreadId;
                if (thread != MainThread) OffThread.Add($"thread {thread}: {formatter(state, exception)}");
            }
        }

        private CapturingLogger _log = null!;
        private RuntimeFlowOptions _options = null!;

        private readonly RunTracker _tracker = new RunTracker();

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _options = TestScope.Options(_log);
        }

        /// <summary>Disposes every run and container this fixture created, so nothing leaks into the next test.</summary>
        [TearDown]
        public void DisposeTrackedRuns() => _tracker.DisposeAll();

        [Test]
        [Timeout(10000)]
        public async Task ATokenCancelledOnAnotherThreadStillStopsTheRunOnTheMainThread()
        {
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var observer = new ThreadWatchingObserver { MainThread = mainThread };
            var logger = new ThreadWatchingLogger { MainThread = mainThread };
            _options.Observers.Add(observer);
            _options.Logger = logger;
            _options.CancellationGrace = TimeSpan.FromMilliseconds(50);

            var stubborn = new Stubborn();
            var container = _tracker.Build(b =>
            {
                b.Register<Blocking>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces();
                b.RegisterInstance(stubborn).AsImplementedInterfaces();
            });
            var blocking = container.Resolve<Blocking>();
            var run = _tracker.Create(container, "session", _options);

            // CancelAfter fires on a timer thread: without a hand-off the scheduler would tear the run
            // down from there, racing the continuations that run on the editor's main thread.
            using var cts = new CancellationTokenSource();
            var running = run.RunAsync(false, 0, cts.Token);
            await blocking.Started;
            await stubborn.Started;
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));

            await AsyncTestAssert.ThrowsAsync<OperationCanceledException>(() => running);

            Assert.That(run.State, Is.EqualTo(RunState.Cancelled));
            Assert.That(blocking.ContinuationThread, Is.EqualTo(mainThread),
                "the service's cancellation continuation belongs to the main thread");
            Assert.That(observer.OffThread, Is.Empty, string.Join("; ", observer.OffThread));
            Assert.That(logger.OffThread, Is.Empty, string.Join("; ", logger.OffThread));

            var status = run.GetStatus();
            Assert.That(status.Names(ServiceState.Running), Is.Empty, "no service is left Running after the run ended");
            Assert.That(status.Running, Is.Empty);

            stubborn.Gate.TrySetResult(true);
        }

        [Test]
        [Timeout(10000)]
        public async Task CancellingTheCallerTokenThrowsOperationCanceled()
        {
            var container = _tracker.Build(b => b.Add<Gated>());
            var gated = container.Resolve<Gated>();
            var run = _tracker.Create(container, "session", _options);
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
            var container = _tracker.Build(b => b.Add<Gated>());
            var gated = container.Resolve<Gated>();
            var run = _tracker.Create(container, "session", _options);
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
            var container = _tracker.Build(b => b.Add<Gated>());
            var run = _tracker.Create(container, "session", _options);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await AsyncTestAssert.ThrowsAsync<OperationCanceledException>(() => run.RunAsync(false, 0, cts.Token));

            Assert.That(run.GetStatus().Service("Gated").State, Is.EqualTo(ServiceState.Skipped));
        }

        [Test]
        [Timeout(10000)]
        public async Task CancelAsyncStopsARunningGraphWithoutThrowing()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<Gated>();
                b.Add<Later>();
            });
            var gated = container.Resolve<Gated>();
            var run = _tracker.Create(container, "session", _options);

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
            var container = _tracker.Build(b => b.RegisterInstance(new Stubborn()).AsImplementedInterfaces());
            var stubborn = (Stubborn)container.Resolve<IAsyncInitializable>();
            _options.CancellationGrace = TimeSpan.FromMilliseconds(100);
            var run = _tracker.Create(container, "session", _options);
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
        public async Task AnAbandonedServiceThatThrowsLateIsReported()
        {
            var stubborn = new Stubborn();
            var container = _tracker.Build(b => b.RegisterInstance(stubborn).AsImplementedInterfaces());
            _options.CancellationGrace = TimeSpan.FromMilliseconds(50);
            var run = _tracker.Create(container, "session", _options);
            using var cts = new CancellationTokenSource();

            var running = run.RunAsync(false, 0, cts.Token);
            await stubborn.Started;
            cts.Cancel();
            await AsyncTestAssert.ThrowsAsync<OperationCanceledException>(() => running);

            stubborn.Gate.TrySetException(new InvalidOperationException("far too late"));

            await AsyncTestAssert.Until(
                () => _log.Has(LogLevel.Warning, "after it was abandoned"),
                TimeSpan.FromSeconds(5),
                _log.Dump());
            Assert.That(_log.Find(LogLevel.Warning, "after it was abandoned"), Is.EqualTo(
                "[RuntimeFlow] session: Stubborn threw InvalidOperationException after it was abandoned; " +
                "it did not observe its CancellationToken."));
        }

        [Test]
        [Timeout(10000)]
        public async Task AnAbandonedServiceThatCancelsLateIsOnlyNotedAtDebug()
        {
            var stubborn = new Stubborn();
            var container = _tracker.Build(b => b.RegisterInstance(stubborn).AsImplementedInterfaces());
            _options.CancellationGrace = TimeSpan.FromMilliseconds(50);
            var run = _tracker.Create(container, "session", _options);
            using var cts = new CancellationTokenSource();

            var running = run.RunAsync(false, 0, cts.Token);
            await stubborn.Started;
            cts.Cancel();
            await AsyncTestAssert.ThrowsAsync<OperationCanceledException>(() => running);

            stubborn.Gate.TrySetCanceled();

            await AsyncTestAssert.Until(
                () => _log.Has(LogLevel.Debug, "after it was abandoned"),
                TimeSpan.FromSeconds(5),
                _log.Dump());
            Assert.That(_log.Find(LogLevel.Debug, "after it was abandoned"), Is.EqualTo(
                "[RuntimeFlow] session: Stubborn completed cancellation after it was abandoned."));
            Assert.That(_log.Has(LogLevel.Warning, "after it was abandoned"), Is.False, _log.Dump());
        }

        [Test]
        [Timeout(10000)]
        public async Task DisposeAsyncCancelsEveryServiceToken()
        {
            var container = _tracker.Build(b => b.Add<Gated>());
            var gated = container.Resolve<Gated>();
            var run = _tracker.Create(container, "session", _options);

            var running = run.RunAsync();
            await gated.Started;

            await run.DisposeAsync();

            await AsyncTestAssert.ThrowsAsync<OperationCanceledException>(() => running);
            Assert.That(gated.Token.IsCancellationRequested, Is.True);
            Assert.That(run.State, Is.EqualTo(RunState.Disposed));
        }
    }
}
