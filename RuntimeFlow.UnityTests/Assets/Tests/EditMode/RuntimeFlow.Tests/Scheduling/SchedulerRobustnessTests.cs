using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Internal;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Scheduling
{
    /// <summary>
    /// The scheduler against hostile user code: cancellation callbacks and loggers that throw, observers
    /// that cancel, frozen runs, late progress, services that outlive their timeout, and polled status.
    /// Every run still finishes with the right outcome and every teardown step still happens.
    /// </summary>
    [TestFixture]
    public sealed class SchedulerRobustnessTests
    {
        private readonly RunTracker _tracker = new RunTracker();
        private CapturingLogger _log = null!;
        private RuntimeFlowOptions _options = null!;

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _options = TestScope.Options(_log);
        }

        [TearDown]
        public void TearDown() => _tracker.DisposeAll();

        private IObjectResolver _container = null!;

        private ScopeRun Run(Action<IContainerBuilder> install, bool ownsScope = false)
        {
            var container = _container = TestScope.Build(install);
            if (!ownsScope) _tracker.Track(container);
            return _tracker.Create(container, "session", _options, null, ownsScope);
        }

        private static async Task<bool> Settles(Task task, int ms = 3000)
            => ReferenceEquals(await Task.WhenAny(task, Task.Delay(ms)), task);

        // ------------------------------------------------------------------ C1: throwing cancellation callbacks

        /// <summary>Registers a callback that throws, the way <c>ct.Register(() => request.Abort())</c> can.</summary>
        public sealed class ThrowingAbort : IAsyncInitializable
        {
            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                cancellationToken.Register(() => throw new InvalidOperationException("abort exploded"));
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }

        public sealed class HaltsAfterAYield : IAsyncInitializable
        {
            public Exception? HaltThrew;

            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                await Task.Yield();
                try
                {
                    context.Halt("stop");
                }
                catch (Exception exception)
                {
                    HaltThrew = exception;
                    throw;
                }
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task AThrowingCancellationCallbackDoesNotWedgeAHalt()
        {
            var run = Run(b =>
            {
                b.Add<ThrowingAbort>();
                b.Add<HaltsAfterAYield>();
            });

            var task = run.RunAsync();
            Assert.That(await Settles(task), Is.True, "the halt never settled\n" + _log.Dump());
            var result = await task;

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Halted), _log.Dump());
            Assert.That(result.HaltedBy, Is.EqualTo(nameof(HaltsAfterAYield)));
            Assert.That(_log.Has(LogLevel.Error, "a cancellation callback threw"), Is.True, _log.Dump());
        }

        [Init(Optional = true, TimeoutSeconds = 0.05)]
        public sealed class TimesOutWithAThrowingCallback : IAsyncInitializable
        {
            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                cancellationToken.Register(() => throw new InvalidOperationException("abort exploded"));
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }

        public sealed class AfterTheTimeout : AutoService
        {
            public AfterTheTimeout(TimesOutWithAThrowingCallback dependency) { }
        }

        [Test]
        [Timeout(10000)]
        public async Task AThrowingCancellationCallbackOnATimeoutStillReleasesTheDependents()
        {
            _options.TimeoutMultiplier = 1;
            var run = Run(b =>
            {
                b.Add<TimesOutWithAThrowingCallback>();
                b.Add<AfterTheTimeout>();
            });

            var task = run.RunAsync();
            Assert.That(await Settles(task), Is.True, "the dependent of the timed-out service never started\n" + _log.Dump());
            var result = await task;

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(result.Degraded, Is.EqualTo(new[] { nameof(TimesOutWithAThrowingCallback) }));
            Assert.That(run.GetStatus().Service(nameof(AfterTheTimeout)).State, Is.EqualTo(ServiceState.Completed));
        }

        public sealed class KeepsAThrowingCallback : IAsyncInitializable, IAsyncDisposable, IDisposable
        {
            public int AsyncDisposed;
            public int Disposed;

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                // Background work that outlives InitializeAsync, cancelled by the teardown.
                cancellationToken.Register(() => throw new InvalidOperationException("abort exploded"));
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                AsyncDisposed++;
                return default;
            }

            public void Dispose() => Disposed++;
        }

        [Test]
        [Timeout(10000)]
        public async Task AThrowingCancellationCallbackDoesNotAbortTheDisposal()
        {
            var run = Run(b => b.Add<KeepsAThrowingCallback>(), ownsScope: true);
            await run.RunAsync();
            var service = ServiceOf<KeepsAThrowingCallback>(run);

            await AsyncTestAssert.DoesNotThrowAsync(() => run.DisposeAsync().AsTask());

            Assert.That(service.AsyncDisposed, Is.EqualTo(1), "DisposeAsync of the service was skipped\n" + _log.Dump());
            Assert.That(service.Disposed, Is.EqualTo(1), "the scope was not disposed\n" + _log.Dump());
            Assert.That(_log.Has(LogLevel.Error, "a cancellation callback threw"), Is.True, _log.Dump());
        }

        // ------------------------------------------------------------------ C2: throwing logger

        public sealed class ThrowingLogger : ILogger
        {
            public LogLevel From = LogLevel.Trace;
            public int Calls;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                Calls++;
                if (logLevel >= From) throw new InvalidOperationException("logger exploded");
            }
        }

        public sealed class First : AutoService { }

        public sealed class Second : AutoService
        {
            public Second(First first) { }
        }

        public sealed class Explodes : IAsyncInitializable
        {
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
                => Task.FromException(new InvalidOperationException("service exploded"));
        }

        [Test]
        [Timeout(10000)]
        public async Task AThrowingLoggerNeverWedgesARun()
        {
            var logger = new ThrowingLogger();
            _options.Logger = logger;
            var run = Run(b =>
            {
                b.Add<First>();
                b.Add<Second>();
            });

            Task<StartupResult>? task = null;
            Assert.DoesNotThrow(() => task = run.RunAsync());
            Assert.That(await Settles(task!), Is.True, "a throwing logger wedged the run");
            Assert.That((await task!).Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(run.GetStatus().Service(nameof(Second)).State, Is.EqualTo(ServiceState.Completed));
            Assert.That(logger.Calls, Is.GreaterThan(0));
        }

        [Test]
        [Timeout(10000)]
        public async Task AThrowingLoggerDoesNotReplaceTheRunFailure()
        {
            _options.Logger = new ThrowingLogger { From = LogLevel.Error };
            var run = Run(b => b.Add<Explodes>());

            var task = run.RunAsync();
            Assert.That(await Settles(task), Is.True, "a throwing logger wedged the failing run");
            var failure = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => task);
            Assert.That(failure.Service, Is.EqualTo(nameof(Explodes)));
        }

        // ------------------------------------------------------------------ C3: frozen runs

        public sealed class GateA : ControlledService { }

        public sealed class GateB : ControlledService { }

        [Test]
        [Timeout(10000)]
        public async Task AFrozenRunNeverCompletesOnItsOwnEvenWhenEveryServiceIsDone()
        {
            var observer = new CollectingObserver();
            _options.Observers.Add(observer);
            var run = Run(b =>
            {
                b.Add<GateA>();
                b.Add<GateB>();
            });
            var task = run.RunAsync();
            var a = ServiceOf<GateA>(run);
            var b2 = ServiceOf<GateB>(run);
            await a.Started;
            await b2.Started;

            run.Freeze();
            a.Fail(new OperationCanceledException("bailing out after requesting a restart"));
            b2.Release();
            for (var i = 0; i < 5; i++) await Task.Yield();

            Assert.That(task.IsCompleted, Is.False, "a frozen run with a cancelled service reported an outcome on its own");
            Assert.That(observer.Events, Has.None.StartsWith("run-completed"));

            await run.CancelAsync();
            Assert.That(task.IsCanceled, Is.True);
            Assert.That(run.State, Is.EqualTo(RunState.Cancelled));
            Assert.That(observer.Events, Has.None.StartsWith("run-completed"));
        }

        [Test]
        [Timeout(10000)]
        public async Task AnEmptyRunFrozenBeforeItStartsWaitsForItsCancellation()
        {
            var run = Run(_ => { });
            run.Freeze();

            var task = run.RunAsync();
            Assert.That(task.IsCompleted, Is.False, "a pre-frozen empty graph completed synchronously");

            await run.CancelAsync();
            Assert.That(task.IsCanceled, Is.True);
        }

        // ------------------------------------------------------------------ C4: failures while stopping

        public sealed class ThrowsWhenCancelled : IAsyncInitializable
        {
            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException("cleanup exploded");
                }
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task AFailureDuringAHaltSettleIsLogged()
        {
            var run = Run(b =>
            {
                b.Add<ThrowsWhenCancelled>();
                b.Add<HaltsAfterAYield>();
            });

            var result = await run.RunAsync();

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Halted));
            var warning = _log.Find(LogLevel.Warning, nameof(ThrowsWhenCancelled));
            Assert.That(warning, Is.Not.Null, _log.Dump());
            Assert.That(warning, Does.Contain("InvalidOperationException"));
            Assert.That(warning, Does.Contain("cleanup exploded"));
            Assert.That(warning, Does.Contain("while the run was halting"));
        }

        // ------------------------------------------------------------------ C5: late progress

        public sealed class KeepsItsContext : IAsyncInitializable
        {
            public InitContext? Context;

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                Context = context;
                return Task.CompletedTask;
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task ProgressReportedAfterCompletionIsIgnored()
        {
            var run = Run(b => b.Add<KeepsItsContext>());
            await run.RunAsync();

            ServiceOf<KeepsItsContext>(run).Context!.ReportProgress(0.3f);

            Assert.That(run.GetStatus().Service(nameof(KeepsItsContext)).Progress, Is.EqualTo(1f));
            Assert.That(run.GetStatus().Percent, Is.EqualTo(100.0));
        }

        // ------------------------------------------------------------------ C6: timed-out services and disposal

        public sealed class Order
        {
            public List<string> Entries { get; } = new List<string>();
        }

        [Init(Optional = true, TimeoutSeconds = 0.05)]
        public sealed class OutlivesItsTimeout : IAsyncInitializable, IAsyncDisposable
        {
            private readonly Order _order;

            public OutlivesItsTimeout(Order order) => _order = order;

            public int HangMs = 300;

            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                await Task.Delay(HangMs);
                _order.Entries.Add("initialize returned");
            }

            public ValueTask DisposeAsync()
            {
                _order.Entries.Add("disposed");
                return default;
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task ATimedOutServiceIsDisposedOnlyOnceItsInitializeAsyncReturned()
        {
            _options.TimeoutMultiplier = 1;
            _options.CancellationGrace = TimeSpan.FromSeconds(2);
            var order = new Order();
            var run = Run(b =>
            {
                b.RegisterInstance(order);
                b.Add<OutlivesItsTimeout>();
            });

            var result = await run.RunAsync();
            Assert.That(result.Degraded, Is.EqualTo(new[] { nameof(OutlivesItsTimeout) }));

            await run.DisposeAsync();

            Assert.That(order.Entries, Is.EqualTo(new[] { "initialize returned", "disposed" }), _log.Dump());
        }

        [Test]
        [Timeout(10000)]
        public async Task ATimedOutServiceThatOutlivesTheGraceIsReportedAndDisposedAnyway()
        {
            _options.TimeoutMultiplier = 1;
            _options.CancellationGrace = TimeSpan.FromMilliseconds(100);
            var order = new Order();
            var run = Run(b =>
            {
                b.RegisterInstance(order);
                b.Add<OutlivesItsTimeout>();
            });
            ServiceOf<OutlivesItsTimeout>(run).HangMs = 1500;

            await run.RunAsync();
            await run.DisposeAsync();

            Assert.That(order.Entries, Is.EqualTo(new[] { "disposed" }));
            var error = _log.Find(LogLevel.Error, "still running");
            Assert.That(error, Is.Not.Null, _log.Dump());
            Assert.That(error, Does.Contain(nameof(OutlivesItsTimeout)));
        }

        // ------------------------------------------------------------------ C7: status polling

        [Test]
        [Timeout(10000)]
        public async Task StatusSnapshotsReuseTheImmutableDependencyLists()
        {
            var run = Run(b =>
            {
                b.Add<GateA>();
                b.Add<Second>();
                b.Add<First>();
            });
            var task = run.RunAsync();
            await ServiceOf<GateA>(run).Started;

            var pending = run.GetStatus();
            Assert.That(pending.Service(nameof(Second)).Dependencies, Is.EqualTo(new[] { nameof(First) }));
            Assert.That(pending.Service(nameof(GateA)).WaitingOn, Is.Empty);

            ServiceOf<GateA>(run).Release();
            await task;

            var one = run.GetStatus();
            var two = run.GetStatus();
            Assert.That(one.Service(nameof(Second)).Dependencies, Is.EqualTo(new[] { nameof(First) }));
            Assert.That(one.Service(nameof(Second)).WaitingOn, Is.Empty);
            Assert.That(one.Service(nameof(Second)).State, Is.EqualTo(ServiceState.Completed));
            Assert.That(two.Service(nameof(Second)).Dependencies,
                Is.SameAs(one.Service(nameof(Second)).Dependencies), "dependency names are rebuilt on every poll");
            Assert.That(two.Service(nameof(Second)).WaitingOn,
                Is.SameAs(one.Service(nameof(Second)).WaitingOn), "an empty WaitingOn list is allocated on every poll");
        }

        // ------------------------------------------------------------------ C8: DegradedServices across awaits

        [Init(Optional = true)]
        public sealed class DegradesAtOnce : IAsyncInitializable
        {
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
                => Task.FromException(new InvalidOperationException("first degradation"));
        }

        [Init(Optional = true)]
        public sealed class DegradesLater : ControlledService { }

        public sealed class EnumeratesAcrossAnAwait : IAsyncInitializable
        {
            public TaskCompletionSource<bool> InLoop { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Gate { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public List<string> Seen { get; } = new List<string>();
            public List<string> Reread { get; } = new List<string>();

            public EnumeratesAcrossAnAwait(DegradesAtOnce dependency) { }

            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                foreach (var name in context.DegradedServices)
                {
                    Seen.Add(name);
                    InLoop.TrySetResult(true);
                    await Gate.Task;
                }
                Reread.AddRange(context.DegradedServices);
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task EnumeratingDegradedServicesAcrossAnAwaitSurvivesAnotherDegradation()
        {
            var run = Run(b =>
            {
                b.Add<DegradesAtOnce>();
                b.Add<DegradesLater>();
                b.Add<EnumeratesAcrossAnAwait>();
            });
            var task = run.RunAsync();
            var reader = ServiceOf<EnumeratesAcrossAnAwait>(run);
            await reader.InLoop.Task;

            ServiceOf<DegradesLater>(run).Fail(new InvalidOperationException("second degradation"));
            await AsyncTestAssert.Until(() => run.GetStatus().Service(nameof(DegradesLater)).State == ServiceState.Degraded,
                TimeSpan.FromSeconds(3));
            reader.Gate.TrySetResult(true);

            var result = await task;
            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed), _log.Dump());
            Assert.That(reader.Seen, Is.EqualTo(new[] { nameof(DegradesAtOnce) }));
            Assert.That(reader.Reread, Is.EquivalentTo(new[] { nameof(DegradesAtOnce), nameof(DegradesLater) }));
        }

        // ------------------------------------------------------------------ C10: observers added before RunAsync

        [Test]
        [Timeout(10000)]
        public async Task AnObserverAddedBetweenCreateAndRunAsyncReceivesTheRun()
        {
            var run = Run(b => b.Add<First>());
            var observer = new CollectingObserver();
            _options.Observers.Add(observer);

            await run.RunAsync();

            Assert.That(observer.Events, Does.Contain("run-completed:session:Completed"), string.Join(", ", observer.Events));
        }

        // ------------------------------------------------------------------ C11: observers that cancel

        public sealed class CancelsOnFailure : IRuntimeFlowObserver
        {
            public ScopeRun? Run;

            public void OnServiceFailed(ServiceStatus service, Exception error)
            {
                if (Run != null) _ = Run.CancelAsync();
            }
        }

        [Init(TimeoutSeconds = 0.05)]
        public sealed class RequiredTimeout : IAsyncInitializable
        {
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
                => Task.Delay(Timeout.Infinite, cancellationToken);
        }

        [Test]
        [Timeout(10000)]
        public async Task AnObserverCancellingOnAFailureDoesNotHideTheFailure()
        {
            var observer = new CancelsOnFailure();
            _options.Observers.Add(observer);
            var run = Run(b => b.Add<Explodes>());
            observer.Run = run;

            var failure = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => run.RunAsync());

            Assert.That(failure.Service, Is.EqualTo(nameof(Explodes)));
            Assert.That(run.State, Is.EqualTo(RunState.Failed));
        }

        [Test]
        [Timeout(10000)]
        public async Task AnObserverCancellingOnATimeoutDoesNotHideTheTimeout()
        {
            _options.TimeoutMultiplier = 1;
            var observer = new CancelsOnFailure();
            _options.Observers.Add(observer);
            var run = Run(b => b.Add<RequiredTimeout>());
            observer.Run = run;

            var failure = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => run.RunAsync());

            Assert.That(failure.InnerException, Is.TypeOf<TimeoutException>());
        }

        // ------------------------------------------------------------------ C12: disposal bookkeeping

        public sealed class StuckScope : IDisposable
        {
            private readonly Exception _error = new InvalidOperationException("stuck");
            public int Calls;

            public void Dispose()
            {
                Calls++;
                throw _error;
            }
        }

        public sealed class FailsIdenticallyFiveTimes : IDisposable
        {
            public int Calls;

            public void Dispose()
            {
                if (++Calls <= 5) throw new InvalidOperationException("already disposed");
            }
        }

        [Test]
        public void AScopeThatAlwaysThrowsStopsAtTheRetryLimit()
        {
            var scope = new StuckScope();

            ScopeDisposal.Dispose(scope, _log, "session");

            Assert.That(scope.Calls, Is.EqualTo(64), "exception identity cannot establish disposal progress");
            Assert.That(_log.Messages(LogLevel.Error).Count, Is.EqualTo(3), _log.Dump());
            Assert.That(_log.Has(LogLevel.Error, "retry limit of 64"), Is.True, _log.Dump());
            Assert.That(_log.Has(LogLevel.Error, "no progress"), Is.False, _log.Dump());
        }

        [Test]
        public void IdenticalFailuresOfDifferentDisposablesAreRetriedButLoggedOnce()
        {
            var scope = new FailsIdenticallyFiveTimes();

            ScopeDisposal.Dispose(scope, _log, "session");

            Assert.That(scope.Calls, Is.EqualTo(6), "each identical-looking failure may still be progress");
            var errors = _log.Messages(LogLevel.Error);
            Assert.That(errors.Count(e => e.Contains("disposing the scope threw InvalidOperationException")), Is.EqualTo(1), _log.Dump());
            Assert.That(_log.Has(LogLevel.Error, "4 more times"), Is.True, _log.Dump());
        }

        public sealed class CountsDisposal : IAsyncInitializable, IAsyncDisposable
        {
            public int Disposed;
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;

            public ValueTask DisposeAsync()
            {
                Disposed++;
                return default;
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task ADisposalCallbackThatDisposesAgainSharesTheTeardown()
        {
            var run = Run(b => b.Add<CountsDisposal>());
            await run.RunAsync();
            var service = ServiceOf<CountsDisposal>(run);
            ValueTask? inner = null;
            var reentered = false;
            run.Disposing = r =>
            {
                if (reentered) return;
                reentered = true;
                inner = r.DisposeAsync();
            };

            await run.DisposeAsync();
            await inner!.Value;

            Assert.That(service.Disposed, Is.EqualTo(1), "a re-entrant DisposeAsync started a second teardown");
        }

        /// <summary>The instance the run's container holds; the runs here are built over <see cref="_container"/>.</summary>
        private T ServiceOf<T>(ScopeRun run) where T : class => _container.Resolve<T>();
    }
}
