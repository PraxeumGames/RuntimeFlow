using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Progress
{
    /// <summary>Weighted progress, sub-progress and the observer callbacks that drive a loading screen.</summary>
    [TestFixture]
    public sealed class ProgressTests
    {
        [Init(Weight = 5)]
        public sealed class Heavy : ControlledService { }

        public sealed class Light : ControlledService
        {
            public Light(Heavy heavy) => Heavy = heavy;

            public Heavy Heavy { get; }
        }

        public sealed class Alpha : AutoService { }

        public sealed class Beta : AutoService { }

        /// <summary>An observer that throws from every callback; the run must survive it.</summary>
        public sealed class HostileObserver : IRuntimeFlowObserver
        {
            public int Calls { get; private set; }

            public void OnServiceStarted(ServiceStatus service)
            {
                Calls++;
                throw new InvalidOperationException("observer exploded");
            }

            public void OnRunCompleted(string scope, StartupResult result)
            {
                Calls++;
                throw new InvalidOperationException("observer exploded");
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
        public async Task PercentIsWeightedByTheInitAttribute()
        {
            var container = TestScope.Build(b =>
            {
                b.Add<Heavy>();
                b.Add<Light>();
            });
            var heavy = container.Resolve<Heavy>();
            var light = container.Resolve<Light>();
            var run = ScopeRun.Create(container, "session", _options);

            var running = run.RunAsync();
            await heavy.Started;
            ProgressAssertions.Percent(run.GetStatus(), 0.0);

            heavy.Release();
            await light.Started;
            ProgressAssertions.Percent(run.GetStatus(), 83.3);

            light.Release();
            await running;
            ProgressAssertions.AllCompleted(run.GetStatus());
        }

        [Test]
        [Timeout(10000)]
        public async Task ReportProgressFeedsTheWeightedPercentage()
        {
            var container = TestScope.Build(b =>
            {
                b.Add<Heavy>();
                b.Add<Light>();
            });
            var heavy = container.Resolve<Heavy>();
            var light = container.Resolve<Light>();
            var run = ScopeRun.Create(container, "session", _options);
            var samples = new List<double>();

            var running = run.RunAsync();
            await heavy.Started;
            samples.Add(run.GetStatus().Percent);

            heavy.Release();
            await light.Started;
            samples.Add(run.GetStatus().Percent);

            light.Context!.ReportProgress(0.5f);
            samples.Add(run.GetStatus().Percent);
            ProgressAssertions.Percent(run.GetStatus(), 91.7);
            Assert.That(run.GetStatus().Service("Light").Progress, Is.EqualTo(0.5f));

            light.Release();
            await running;
            samples.Add(run.GetStatus().Percent);

            ProgressAssertions.Monotonic(samples);
        }

        [Test]
        public void ReportProgressIsClamped()
        {
            var container = TestScope.Build(b => b.Add<Heavy>());
            var heavy = container.Resolve<Heavy>();
            var run = ScopeRun.Create(container, "session", _options);

            _ = run.RunAsync();

            heavy.Context!.ReportProgress(-3f);
            Assert.That(run.GetStatus().Service("Heavy").Progress, Is.EqualTo(0f));
            heavy.Context.ReportProgress(9f);
            Assert.That(run.GetStatus().Service("Heavy").Progress, Is.EqualTo(1f));

            heavy.Release();
        }

        [Test]
        [Timeout(10000)]
        public async Task ObserversSeeEveryLifecycleEvent()
        {
            var observer = new CollectingObserver();
            _options.Observers.Add(observer);
            var container = TestScope.Build(b =>
            {
                b.Add<Alpha>();
                b.Add<Beta>();
            });

            await ScopeRun.Create(container, "session", _options).RunAsync();

            Assert.That(observer.Events, Is.EqualTo(new[]
            {
                "run-started:session:start",
                "started:session:Alpha", "completed:session:Alpha",
                "started:session:Beta", "completed:session:Beta",
                "run-completed:session:Completed"
            }));
        }

        [Test]
        [Timeout(10000)]
        public async Task RestartIsVisibleToServicesAndObservers()
        {
            var observer = new CollectingObserver();
            _options.Observers.Add(observer);
            var container = TestScope.Build(b => b.Add<Alpha>());
            var alpha = container.Resolve<Alpha>();

            await ScopeRun.Create(container, "session", _options).RunAsync(isRestart: true, generation: 3);

            Assert.That(alpha.Context!.IsRestart, Is.True);
            Assert.That(alpha.Context.Generation, Is.EqualTo(3));
            Assert.That(alpha.Context.Scope, Is.EqualTo("session"));
            Assert.That(observer.Contains("run-started:session:restart"), Is.True);
        }

        [Test]
        [Timeout(10000)]
        public async Task AnObserverThatThrowsIsLoggedAndIgnored()
        {
            var hostile = new HostileObserver();
            _options.Observers.Add(hostile);
            var container = TestScope.Build(b => b.Add<Alpha>());

            var result = await ScopeRun.Create(container, "session", _options).RunAsync();

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(hostile.Calls, Is.EqualTo(2));
            Assert.That(_log.Find(LogLevel.Error, "observer HostileObserver"), Is.EqualTo(
                "[RuntimeFlow] session: observer HostileObserver threw InvalidOperationException in OnServiceStarted; ignored."));
        }
    }
}
