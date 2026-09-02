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

        private CapturingLogger _log = null!;

        [SetUp]
        public void SetUp() => _log = new CapturingLogger();

        private RuntimeFlowOptions Options(params string[] phases)
        {
            var options = TestScope.Options(_log);
            options.Phases = phases;
            return options;
        }

        [Test]
        [Timeout(10000)]
        public async Task ServicesOfALaterPhaseWaitForTheBarrier()
        {
            var observer = new CollectingObserver();
            var options = Options("platform", "content");
            options.Observers.Add(observer);

            var container = TestScope.Build(b =>
            {
                b.Add<ContentOne>();
                b.Add<PlatformOne>();
                b.Add<PlatformTwo>();
            });

            await ScopeRun.Create(container, "session", options).RunAsync();

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
            var container = TestScope.Build(b => b.Add<Unmarked>());

            var run = ScopeRun.Create(container, "session", Options("platform", "content"));

            Assert.That(run.GetStatus().Service("Unmarked").Phase, Is.EqualTo("content"));
        }

        [Test]
        public void DefaultPhaseOverridesTheLastPhase()
        {
            var options = Options("platform", "content");
            options.DefaultPhase = "platform";
            var container = TestScope.Build(b => b.Add<Unmarked>());

            var run = ScopeRun.Create(container, "session", options);

            Assert.That(run.GetStatus().Service("Unmarked").Phase, Is.EqualTo("platform"));
        }

        [Test]
        public void AnUnknownPhaseIsRejected()
        {
            var container = TestScope.Build(b => b.Add<UnknownPhase>());

            var error = Assert.Throws<InitGraphException>(
                () => ScopeRun.Create(container, "session", Options("platform", "content", "session", "ui")));

            Assert.That(error!.Message, Is.EqualTo(
                "UnknownPhase declares phase 'assets', but RuntimeFlowOptions.Phases is [platform, content, session, ui]."));
        }

        [Test]
        public void APhaseWithoutADeclaredPhaseListIsRejected()
        {
            var container = TestScope.Build(b => b.Add<UnknownPhase>());

            var error = Assert.Throws<InitGraphException>(() => ScopeRun.Create(container, "session", Options()));

            Assert.That(error!.Message, Is.EqualTo(
                "UnknownPhase declares phase 'assets', but RuntimeFlowOptions.Phases is empty. " +
                "Declare the ordered phase list in RuntimeFlowOptions.Phases."));
        }

        [Test]
        public void InjectingAServiceOfALaterPhaseIsACycle()
        {
            var container = TestScope.Build(b =>
            {
                b.Add<EarlyService>();
                b.Add<LateService>();
            });

            var error = Assert.Throws<InitGraphException>(
                () => ScopeRun.Create(container, "session", Options("content", "session")));

            Assert.That(error!.Message, Does.StartWith(
                "Initialization graph of scope 'session' has a cycle: EarlyService -> LateService -> phase 'content' -> EarlyService. " +
                "Edges: EarlyService -> LateService (ctor: LateService late), LateService -> phase 'content' (phase barrier), " +
                "phase 'content' -> EarlyService (phase barrier)."));
            Assert.That(error.Message, Does.Contain(
                "(EarlyService in phase 'content' depends on LateService in phase 'session'; a service cannot depend on a later phase)"));
        }

        [Test]
        public void AnInvalidDefaultPhaseIsRejected()
        {
            var options = Options("platform", "content");
            options.DefaultPhase = "nope";
            var container = TestScope.Build(b => b.Add<Unmarked>());

            var error = Assert.Throws<InitGraphException>(() => ScopeRun.Create(container, "session", options));

            Assert.That(error!.Message, Is.EqualTo(
                "RuntimeFlowOptions.DefaultPhase is 'nope', but RuntimeFlowOptions.Phases is [platform, content]."));
        }
    }
}
