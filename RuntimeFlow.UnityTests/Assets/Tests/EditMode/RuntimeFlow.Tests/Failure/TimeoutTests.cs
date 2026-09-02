using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Failure
{
    /// <summary>
    /// Timeouts exist only where a service declares one; everything else gets a stall warning.
    /// These are the only tests that depend on real time, with 50-200 ms thresholds.
    /// </summary>
    [TestFixture]
    public sealed class TimeoutTests
    {
        [Init(TimeoutSeconds = 0.1)]
        public sealed class Slow : ControlledService { }

        [Init(TimeoutSeconds = 0.1, Optional = true)]
        public sealed class SlowOptional : ControlledService { }

        [Init(UserGated = true)]
        public sealed class GdprConsent : ControlledService { }

        public sealed class Worker : ControlledService { }

        public sealed class Waiting : ControlledService
        {
            public Waiting(Worker worker) => Worker = worker;

            public Worker Worker { get; }
        }

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

        /// <summary>Disposes every run and container this fixture created, so nothing leaks into the next test.</summary>
        [TearDown]
        public void DisposeTrackedRuns() => _tracker.DisposeAll();

        [Test]
        [Timeout(10000)]
        public async Task ADeclaredTimeoutFailsTheServiceWithAnExplicitMessage()
        {
            var container = _tracker.Build(b => b.Add<Slow>());
            var slow = container.Resolve<Slow>();
            var run = _tracker.Create(container, "session", _options);

            var running = run.RunAsync();
            await slow.Started;

            var error = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => running);

            Assert.That(error.InnerException, Is.TypeOf<TimeoutException>());
            Assert.That(error.InnerException!.Message, Is.EqualTo(
                "Slow did not complete within 0.1s (limit 0.1s from [Init(TimeoutSeconds = 0.1)], multiplier 1.0)."));
            Assert.That(run.GetStatus().Service("Slow").State, Is.EqualTo(ServiceState.Failed));
        }

        [Test]
        [Timeout(10000)]
        public async Task AnOptionalTimeoutOnlyDegradesTheRun()
        {
            var container = _tracker.Build(b => b.Add<SlowOptional>());
            var slow = container.Resolve<SlowOptional>();
            var run = _tracker.Create(container, "session", _options);

            var result = await run.RunAsync();

            Assert.That(slow.Attempts, Is.EqualTo(1));
            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(result.Degraded, Is.EqualTo(new[] { "SlowOptional" }));
        }

        [Test]
        [Timeout(10000)]
        public async Task TimeoutMultiplierZeroDisablesEveryTimeout()
        {
            _options.TimeoutMultiplier = 0;
            var container = _tracker.Build(b => b.Add<Slow>());
            var slow = container.Resolve<Slow>();
            var run = _tracker.Create(container, "session", _options);

            var running = run.RunAsync();
            await slow.Started;
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            Assert.That(run.GetStatus().Service("Slow").State, Is.EqualTo(ServiceState.Running));

            slow.Release();
            var result = await running;
            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
        }

        [Test]
        [Timeout(10000)]
        public async Task AUserGatedServiceNeverTimesOutAndReportsAwaitingPlayer()
        {
            var observer = new CollectingObserver();
            _options.Observers.Add(observer);
            _options.StallWarningAfter = TimeSpan.FromMilliseconds(50);
            var container = _tracker.Build(b => b.Add<GdprConsent>());
            var consent = container.Resolve<GdprConsent>();
            var run = _tracker.Create(container, "session", _options);

            var running = run.RunAsync();
            await consent.Started;
            await AsyncTestAssert.Until(
                () => _log.Has(LogLevel.Information, "awaiting player: GdprConsent ("),
                TimeSpan.FromSeconds(5),
                _log.Dump());

            var status = run.GetStatus();
            Assert.That(status.Service("GdprConsent").State, Is.EqualTo(ServiceState.Running));
            Assert.That(status.Service("GdprConsent").AwaitingPlayer, Is.True);
            Assert.That(observer.Contains("awaiting:session:GdprConsent"), Is.True);
            Assert.That(_log.Has(LogLevel.Warning, "no progress for"), Is.False, _log.Dump());
            Assert.That(_log.Has(LogLevel.Information, "awaiting player: GdprConsent ("), Is.True, _log.Dump());

            consent.Release();
            await running;
        }

        [Test]
        [Timeout(10000)]
        public async Task AStalledRunWarnsWithRunningAndBlockedServices()
        {
            _options.StallWarningAfter = TimeSpan.FromMilliseconds(50);
            var container = _tracker.Build(b =>
            {
                b.Add<Worker>();
                b.Add<Waiting>();
            });
            var worker = container.Resolve<Worker>();
            container.Resolve<Waiting>().AutoComplete = true;
            var run = _tracker.Create(container, "session", _options);

            var running = run.RunAsync();
            await worker.Started;
            await AsyncTestAssert.Until(
                () => _log.Has(LogLevel.Warning, "no progress for"),
                TimeSpan.FromSeconds(5),
                _log.Dump());

            var message = _log.Find(LogLevel.Warning, "no progress for");
            Assert.That(message, Is.Not.Null, _log.Dump());
            Assert.That(message, Does.StartWith("[RuntimeFlow] session: no progress for 0.1s. Running: Worker ("));
            Assert.That(message, Does.EndWith(". Blocked: Waiting (waits for Worker)."));

            worker.Release();
            await running;
        }
    }
}
