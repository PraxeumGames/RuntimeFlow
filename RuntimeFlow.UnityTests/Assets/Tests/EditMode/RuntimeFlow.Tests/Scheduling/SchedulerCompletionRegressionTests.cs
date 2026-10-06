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
    /// <summary>Completed tasks ahead of their observations, invalid progress and grace abandonment.</summary>
    [TestFixture]
    public sealed class SchedulerCompletionRegressionTests
    {
        public sealed class ProgressGate : ControlledService { }

        [Init(Weight = double.MaxValue)]
        public sealed class HeavyOne : ControlledService { }

        [Init(Weight = double.MaxValue)]
        public sealed class HeavyTwo : ControlledService { }

        public class IgnoresCancellation : IAsyncInitializable
        {
            public InitContext? Context;
            public CancellationToken Token;
            public TaskCompletionSource<bool> Gate { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                Context = context;
                Token = cancellationToken;
                return Gate.Task;
            }
        }

        [Init(TimeoutSeconds = 0.2)]
        public sealed class RequiredDeadline : IgnoresCancellation { }

        [Init(Optional = true, TimeoutSeconds = 0.2)]
        public sealed class OptionalDeadline : IgnoresCancellation { }

        public sealed class EarlierCancellation : IgnoresCancellation { }

        public sealed class FirstObservedFailure : IgnoresCancellation { }

        public sealed class CompletionTokenObserver : IRuntimeFlowObserver
        {
            public OptionalDeadline? Service;
            public bool CancelledAtCompletion;

            public void OnRunCompleted(string scope, StartupResult result)
                => CancelledAtCompletion = Service!.Token.IsCancellationRequested;
        }

        public sealed class InlineCompletingSibling : IAsyncInitializable
        {
            // The observer intentionally resumes the scheduler's await inline on the captured context.
            public TaskCompletionSource<bool> Gate { get; } = new TaskCompletionSource<bool>();

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Gate.Task;
        }

        public sealed class CompletesSiblingOnTimeout : IRuntimeFlowObserver
        {
            public ScopeRun? Run;
            public OptionalDeadline? Expired;
            public InlineCompletingSibling? Sibling;
            public bool SiblingCompletedInsideFailure;
            public bool CancelledAtCompletion;
            public bool CompletedOutsideFailure;
            private bool _insideFailure;

            public void OnServiceFailed(ServiceStatus service, Exception error)
            {
                if (service.Name != nameof(OptionalDeadline)) return;
                _insideFailure = true;
                Sibling!.Gate.TrySetResult(true);
                SiblingCompletedInsideFailure = Run!.GetStatus().Service(nameof(InlineCompletingSibling)).State
                    == ServiceState.Completed;
                _insideFailure = false;
            }

            public void OnRunCompleted(string scope, StartupResult result)
            {
                CancelledAtCompletion = Expired!.Token.IsCancellationRequested;
                CompletedOutsideFailure = !_insideFailure;
            }
        }

        [Init(Optional = true)]
        public sealed class InlineNotificationA : IAsyncInitializable
        {
            public TaskCompletionSource<bool> Gate { get; } = new TaskCompletionSource<bool>();

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Gate.Task;
        }

        public sealed class InlineNotificationB : IAsyncInitializable
        {
            public TaskCompletionSource<bool> Gate { get; } = new TaskCompletionSource<bool>();

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Gate.Task;
        }

        public sealed class CompletesPeerDuringNotification : IRuntimeFlowObserver
        {
            public InlineNotificationB? Peer;
            public CollectingObserver Recorded { get; } = new CollectingObserver();

            public void OnServiceCompleted(ServiceStatus service)
            {
                Recorded.OnServiceCompleted(service);
                if (service.Name == nameof(InlineNotificationA)) Peer!.Gate.TrySetResult(true);
            }

            public void OnServiceFailed(ServiceStatus service, Exception error)
            {
                Recorded.OnServiceFailed(service, error);
                if (service.Name == nameof(InlineNotificationA)) Peer!.Gate.TrySetResult(true);
            }

            public void OnRunCompleted(string scope, StartupResult result) => Recorded.OnRunCompleted(scope, result);
        }

        [Init(TimeoutSeconds = 0.2)]
        public sealed class CompletesBeforeDeadline : IAsyncInitializable
        {
            public TaskCompletionSource<bool> Gate { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public Stopwatch Clock { get; } = new Stopwatch();

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                Clock.Start();
                return Gate.Task;
            }
        }

        private sealed class QueuedContext : SynchronizationContext, IDisposable
        {
            private readonly Queue<Action> _callbacks = new Queue<Action>();
            private readonly AutoResetEvent _posted = new AutoResetEvent(false);
            private bool _disposed;

            public override void Post(SendOrPostCallback callback, object? state)
            {
                lock (_callbacks)
                {
                    if (_disposed) return;
                    _callbacks.Enqueue(() => callback(state));
                    _posted.Set();
                }
            }

            public bool WaitForPost(int milliseconds) => _posted.WaitOne(milliseconds);

            public void PumpAll()
            {
                while (true)
                {
                    Action callback;
                    lock (_callbacks)
                    {
                        if (_callbacks.Count == 0) return;
                        callback = _callbacks.Dequeue();
                    }
                    callback();
                }
            }

            public void PumpUntil(Task task)
            {
                var bound = Stopwatch.StartNew();
                while (!task.IsCompleted && bound.Elapsed < TimeSpan.FromSeconds(2))
                {
                    Action? callback = null;
                    lock (_callbacks)
                    {
                        if (_callbacks.Count > 0) callback = _callbacks.Dequeue();
                    }
                    if (callback != null) callback();
                    else WaitForPost(10);
                }
                Assert.That(task.IsCompleted, Is.True, "The manually pumped operation never settled.");
            }

            public void Dispose()
            {
                lock (_callbacks)
                {
                    _disposed = true;
                    _callbacks.Clear();
                    _posted.Dispose();
                }
            }
        }

        private static void CompleteBeforeQueuedTickResumes(
            QueuedContext context, CompletesBeforeDeadline service, Action complete)
        {
            // The still-incomplete task cannot post an observation, so the first post is the watch.
            Assert.That(context.WaitForPost(1000), Is.True, "The first watch tick was not posted.");
            complete();
            Assert.That(service.Clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(0.2)),
                "Proof precondition: the raw task must finish before its deadline.");
            Assert.That(service.Gate.Task.IsCompleted, Is.True);
            Assert.That(context.WaitForPost(1000), Is.True, "The completed task's observation was not queued.");

            var remaining = TimeSpan.FromSeconds(0.22) - service.Clock.Elapsed;
            if (remaining > TimeSpan.Zero) Thread.Sleep(remaining);
        }

        [Test]
        [Timeout(10000)]
        public async Task NaNProgressCannotEscapeTheDocumentedProgressRange()
        {
            var options = TestScope.Options(new CapturingLogger());
            var container = TestScope.Build(b => b.Add<ProgressGate>());
            var run = ScopeRun.Create(container, "progress-proof", options);
            var service = container.Resolve<ProgressGate>();
            var running = run.RunAsync();
            try
            {
                await service.Started;
                service.Context!.ReportProgress(0.25f);
                service.Context.ReportProgress(float.NaN);
                var status = run.GetStatus();
                var progress = status.Service(nameof(ProgressGate)).Progress;

                Assert.That(progress, Is.EqualTo(0.25f), "An invalid report must preserve the last valid progress.");
                Assert.That(status.Percent, Is.EqualTo(25.0), "Invalid progress must not corrupt the weighted percentage.");
            }
            finally
            {
                service.Release();
                await running;
                await run.DisposeAsync();
                container.Dispose();
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task ContextRejectsCallbacksAfterStandaloneCancellationAbandonsItsService()
        {
            var options = TestScope.Options(new CapturingLogger());
            options.CancellationGrace = TimeSpan.Zero;
            var container = TestScope.Build(b => b.Add<IgnoresCancellation>());
            var run = ScopeRun.Create(container, "abandonment-proof", options);
            var service = container.Resolve<IgnoresCancellation>();
            var running = run.RunAsync();
            try
            {
                Assert.That(service.Context, Is.Not.Null);
                await run.CancelAsync();
                Assert.That(running.IsCanceled, Is.True);
                Assert.That(run.GetStatus().Service(nameof(IgnoresCancellation)).State,
                    Is.EqualTo(ServiceState.Cancelled));
                Assert.That(service.Gate.Task.IsCompleted, Is.False,
                    "The initializer must still be running when the framework abandons it.");

                // The grace expired and the initializer was abandoned, although disposal has not begun.
                Assert.Throws<ObjectDisposedException>(() => service.Context!.ReportProgress(0.5f));
                Assert.Throws<ObjectDisposedException>(() => service.Context!.Halt("late-halt"));
            }
            finally
            {
                service.Gate.TrySetResult(true);
                await run.DisposeAsync();
                container.Dispose();
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [Timeout(10000)]
        public void AQueuedWatchTickSettlesTheOriginalOutcomeOfAnAlreadyCompletedTask(int outcome)
        {
            var original = SynchronizationContext.Current;
            using var context = new QueuedContext();
            var options = TestScope.Options(new CapturingLogger());
            options.TimeoutMultiplier = 1;
            var observer = new CollectingObserver();
            options.Observers.Add(observer);
            var container = TestScope.Build(b => b.Add<CompletesBeforeDeadline>());
            var run = ScopeRun.Create(container, "queued-timeout-proof", options);
            var service = container.Resolve<CompletesBeforeDeadline>();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var running = run.RunAsync();
                var originalError = new InvalidOperationException("original initializer failure");
                CompleteBeforeQueuedTickResumes(context, service, () =>
                {
                    if (outcome == 1) service.Gate.TrySetException(originalError);
                    else if (outcome == 2) service.Gate.TrySetCanceled();
                    else service.Gate.TrySetResult(true);
                });
                context.PumpUntil(running);
                context.PumpAll();

                if (outcome != 0)
                {
                    Assert.That(running.IsFaulted, Is.True);
                    var failure = running.Exception!.InnerException as RuntimeFlowException;
                    Assert.That(failure, Is.Not.Null);
                    if (outcome == 1)
                        Assert.That(failure!.InnerException, Is.SameAs(originalError),
                            "A completed task's original failure must survive the delayed watch tick.");
                    else
                        Assert.That(failure!.InnerException, Is.InstanceOf<OperationCanceledException>(),
                            "An unexpected service cancellation must remain a required failure, not a timeout.");
                    Assert.That(observer.Events.Count(e => e == "failed:queued-timeout-proof:CompletesBeforeDeadline"),
                        Is.EqualTo(1));
                }
                else
                {
                    Assert.That(running.Status, Is.EqualTo(TaskStatus.RanToCompletion),
                        "A delayed watch tick must not time out an initializer that already succeeded. " + running.Exception);
                    Assert.That(running.Result.Outcome, Is.EqualTo(StartupOutcome.Completed));
                    Assert.That(observer.Events.Count(e => e == "completed:queued-timeout-proof:CompletesBeforeDeadline"),
                        Is.EqualTo(1));
                }
            }
            finally
            {
                try
                {
                    service.Gate.TrySetResult(true);
                    context.PumpUntil(run.DisposeAsync().AsTask());
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(original);
                    container.Dispose();
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        [Timeout(10000)]
        public void AQueuedCompletedFailureIsCollectedAlongsideARequiredTimeout(bool cancelled)
        {
            var original = SynchronizationContext.Current;
            using var context = new QueuedContext();
            var options = TestScope.Options(new CapturingLogger());
            options.TimeoutMultiplier = 1;
            options.CancellationGrace = TimeSpan.Zero;
            var container = TestScope.Build(b =>
            {
                b.Add<CompletesBeforeDeadline>();
                b.Add<RequiredDeadline>();
            });
            var run = ScopeRun.Create(container, "queued-failures", options);
            var finished = container.Resolve<CompletesBeforeDeadline>();
            var expired = container.Resolve<RequiredDeadline>();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var running = run.RunAsync();
                var originalError = new InvalidOperationException("completed failure");
                CompleteBeforeQueuedTickResumes(context, finished, () =>
                {
                    if (cancelled) finished.Gate.TrySetCanceled();
                    else finished.Gate.TrySetException(originalError);
                });
                context.PumpUntil(running);
                Assert.That(running.IsFaulted, Is.True);
                var failure = (RuntimeFlowException)running.Exception!.InnerException!;
                Assert.That(failure.Failures.Count, Is.EqualTo(2));
                var completedError = failure.Failures.Single(f => f.Service == nameof(CompletesBeforeDeadline)).Error;
                if (cancelled) Assert.That(completedError, Is.InstanceOf<OperationCanceledException>());
                else Assert.That(completedError, Is.SameAs(originalError));
                Assert.That(failure.Failures.Single(f => f.Service == nameof(RequiredDeadline)).Error,
                    Is.TypeOf<TimeoutException>());
            }
            finally
            {
                try
                {
                    finished.Gate.TrySetResult(true);
                    expired.Gate.TrySetResult(true);
                    context.PumpUntil(run.DisposeAsync().AsTask());
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(original);
                    container.Dispose();
                }
            }
        }

        [Test]
        [Timeout(10000)]
        public void InlineCompletionFromTimeoutObserverCannotPublishBeforeTimeoutTokenCancellation()
        {
            var original = SynchronizationContext.Current;
            using var context = new QueuedContext();
            var observer = new CompletesSiblingOnTimeout();
            var options = TestScope.Options(new CapturingLogger(), observer);
            options.TimeoutMultiplier = 1;
            options.CancellationGrace = TimeSpan.Zero;
            var container = TestScope.Build(b =>
            {
                b.Add<OptionalDeadline>();
                b.Add<InlineCompletingSibling>();
            });
            var run = ScopeRun.Create(container, "inline-timeout-observer", options);
            var expired = container.Resolve<OptionalDeadline>();
            var sibling = container.Resolve<InlineCompletingSibling>();
            observer.Expired = expired;
            observer.Sibling = sibling;
            observer.Run = run;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var clock = Stopwatch.StartNew();
                var running = run.RunAsync();
                Assert.That(context.WaitForPost(1000), Is.True, "The first watch tick was not posted.");
                var remaining = TimeSpan.FromSeconds(0.22) - clock.Elapsed;
                if (remaining > TimeSpan.Zero) Thread.Sleep(remaining);
                context.PumpUntil(running);

                Assert.That(observer.SiblingCompletedInsideFailure, Is.True,
                    "The regression must actually resume sibling bookkeeping inline inside OnServiceFailed.");
                Assert.That(running.Status, Is.EqualTo(TaskStatus.RanToCompletion), running.Exception?.ToString());
                Assert.That(observer.CancelledAtCompletion, Is.True,
                    "Inline sibling completion must not overtake cancellation of the timeout batch.");
                Assert.That(observer.CompletedOutsideFailure, Is.True,
                    "RunCompleted must wait until the batch's failure notification has returned.");
            }
            finally
            {
                try
                {
                    expired.Gate.TrySetResult(true);
                    sibling.Gate.TrySetResult(true);
                    context.PumpUntil(run.DisposeAsync().AsTask());
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(original);
                    container.Dispose();
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        [Timeout(10000)]
        public async Task InlineCompletionDuringAnObserverCallbackPreservesTheWholeNotificationOrder(bool fail)
        {
            var first = new CompletesPeerDuringNotification();
            var second = new CollectingObserver();
            var options = TestScope.Options(new CapturingLogger(), first, second);
            var container = TestScope.Build(b =>
            {
                b.Add<InlineNotificationA>();
                b.Add<InlineNotificationB>();
            });
            var run = ScopeRun.Create(container, "notification-reentry", options);
            var a = container.Resolve<InlineNotificationA>();
            var b2 = container.Resolve<InlineNotificationB>();
            first.Peer = b2;
            var running = run.RunAsync();
            try
            {
                Assert.That(run.GetStatus().Running.Count, Is.EqualTo(2),
                    "Both services must be pending after the initial scheduler pump has returned.");
                if (fail) a.Gate.TrySetException(new InvalidOperationException("optional failure"));
                else a.Gate.TrySetResult(true);
                await running;

                var expected = new[]
                {
                    $"{(fail ? "failed" : "completed")}:notification-reentry:InlineNotificationA",
                    "completed:notification-reentry:InlineNotificationB",
                    "run-completed:notification-reentry:Completed"
                };
                Assert.That(first.Recorded.Events, Is.EqualTo(expected));
                Assert.That(second.Events.Where(e => e.StartsWith("failed:") || e.StartsWith("completed:")
                    || e.StartsWith("run-completed:")), Is.EqualTo(expected),
                    "Every observer must receive the outer service event before the nested completion and terminal event.");
            }
            finally
            {
                a.Gate.TrySetResult(true);
                b2.Gate.TrySetResult(true);
                await running;
                await run.DisposeAsync();
                container.Dispose();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        [Timeout(10000)]
        public void ARequiredFailureCannotHideAnAlreadyCompletedUnexpectedCancellation(bool zeroGrace)
        {
            var original = SynchronizationContext.Current;
            using var context = new QueuedContext();
            var options = TestScope.Options(new CapturingLogger());
            if (zeroGrace) options.CancellationGrace = TimeSpan.Zero;
            var container = TestScope.Build(b =>
            {
                b.Add<EarlierCancellation>();
                b.Add<FirstObservedFailure>();
            });
            var run = ScopeRun.Create(container, "queued-ordinary-failures", options);
            var cancelled = container.Resolve<EarlierCancellation>();
            var failed = container.Resolve<FirstObservedFailure>();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var running = run.RunAsync();
                var originalError = new InvalidOperationException("first queued failure");
                failed.Gate.TrySetException(originalError);
                Assert.That(context.WaitForPost(1000), Is.True, "The first failure observation was not queued.");
                cancelled.Gate.TrySetCanceled();
                Assert.That(context.WaitForPost(1000), Is.True, "The unexpected cancellation observation was not queued.");
                Assert.That(cancelled.Token.IsCancellationRequested, Is.False,
                    "The raw cancellation must precede any cancellation of this service's own token.");
                context.PumpUntil(running);

                Assert.That(running.IsFaulted, Is.True);
                var failure = (RuntimeFlowException)running.Exception!.InnerException!;
                Assert.That(failure.Failures.Count, Is.EqualTo(2),
                    "Failure settlement cannot reclassify the earlier independent cancellation as its own cancellation.");
                Assert.That(failure.Failures.Single(f => f.Service == nameof(FirstObservedFailure)).Error,
                    Is.SameAs(originalError));
                Assert.That(failure.Failures.Single(f => f.Service == nameof(EarlierCancellation)).Error,
                    Is.InstanceOf<OperationCanceledException>());
            }
            finally
            {
                try
                {
                    cancelled.Gate.TrySetResult(true);
                    failed.Gate.TrySetResult(true);
                    context.PumpUntil(run.DisposeAsync().AsTask());
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(original);
                    container.Dispose();
                }
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [Timeout(10000)]
        public void EarlierUnexpectedCancellationIsRetainedWithoutChangingAnExplicitStopOutcome(int stop)
        {
            var original = SynchronizationContext.Current;
            using var context = new QueuedContext();
            var observer = new CollectingObserver();
            var options = TestScope.Options(new CapturingLogger(), observer);
            options.CancellationGrace = TimeSpan.Zero;
            var container = TestScope.Build(b =>
            {
                b.Add<EarlierCancellation>();
                b.Add<FirstObservedFailure>();
            });
            var run = ScopeRun.Create(container, "queued-explicit-stop", options);
            var earlier = container.Resolve<EarlierCancellation>();
            var later = container.Resolve<FirstObservedFailure>();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var running = run.RunAsync();
                earlier.Gate.TrySetCanceled();
                Assert.That(context.WaitForPost(1000), Is.True, "The earlier cancellation observation was not queued.");
                Assert.That(earlier.Token.IsCancellationRequested, Is.False);
                Task? cancellation = null;
                if (stop == 0) later.Context!.Halt("explicit-halt");
                else
                {
                    if (stop == 2) run.Freeze();
                    cancellation = run.CancelAsync();
                }
                later.Gate.TrySetCanceled();
                context.PumpUntil(running);
                if (cancellation != null) context.PumpUntil(cancellation);

                if (stop == 0) Assert.That(running.Result.Outcome, Is.EqualTo(StartupOutcome.Halted));
                else Assert.That(running.IsCanceled, Is.True);
                var status = run.GetStatus();
                Assert.That(status.Service(nameof(EarlierCancellation)).State, Is.EqualTo(ServiceState.Failed));
                Assert.That(status.Service(nameof(FirstObservedFailure)).State, Is.EqualTo(ServiceState.Cancelled),
                    "A future cancellation during stopping must retain the expected-cancellation policy.");
                Assert.That(observer.Events.Count(e => e == "failed:queued-explicit-stop:EarlierCancellation"), Is.EqualTo(1));
                Assert.That(observer.Events.Any(e => e == "failed:queued-explicit-stop:FirstObservedFailure"), Is.False);
            }
            finally
            {
                try
                {
                    earlier.Gate.TrySetResult(true);
                    later.Gate.TrySetResult(true);
                    context.PumpUntil(run.DisposeAsync().AsTask());
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(original);
                    container.Dispose();
                }
            }
        }

        [Test]
        [Timeout(10000)]
        public void OptionalTimeoutTokenIsCancelledBeforeQueuedSuccessCompletesTheRun()
        {
            var original = SynchronizationContext.Current;
            using var context = new QueuedContext();
            var observer = new CompletionTokenObserver();
            var options = TestScope.Options(new CapturingLogger(), observer);
            options.TimeoutMultiplier = 1;
            options.CancellationGrace = TimeSpan.Zero;
            var container = TestScope.Build(b =>
            {
                b.Add<CompletesBeforeDeadline>();
                b.Add<OptionalDeadline>();
            });
            var run = ScopeRun.Create(container, "queued-success", options);
            var finished = container.Resolve<CompletesBeforeDeadline>();
            var expired = container.Resolve<OptionalDeadline>();
            observer.Service = expired;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var running = run.RunAsync();
                CompleteBeforeQueuedTickResumes(context, finished, () => finished.Gate.TrySetResult(true));
                context.PumpUntil(running);
                Assert.That(running.Status, Is.EqualTo(TaskStatus.RanToCompletion), running.Exception?.ToString());
                Assert.That(running.Result.Degraded, Is.EqualTo(new[] { nameof(OptionalDeadline) }));
                Assert.That(observer.CancelledAtCompletion, Is.True,
                    "RunCompleted must not overtake cancellation of the optional timeout's token.");
            }
            finally
            {
                try
                {
                    finished.Gate.TrySetResult(true);
                    expired.Gate.TrySetResult(true);
                    context.PumpUntil(run.DisposeAsync().AsTask());
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(original);
                    container.Dispose();
                }
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task InvalidProgressDoesNotRefreshTheStallTimer()
        {
            var logger = new CapturingLogger();
            var options = TestScope.Options(logger);
            options.StallWarningAfter = TimeSpan.FromMilliseconds(50);
            var container = TestScope.Build(b => b.Add<ProgressGate>());
            var run = ScopeRun.Create(container, "invalid-progress-stall", options);
            var service = container.Resolve<ProgressGate>();
            var running = run.RunAsync();
            try
            {
                await service.Started;
                service.Context!.ReportProgress(0.25f);
                var bound = Stopwatch.StartNew();
                while (!logger.Has(LogLevel.Warning, "no progress") && bound.Elapsed < TimeSpan.FromSeconds(1))
                {
                    service.Context.ReportProgress(float.NaN);
                    await Task.Delay(10);
                }
                Assert.That(logger.Has(LogLevel.Warning, "no progress"), Is.True,
                    "An invalid report cannot count as activity that suppresses a stall warning.");
            }
            finally
            {
                service.Release();
                await running;
                await run.DisposeAsync();
                container.Dispose();
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task FiniteWeightsThatOverflowTheirSumStillProduceTheCorrectPercentage()
        {
            var options = TestScope.Options(new CapturingLogger());
            var container = TestScope.Build(b =>
            {
                b.Add<HeavyOne>();
                b.Add<HeavyTwo>();
            });
            var run = ScopeRun.Create(container, "weight-overflow", options);
            var one = container.Resolve<HeavyOne>();
            var two = container.Resolve<HeavyTwo>();
            var running = run.RunAsync();
            try
            {
                await one.Started;
                await two.Started;
                Assert.That(run.GetStatus().Percent, Is.EqualTo(0.0));
                one.Context!.ReportProgress(0.5f);
                Assert.That(run.GetStatus().Percent, Is.EqualTo(25.0));
                one.Release();
                await AsyncTestAssert.Until(() => run.GetStatus().Service(nameof(HeavyOne)).State == ServiceState.Completed,
                    TimeSpan.FromSeconds(2));
                Assert.That(run.GetStatus().Percent, Is.EqualTo(50.0));
                two.Release();
                await running;
                Assert.That(run.GetStatus().Percent, Is.EqualTo(100.0));
            }
            finally
            {
                one.Release();
                two.Release();
                await running;
                await run.DisposeAsync();
                container.Dispose();
            }
        }
    }
}
