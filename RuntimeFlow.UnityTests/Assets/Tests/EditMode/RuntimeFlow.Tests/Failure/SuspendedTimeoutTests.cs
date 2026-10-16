using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Internal;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Failure
{
    /// <summary>
    /// Timeouts and stall warnings count only the time the application is active: a player who sends the
    /// game to the background during loading must not come back to services that timed out while nothing
    /// could run. The suspension is driven through <see cref="AppSuspension"/>, the seam the Unity focus
    /// hook uses, so no Unity event is needed.
    /// </summary>
    [TestFixture]
    public sealed class SuspendedTimeoutTests
    {
        [Init(TimeoutSeconds = 0.1)]
        public sealed class Slow : ControlledService { }

        public sealed class Worker : ControlledService { }

        private CapturingLogger _log = null!;
        private RuntimeFlowOptions _options = null!;

        private readonly RunTracker _tracker = new RunTracker();

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _options = TestScope.Options(_log);
            _options.TimeoutMultiplier = 1.0;
        }

        /// <summary>The clock is process-wide: never leave it stopped for the next test.</summary>
        [TearDown]
        public void TearDown()
        {
            AppSuspension.Resume();
            _tracker.DisposeAll();
        }

        [Test]
        [Timeout(10000)]
        public async Task ATimeoutDoesNotCountTimeTheApplicationSpentSuspended()
        {
            var container = _tracker.Build(b => b.Add<Slow>());
            var slow = container.Resolve<Slow>();
            var run = _tracker.Create(container, "session", _options);

            AppSuspension.Suspend();
            var running = run.RunAsync();
            await slow.Started;
            await Task.Delay(TimeSpan.FromMilliseconds(400));

            var status = run.GetStatus().Service("Slow");
            Assert.That(status.State, Is.EqualTo(ServiceState.Running),
                "four times its timeout passed, all of it suspended: the deadline must stand still");
            Assert.That(status.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(300)),
                "ServiceStatus.Elapsed stays wall-clock");

            AppSuspension.Resume();
            var error = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => running);

            Assert.That(error.InnerException, Is.TypeOf<TimeoutException>(), "after resuming, the deadline runs again");
            Assert.That(slow.Attempts, Is.EqualTo(1));
        }

        [Test]
        [Timeout(10000)]
        public async Task AServiceThatFinishesAfterResumingIsNotTimedOut()
        {
            var container = _tracker.Build(b => b.Add<Slow>());
            var slow = container.Resolve<Slow>();
            var run = _tracker.Create(container, "session", _options);

            AppSuspension.Suspend();
            var running = run.RunAsync();
            await slow.Started;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            AppSuspension.Resume();
            slow.Release();

            var result = await running;

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed), _log.Dump());
            Assert.That(run.GetStatus().Service("Slow").State, Is.EqualTo(ServiceState.Completed));
        }

        [Test]
        [Timeout(10000)]
        public async Task TheStallWarningDoesNotCountTimeTheApplicationSpentSuspended()
        {
            _options.StallWarningAfter = TimeSpan.FromMilliseconds(100);
            var container = _tracker.Build(b => b.Add<Worker>());
            var worker = container.Resolve<Worker>();
            var run = _tracker.Create(container, "session", _options);

            AppSuspension.Suspend();
            var running = run.RunAsync();
            await worker.Started;
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            Assert.That(_log.Has(LogLevel.Warning, "no progress for"), Is.False,
                "no stall is reported for time spent in the background\n" + _log.Dump());

            AppSuspension.Resume();
            await AsyncTestAssert.Until(
                () => _log.Has(LogLevel.Warning, "no progress for"),
                TimeSpan.FromSeconds(5),
                _log.Dump());

            worker.Release();
            await running;
        }
    }
}
