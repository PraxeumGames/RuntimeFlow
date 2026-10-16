using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Graph
{
    /// <summary>Phases are barrier edges: everything in a phase finishes before the next phase starts.</summary>
    [TestFixture]
    public sealed class PhaseTests
    {
        [Init(Phase = "platform")]
        public sealed class PlatformOne : AutoService { }

        [Init(Phase = "platform")]
        public sealed class PlatformTwo : AutoService { }

        [Init(Phase = "content")]
        public sealed class ContentOne : AutoService { }

        public sealed class Unmarked : AutoService { }

        public sealed class AlsoUnmarked : AutoService { }

        [Init(Phase = "assets")]
        public sealed class UnknownPhase : AutoService { }

        [Init(Phase = "content")]
        public sealed class EarlyService : AutoService
        {
            public EarlyService(LateService late) => Late = late;

            public LateService Late { get; }
        }

        [Init(Phase = "session")]
        public sealed class LateService : AutoService { }

        /// <summary>Built by user code (an instance or a factory product), holding a service of its own phase.</summary>
        [Init(Phase = "content")]
        public sealed class PrebuiltHolder : AutoService
        {
            public PrebuiltHolder(HolderUser? user) => User = user;

            public HolderUser? User { get; }
        }

        [Init(Phase = "content")]
        public sealed class HolderUser : AutoService
        {
            public HolderUser(PrebuiltHolder holder) { }
        }

        private CapturingLogger _log = null!;

        private readonly RunTracker _tracker = new RunTracker();

        [SetUp]
        public void SetUp() => _log = new CapturingLogger();

        private RuntimeFlowOptions Options(params string[] phases)
        {
            var options = TestScope.Options(_log);
            options.Phases = phases;
            return options;
        }

        /// <summary>Disposes every run and container this fixture created, so nothing leaks into the next test.</summary>
        [TearDown]
        public void DisposeTrackedRuns() => _tracker.DisposeAll();

        [Test]
        [Timeout(10000)]
        public async Task ServicesOfALaterPhaseWaitForTheBarrier()
        {
            var observer = new CollectingObserver();
            var options = Options("platform", "content");
            options.Observers.Add(observer);

            var container = _tracker.Build(b =>
            {
                b.Add<ContentOne>();
                b.Add<PlatformOne>();
                b.Add<PlatformTwo>();
            });

            await _tracker.Create(container, "session", options).RunAsync();

            var events = observer.Events;
            Assert.That(observer.IndexOf("completed:session:PlatformOne"), Is.LessThan(observer.IndexOf("started:session:ContentOne")));
            Assert.That(observer.IndexOf("completed:session:PlatformTwo"), Is.LessThan(observer.IndexOf("started:session:ContentOne")));
            Assert.That(events, Is.EqualTo(new List<string>
            {
                "run-started:session:start",
                "phase-started:session:platform",
                "started:session:PlatformOne",
                "completed:session:PlatformOne",
                "started:session:PlatformTwo",
                "completed:session:PlatformTwo",
                "phase-completed:session:platform",
                "phase-started:session:content",
                "started:session:ContentOne",
                "completed:session:ContentOne",
                "phase-completed:session:content",
                "run-completed:session:Completed"
            }));
        }

        [Test]
        public void AnUnmarkedServiceLandsInTheLastPhase()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<PlatformOne>();
                b.Add<Unmarked>();
            });

            var run = _tracker.Create(container, "session", Options("platform", "content"));

            Assert.That(run.GetStatus().Service("Unmarked").Phase, Is.EqualTo("content"));
            Assert.That(run.GetStatus().Service("PlatformOne").Phase, Is.EqualTo("platform"));
        }

        [Test]
        public void DefaultPhaseOverridesTheLastPhase()
        {
            var options = Options("platform", "content");
            options.DefaultPhase = "platform";
            var container = _tracker.Build(b =>
            {
                b.Add<ContentOne>();
                b.Add<Unmarked>();
            });

            var run = _tracker.Create(container, "session", options);

            Assert.That(run.GetStatus().Service("Unmarked").Phase, Is.EqualTo("platform"));
        }

        [Test]
        [Timeout(10000)]
        public async Task AScopeWithoutASingleMarkedServiceRunsWithoutPhases()
        {
            var observer = new CollectingObserver();
            var options = Options("platform", "content");
            options.Observers.Add(observer);
            var container = _tracker.Build(b =>
            {
                b.Add<Unmarked>();
                b.Add<AlsoUnmarked>();
            });

            var run = _tracker.Create(container, "session", options);
            await run.RunAsync();

            var status = run.GetStatus();
            Assert.That(status.Service("Unmarked").Phase, Is.Null);
            Assert.That(status.Service("AlsoUnmarked").Phase, Is.Null);
            Assert.That(status.Phase, Is.Null);
            Assert.That(run.Describe(), Is.EqualTo(string.Join(Environment.NewLine, new[]
            {
                "scope 'session' — 2 services",
                " 1 [-] Unmarked      required, weight 1",
                " 2 [-] AlsoUnmarked  required, weight 1",
                ""
            })), run.Describe());
            Assert.That(observer.Events, Is.EqualTo(new List<string>
            {
                "run-started:session:start",
                "started:session:Unmarked",
                "completed:session:Unmarked",
                "started:session:AlsoUnmarked",
                "completed:session:AlsoUnmarked",
                "run-completed:session:Completed"
            }));
        }

        [Test]
        [Timeout(10000)]
        public async Task PhasesApplyPerScopeSoAnUnmarkedGlobalStaysWithoutOne()
        {
            var options = Options("platform", "content");
            await using var host = new RuntimeFlowHost(
                builder => builder.Add<Unmarked>(),
                builder =>
                {
                    builder.Add<PlatformOne>();
                    builder.Add<AlsoUnmarked>();
                },
                options);

            await host.StartAsync();

            var status = host.GetStatus();
            Assert.That(status.Service("Unmarked").Scope, Is.EqualTo("global"));
            Assert.That(status.Service("Unmarked").Phase, Is.Null, "no global service declares a phase");
            Assert.That(status.Service("PlatformOne").Phase, Is.EqualTo("platform"));
            Assert.That(status.Service("AlsoUnmarked").Phase, Is.EqualTo("content"),
                "one marked service is enough to put the unmarked ones of that scope in the last phase");
            Assert.That(host.Describe(), Does.Not.Contain("scope 'global' — 1 services, phases"));
        }

        [Test]
        public void AnUnknownPhaseIsRejected()
        {
            var container = _tracker.Build(b => b.Add<UnknownPhase>());

            var error = Assert.Throws<InitGraphException>(
                () => _tracker.Create(container, "session", Options("platform", "content", "session", "ui")));

            Assert.That(error!.Message, Is.EqualTo(
                "UnknownPhase declares phase 'assets', but RuntimeFlowOptions.Phases is [platform, content, session, ui]."));
        }

        [Test]
        public void APhaseWithoutADeclaredPhaseListIsRejected()
        {
            var container = _tracker.Build(b => b.Add<UnknownPhase>());

            var error = Assert.Throws<InitGraphException>(() => _tracker.Create(container, "session", Options()));

            Assert.That(error!.Message, Is.EqualTo(
                "UnknownPhase declares phase 'assets', but RuntimeFlowOptions.Phases is empty. " +
                "Declare the ordered phase list in RuntimeFlowOptions.Phases."));
        }

        [Test]
        public void InjectingAServiceOfALaterPhaseIsACycle()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<EarlyService>();
                b.Add<LateService>();
            });

            var error = Assert.Throws<InitGraphException>(
                () => _tracker.Create(container, "session", Options("content", "session")));

            Assert.That(error!.Message, Does.StartWith(
                "Initialization graph of scope 'session' has a cycle: EarlyService -> LateService -> phase 'content' -> EarlyService. " +
                "Edges: EarlyService -> LateService (ctor: LateService late), LateService -> phase 'content' (phase barrier), " +
                "phase 'content' -> EarlyService (phase barrier)."));
            Assert.That(error.Message, Does.Contain(
                "(EarlyService in phase 'content' depends on LateService in phase 'session'; a service cannot depend on a later phase)"));
        }

        /// <summary>
        /// The constructor edge of an instance or a factory product is inferred (VContainer never resolved
        /// it) and is dropped when it would close a cycle of services. A path through a phase barrier is no
        /// such cycle: an early-phase object holding a later-phase service is a phase-order error.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void AnInstanceOrFactoryHoldingAServiceOfALaterPhaseIsStillAPhaseError(bool factory)
        {
            var container = _tracker.Build(b =>
            {
                if (factory)
                    b.Register(_ => new EarlyService(new LateService()), Lifetime.Singleton).AsSelf().As<IAsyncInitializable>();
                else
                    b.RegisterInstance(new EarlyService(new LateService())).AsSelf().As<IAsyncInitializable>();
                b.Add<LateService>();
            });

            var error = Assert.Throws<InitGraphException>(
                () => _tracker.Create(container, "session", Options("content", "session")));

            Assert.That(error!.Message, Does.StartWith(
                "Initialization graph of scope 'session' has a cycle: EarlyService -> LateService -> phase 'content' -> EarlyService."));
            Assert.That(error.Message, Does.Contain(
                "(EarlyService in phase 'content' depends on LateService in phase 'session'; a service cannot depend on a later phase)"));
            Assert.That(_log.Has(Microsoft.Extensions.Logging.LogLevel.Warning, "ignoring the edge"), Is.False, _log.Dump());
        }

        [TestCase(false)]
        [TestCase(true)]
        [Timeout(10000)]
        public async Task AnInstanceOrFactoryEdgeClosingACycleOfServicesIsStillDroppedWithPhases(bool factory)
        {
            var container = _tracker.Build(b =>
            {
                if (factory)
                    b.Register(_ => new PrebuiltHolder(null), Lifetime.Singleton).AsSelf().As<IAsyncInitializable>();
                else
                    b.RegisterInstance(new PrebuiltHolder(null)).AsSelf().As<IAsyncInitializable>();
                b.Add<HolderUser>();
                b.Add<PlatformOne>();
            });

            ScopeRun? run = null;
            Assert.DoesNotThrow(() => run = _tracker.Create(container, "session", Options("platform", "content")), _log.Dump());

            Assert.That(_log.Find(Microsoft.Extensions.Logging.LogLevel.Warning, "ignoring the edge"),
                Does.Contain("PrebuiltHolder -> HolderUser"), _log.Dump());
            Assert.That((await run!.RunAsync()).Outcome, Is.EqualTo(StartupOutcome.Completed));
        }

        [Test]
        public void AnInvalidDefaultPhaseIsRejected()
        {
            var options = Options("platform", "content");
            options.DefaultPhase = "nope";
            var container = _tracker.Build(b => b.Add<Unmarked>());

            var error = Assert.Throws<InitGraphException>(() => _tracker.Create(container, "session", options));

            Assert.That(error!.Message, Is.EqualTo(
                "RuntimeFlowOptions.DefaultPhase is 'nope', but RuntimeFlowOptions.Phases is [platform, content]."));
        }
    }
}
