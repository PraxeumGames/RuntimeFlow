using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Scheduling
{
    /// <summary>
    /// The scheduler's bookkeeping at the edges: cancellation continuations that run inline, several
    /// timeouts in one tick, construction failures, teardown racing a settle, foreign threads and
    /// pathological option values. Every node reaches exactly one terminal state and every run finishes.
    /// </summary>
    [TestFixture]
    public sealed class SchedulerEdgeCaseTests
    {
        /// <summary>Times out, catches its own cancellation and returns normally, inline inside Cancel().</summary>
        [Init(Optional = true, TimeoutSeconds = 0.05)]
        public sealed class SwallowsItsTimeout : IAsyncInitializable
        {
            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // returns normally: the run already decided this service timed out
                }
            }
        }

        public sealed class AfterSwallower : AutoService
        {
            public AfterSwallower(SwallowsItsTimeout dependency) { }
        }

        [Init(TimeoutSeconds = 0.05)]
        public sealed class SlowOne : IAsyncInitializable
        {
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
                => Task.Delay(Timeout.Infinite, cancellationToken);
        }

        [Init(TimeoutSeconds = 0.05)]
        public sealed class SlowTwo : IAsyncInitializable
        {
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
                => Task.Delay(Timeout.Infinite, cancellationToken);
        }

        /// <summary>Times out and never looks at its token again.</summary>
        [Init(Optional = true, TimeoutSeconds = 0.05)]
        public sealed class IgnoresItsToken : IAsyncInitializable
        {
            public TaskCompletionSource<bool> Never { get; } = new TaskCompletionSource<bool>();

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Never.Task;
        }

        public sealed class HaltsAfterTheIgnorer : IAsyncInitializable
        {
            public HaltsAfterTheIgnorer(IgnoresItsToken dependency) { }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                context.Halt("stop");
                return Task.CompletedTask;
            }
        }

        [Init(Optional = true)]
        public sealed class BrokenOptional : AutoService
        {
            public BrokenOptional() => throw new InvalidOperationException("optional constructor exploded");
        }

        public sealed class BrokenRequired : AutoService
        {
            public BrokenRequired() => throw new InvalidOperationException("required constructor exploded");
        }

        public sealed class BrokenRequiredToo : AutoService
        {
            public BrokenRequiredToo() => throw new InvalidOperationException("second constructor exploded");
        }

        public sealed class AfterBrokenRequired : AutoService
        {
            public AfterBrokenRequired(BrokenRequired dependency) { }
        }

        public sealed class Upstream : ControlledService { }

        /// <summary>Optional, broken at construction, and ordered after <see cref="Upstream"/>.</summary>
        [Init(Optional = true)]
        public sealed class BrokenInTheMiddle : AutoService
        {
            public BrokenInTheMiddle(Upstream upstream) => throw new InvalidOperationException("middle constructor exploded");
        }

        [DependsOn(typeof(BrokenInTheMiddle))]
        public sealed class Downstream : AutoService { }

        /// <summary>Takes a while to honour its cancellation, well within any sane grace.</summary>
        public sealed class SlowToCancel : IAsyncInitializable, IAsyncDisposable
        {
            public TaskCompletionSource<bool> Started { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public bool Initializing { get; private set; }
            public bool DisposedWhileInitializing { get; private set; }

            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                Initializing = true;
                Started.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                finally
                {
                    await Task.Delay(150, CancellationToken.None);
                    Initializing = false;
                }
            }

            public ValueTask DisposeAsync()
            {
                DisposedWhileInitializing |= Initializing;
                return default;
            }
        }

        public sealed class Halter : ControlledService { }

        public sealed class HaltsFromAWorkerThread : IAsyncInitializable
        {
            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                await Task.Run(() =>
                {
                    context.ReportProgress(0.5f);
                    context.Halt("from-worker");
                });
            }
        }

        public sealed class CancelsTheCallerTokenAsItCompletes : IAsyncInitializable
        {
            public CancellationTokenSource Caller { get; } = new CancellationTokenSource();
            public CancellationToken Token { get; private set; }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                Token = cancellationToken;
                // Another thread cancels the caller token just before this, the last service, completes:
                // the cancellation is posted to the main thread and lands after the run finished.
                Task.Run(() => Caller.Cancel()).Wait();
                return Task.CompletedTask;
            }
        }

        [Init(TimeoutSeconds = 0.3)]
        public sealed class TimesOutLater : IAsyncInitializable
        {
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
                => Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public sealed class DisposesSlowly : IAsyncInitializable, IAsyncDisposable
        {
            public TaskCompletionSource<bool> Gate { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;

            public async ValueTask DisposeAsync() => await Gate.Task;
        }

        public sealed class CancelledRunObserver : IRuntimeFlowObserver
        {
            public List<string> Events { get; } = new List<string>();

            public void OnRunCancelled(string scope) => Events.Add($"run-cancelled:{scope}");
        }

        public sealed class MainThreadObserver : IRuntimeFlowObserver
        {
            private readonly int _mainThread;

            public MainThreadObserver(int mainThread) => _mainThread = mainThread;

            public List<string> OffThread { get; } = new List<string>();
            public bool Halted { get; private set; }

            public void OnRunHalted(string scope, StartupResult result)
            {
                Halted = true;
                if (Thread.CurrentThread.ManagedThreadId != _mainThread) OffThread.Add(nameof(OnRunHalted));
            }
        }

        /// <summary>A logger whose stall warnings throw, standing in for any failure inside a watch tick.</summary>
        public sealed class ExplodingStallLogger : ILogger
        {
            private readonly CapturingLogger _inner;

            public ExplodingStallLogger(CapturingLogger inner) => _inner = inner;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning && formatter(state, exception).Contains("no progress"))
                    throw new InvalidOperationException("logger exploded");
                _inner.Log(logLevel, eventId, state, exception, formatter);
            }
        }

        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

        private CapturingLogger _log = null!;
        private CollectingObserver _observer = null!;
        private RuntimeFlowOptions _options = null!;

        private readonly RunTracker _tracker = new RunTracker();

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _observer = new CollectingObserver();
            _options = TestScope.Options(_log, _observer);
        }

        /// <summary>Disposes every run and container this fixture created, so nothing leaks into the next test.</summary>
        [TearDown]
        public void DisposeTrackedRuns() => _tracker.DisposeAll();

        private async Task Settled(Task task, string what)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Bound));
            Assert.That(winner, Is.SameAs(task), $"{what} never settled.\n{_log.Dump()}");
        }

        private int Count(string entry) => _observer.Events.Count(e => e == entry);

        [Test]
        [Timeout(10000)]
        public async Task AServiceThatReturnsFromItsTimeoutCancellationIsReportedOnceAsTimedOut()
        {
            _options.TimeoutMultiplier = 1.0;
            var container = _tracker.Build(b =>
            {
                b.Add<SwallowsItsTimeout>();
                b.Add<AfterSwallower>();
            });
            var run = _tracker.Create(container, "session", _options);

            var task = run.RunAsync();
            await Settled(task, "the run");
            var result = await task;

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(result.Degraded, Is.EqualTo(new[] { "SwallowsItsTimeout" }));
            var status = run.GetStatus().Service("SwallowsItsTimeout");
            Assert.That(status.State, Is.EqualTo(ServiceState.Degraded));
            Assert.That(status.Error, Is.TypeOf<TimeoutException>());
            Assert.That(Count("failed:session:SwallowsItsTimeout"), Is.EqualTo(1), string.Join("\n", _observer.Events));
            Assert.That(Count("completed:session:SwallowsItsTimeout"), Is.EqualTo(0), string.Join("\n", _observer.Events));
            Assert.That(Count("started:session:AfterSwallower"), Is.EqualTo(1), string.Join("\n", _observer.Events));
        }

        [Test]
        [Timeout(10000)]
        public async Task TwoRequiredServicesExpiringInTheSameTickBothTimeOut()
        {
            _options.TimeoutMultiplier = 1.0;
            var container = _tracker.Build(b =>
            {
                b.Add<SlowOne>();
                b.Add<SlowTwo>();
            });
            var run = _tracker.Create(container, "session", _options);

            var task = run.RunAsync();
            await Settled(task, "the run");
            var error = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => task);

            Assert.That(error.Failures.Select(f => f.Service), Is.EquivalentTo(new[] { "SlowOne", "SlowTwo" }), error.Message);
            Assert.That(error.Failures.All(f => f.Error is TimeoutException), Is.True, error.Message);
            Assert.That(run.GetStatus().Service("SlowTwo").State, Is.EqualTo(ServiceState.Failed));
        }

        [Test]
        [Timeout(10000)]
        public async Task AStopDoesNotWaitForAServiceThatAlreadyTimedOut()
        {
            _options.TimeoutMultiplier = 1.0;
            _options.CancellationGrace = TimeSpan.FromSeconds(3);
            var container = _tracker.Build(b =>
            {
                b.Add<IgnoresItsToken>();
                b.Add<HaltsAfterTheIgnorer>();
            });
            var ignorer = container.Resolve<IgnoresItsToken>();
            var run = _tracker.Create(container, "session", _options);
            try
            {
                var clock = Stopwatch.StartNew();
                var result = await run.RunAsync();

                Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Halted));
                Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1.5)),
                    "the halt must not wait out the grace for a service the run already gave up on");
            }
            finally
            {
                ignorer.Never.TrySetResult(true);
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task EveryRequiredConstructionFailureIsReportedBeforeAnythingStarts()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<BrokenOptional>();
                b.Add<BrokenRequired>();
                b.Add<BrokenRequiredToo>();
                b.Add<AfterBrokenRequired>();
            });
            var run = _tracker.Create(container, "session", _options);

            var error = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => run.RunAsync());

            // AfterBrokenRequired cannot be constructed either: its dependency could not be.
            Assert.That(error.Failures.Select(f => f.Service),
                Is.EqualTo(new[] { "BrokenRequired", "BrokenRequiredToo", "AfterBrokenRequired" }), error.Message);
            Assert.That(_observer.Events.Any(e => e.StartsWith("started:", StringComparison.Ordinal)), Is.False,
                string.Join("\n", _observer.Events));
            Assert.That(_observer.Events.Any(e => e.StartsWith("completed:", StringComparison.Ordinal)), Is.False,
                string.Join("\n", _observer.Events));
            Assert.That(run.GetStatus().Service("BrokenRequired").State, Is.EqualTo(ServiceState.Failed));
            Assert.That(run.GetStatus().Service("BrokenOptional").State, Is.EqualTo(ServiceState.Skipped),
                "an optional construction failure is not reported before its turn, and the run stopped first");
        }

        [Test]
        [Timeout(10000)]
        public async Task AnOptionalConstructionFailureDegradesOnlyOnceItsOwnDependenciesAreDone()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<Upstream>();
                b.Add<BrokenInTheMiddle>();
                b.Add<Downstream>();
            });
            var upstream = container.Resolve<Upstream>();
            var run = _tracker.Create(container, "session", _options);

            var task = run.RunAsync();
            await upstream.Started;
            await Task.Yield();

            Assert.That(run.GetStatus().Service("BrokenInTheMiddle").State, Is.EqualTo(ServiceState.Pending));
            Assert.That(_observer.Contains("started:session:Downstream"), Is.False, "the transitive order Upstream > Downstream holds");

            upstream.Release();
            var result = await task;

            Assert.That(result.Degraded, Is.EqualTo(new[] { "BrokenInTheMiddle" }));
            Assert.That(_observer.IndexOf("completed:session:Upstream"), Is.LessThan(_observer.IndexOf("started:session:Downstream")));
        }

        [Test]
        [Timeout(10000)]
        public async Task DisposingDuringAHaltWaitsForTheSettleBeforeDisposingServices()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<Halter>();
                b.Register<SlowToCancel>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces();
            });
            var halter = container.Resolve<Halter>();
            var slow = container.Resolve<SlowToCancel>();
            var run = ScopeRun.Create(container, "session", _options);

            var task = run.RunAsync();
            await slow.Started.Task;
            await halter.Started;

            halter.Context!.Halt("stop");
            await Settled(run.DisposeAsync().AsTask(), "the disposal");

            Assert.That(slow.DisposedWhileInitializing, Is.False, "DisposeAsync ran while InitializeAsync was still unwinding");
            Assert.That(task.IsCompleted, Is.True);
        }

        [Test]
        public void RunningWithoutASynchronizationContextIsRefused()
        {
            var container = _tracker.Build(b => b.Add<Halter>());
            var run = _tracker.Create(container, "session", _options);

            var saved = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                var error = Assert.Throws<InvalidOperationException>(() => run.RunAsync());
                Assert.That(error!.Message, Does.Contain("SynchronizationContext"));
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(saved);
            }

            Assert.That(run.State, Is.EqualTo(RunState.NotStarted));
        }

        [Test]
        [Timeout(10000)]
        public async Task AHaltFromAWorkerThreadIsHandledOnTheMainThread()
        {
            var observer = new MainThreadObserver(Thread.CurrentThread.ManagedThreadId);
            _options.Observers.Add(observer);
            var container = _tracker.Build(b => b.Add<HaltsFromAWorkerThread>());
            var run = _tracker.Create(container, "session", _options);

            var task = run.RunAsync();
            await Settled(task, "the run");
            var result = await task;

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Halted));
            Assert.That(observer.Halted, Is.True);
            Assert.That(observer.OffThread, Is.Empty);
        }

        [Test]
        [Timeout(10000)]
        public async Task ACallerCancellationThatLandsAfterCompletionLeavesTheRunCompleted()
        {
            var container = _tracker.Build(b => b.Add<CancelsTheCallerTokenAsItCompletes>());
            var service = container.Resolve<CancelsTheCallerTokenAsItCompletes>();
            var run = _tracker.Create(container, "session", _options);

            var result = await run.RunAsync(false, 0, service.Caller.Token);
            await Task.Delay(50);

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(run.State, Is.EqualTo(RunState.Completed));
            Assert.That(service.Token.IsCancellationRequested, Is.False, "service tokens stay valid after a completed run");
        }

        [Test]
        [Timeout(10000)]
        public async Task AGraceBeyondTaskDelaysRangeStillLetsTheRunFinish()
        {
            _options.CancellationGrace = TimeSpan.MaxValue;
            var container = _tracker.Build(b =>
            {
                b.Add<Halter>();
                b.Add<SlowOne>();
            });
            var halter = container.Resolve<Halter>();
            var run = _tracker.Create(container, "session", _options);

            var task = run.RunAsync();
            await halter.Started;
            halter.Context!.Halt("stop");

            await Settled(task, "the run");
            Assert.That((await task).Outcome, Is.EqualTo(StartupOutcome.Halted));
        }

        [Test]
        [Timeout(10000)]
        public async Task AnInfiniteGraceWaitsForAServiceThatIsSlowToCancel()
        {
            _options.CancellationGrace = Timeout.InfiniteTimeSpan;
            var container = _tracker.Build(b =>
            {
                b.Add<Halter>();
                b.Register<SlowToCancel>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces();
            });
            var halter = container.Resolve<Halter>();
            var slow = container.Resolve<SlowToCancel>();
            var run = _tracker.Create(container, "session", _options);

            var task = run.RunAsync();
            await slow.Started.Task;
            await halter.Started;
            halter.Context!.Halt("stop");

            await Settled(task, "the run");
            Assert.That(slow.Initializing, Is.False, "the run waited for the service to unwind");
            Assert.That(_log.Messages(LogLevel.Error), Is.Empty, _log.Dump());
            Assert.That(run.GetStatus().Service("SlowToCancel").State, Is.EqualTo(ServiceState.Cancelled));
        }

        [Test]
        [Timeout(10000)]
        public async Task ALoggerThatThrowsInTheWatchTickDoesNotStopTimeouts()
        {
            _options.TimeoutMultiplier = 1.0;
            _options.StallWarningAfter = TimeSpan.FromMilliseconds(40);
            _options.Logger = new ExplodingStallLogger(_log);
            var container = _tracker.Build(b => b.Add<TimesOutLater>());
            var run = _tracker.Create(container, "session", _options);

            var task = run.RunAsync();
            await Settled(task, "the run");
            var error = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => task);

            Assert.That(error.InnerException, Is.TypeOf<TimeoutException>());
            // The logging helper contains a throwing logger, so the tick itself never fails: the stall
            // warning is lost, the timeouts keep running.
            Assert.That(_log.Has(LogLevel.Error, "the watch tick threw"), Is.False, _log.Dump());
        }

        [Test]
        [Timeout(10000)]
        public async Task ACancelledRunIsReportedToObserversAndTheLog()
        {
            var observer = new CancelledRunObserver();
            _options.Observers.Add(observer);
            var container = _tracker.Build(b => b.Add<Halter>());
            var halter = container.Resolve<Halter>();
            var run = _tracker.Create(container, "session", _options);
            using var cts = new CancellationTokenSource();

            var task = run.RunAsync(false, 0, cts.Token);
            await halter.Started;
            cts.Cancel();
            await AsyncTestAssert.ThrowsAsync<OperationCanceledException>(() => task);

            Assert.That(observer.Events, Is.EqualTo(new[] { "run-cancelled:session" }));
            Assert.That(_observer.Contains("run-cancelled:session"), Is.True, string.Join("\n", _observer.Events));
            Assert.That(_log.Has(LogLevel.Information, "[RuntimeFlow] session: cancelled after"), Is.True, _log.Dump());
        }

        [Test]
        [Timeout(10000)]
        public async Task ConcurrentDisposeCallsAllWaitForTheSameTeardown()
        {
            var container = _tracker.Build(b =>
                b.Register<DisposesSlowly>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces());
            var service = container.Resolve<DisposesSlowly>();
            var run = ScopeRun.Create(container, "session", _options);
            await run.RunAsync();

            var first = run.DisposeAsync().AsTask();
            var second = run.DisposeAsync().AsTask();
            await Task.Yield();

            Assert.That(first.IsCompleted, Is.False);
            Assert.That(second.IsCompleted, Is.False, "a second DisposeAsync must not report a teardown that is still running");

            service.Gate.TrySetResult(true);
            await Settled(Task.WhenAll(first, second), "the disposals");
        }
    }
}
