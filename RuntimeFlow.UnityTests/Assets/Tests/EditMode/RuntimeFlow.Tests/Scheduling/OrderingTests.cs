using System;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Scheduling
{
    /// <summary>
    /// The scheduler is dynamic: a service starts the moment its own dependencies finish, not when a
    /// whole layer does, and independent services run interleaved.
    /// </summary>
    [TestFixture]
    public sealed class OrderingTests
    {
        public sealed class Fast : ControlledService { }

        public sealed class Slow : ControlledService { }

        public sealed class NeedsFast : ControlledService
        {
            public NeedsFast(Fast fast) => Fast = fast;

            public Fast Fast { get; }
        }

        public sealed class Independent : ControlledService { }

        /// <summary>Registered first, so it is the lowest-index root of the graph.</summary>
        public sealed class FirstRoot : AutoService { }

        /// <summary>An optional service whose constructor throws, so its node degrades before the run starts.</summary>
        [Init(Optional = true)]
        public sealed class BrokenOptional : AutoService
        {
            public BrokenOptional() => throw new InvalidOperationException("constructor exploded");
        }

        /// <summary>Ordered after the broken service, so it becomes ready the moment that one degrades.</summary>
        [DependsOn(typeof(BrokenOptional))]
        public sealed class AfterBroken : AutoService { }

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

        [Test]
        [Timeout(10000)]
        public async Task ADegradedConstructionDoesNotLetItsDependentsOvertakeTheRoots()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<FirstRoot>();
                b.Add<BrokenOptional>();
                b.Add<AfterBroken>();
            });
            var run = _tracker.Create(container, "session", _options);

            var result = await run.RunAsync();

            Assert.That(result.Degraded, Is.EqualTo(new[] { "BrokenOptional" }));
            Assert.That(_observer.IndexOf("started:session:FirstRoot"),
                Is.LessThan(_observer.IndexOf("started:session:AfterBroken")),
                "the dependents released by a construction failure queue behind the roots, in index order");
        }

        [Test]
        [Timeout(10000)]
        public async Task ADependentWaitsForItsDependency()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<NeedsFast>();
                b.Add<Fast>();
            });
            var fast = container.Resolve<Fast>();
            var dependent = container.Resolve<NeedsFast>();

            var run = _tracker.Create(container, "session", _options);
            var running = run.RunAsync();

            await fast.Started;
            Assert.That(dependent.Started.IsCompleted, Is.False, "the dependent must not start before its dependency");

            fast.Release();
            await dependent.Started;
            dependent.Release();
            await running;

            Assert.That(_observer.IndexOf("completed:session:Fast"), Is.LessThan(_observer.IndexOf("started:session:NeedsFast")));
        }

        [Test]
        [Timeout(10000)]
        public async Task IndependentServicesRunInterleaved()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<Fast>();
                b.Add<Independent>();
            });
            var fast = container.Resolve<Fast>();
            var independent = container.Resolve<Independent>();

            var run = _tracker.Create(container, "session", _options);
            var running = run.RunAsync();

            await fast.Started;
            await independent.Started;
            Assert.That(fast.Finished, Is.False);
            Assert.That(independent.Finished, Is.False);
            Assert.That(run.GetStatus().Running, Has.Count.EqualTo(2));

            independent.Release();
            fast.Release();
            await running;

            ProgressAssertions.AllCompleted(run.GetStatus());
        }

        [Test]
        [Timeout(10000)]
        public async Task ADependentStartsWhenItsOwnDependencyFinishesNotTheWholeLayer()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<Fast>();
                b.Add<Slow>();
                b.Add<NeedsFast>();
            });
            var fast = container.Resolve<Fast>();
            var slow = container.Resolve<Slow>();
            var dependent = container.Resolve<NeedsFast>();

            var run = _tracker.Create(container, "session", _options);
            var running = run.RunAsync();

            await fast.Started;
            await slow.Started;

            fast.Release();
            await dependent.Started;

            Assert.That(slow.Finished, Is.False, "the slow sibling is still running");
            Assert.That(run.GetStatus().Service("Slow").State, Is.EqualTo(ServiceState.Running));

            dependent.Release();
            slow.Release();
            await running;

            ProgressAssertions.AllCompleted(run.GetStatus());
        }

        [Test]
        [Timeout(10000)]
        public async Task ServicesStartInRegistrationOrder()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<Slow>();
                b.Add<Fast>();
                b.Add<Independent>();
            });
            foreach (var service in new ControlledService[]
                     {
                         container.Resolve<Slow>(), container.Resolve<Fast>(), container.Resolve<Independent>()
                     })
            {
                service.AutoComplete = true;
            }

            await _tracker.Create(container, "session", _options).RunAsync();

            Assert.That(_observer.Events, Is.EqualTo(new[]
            {
                "run-started:session:start",
                "started:session:Slow", "completed:session:Slow",
                "started:session:Fast", "completed:session:Fast",
                "started:session:Independent", "completed:session:Independent",
                "run-completed:session:Completed"
            }));
        }
    }
}
