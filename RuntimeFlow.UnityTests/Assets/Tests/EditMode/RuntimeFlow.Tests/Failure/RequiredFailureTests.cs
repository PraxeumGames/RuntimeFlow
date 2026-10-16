using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Failure
{
    /// <summary>A required failure stops the run, collects every error and reports what was left unfinished.</summary>
    [TestFixture]
    public sealed class RequiredFailureTests
    {
        public sealed class Alpha : AutoService { }

        public sealed class Boom : ControlledService { }

        public sealed class AlsoBoom : ControlledService { }

        public sealed class Sibling : ControlledService { }

        public sealed class Dependent : ControlledService
        {
            public Dependent(Boom boom) => Boom = boom;

            public Boom Boom { get; }
        }

        public sealed class Done1 : AutoService { }
        public sealed class Done2 : AutoService { }
        public sealed class Done3 : AutoService { }
        public sealed class Done4 : AutoService { }
        public sealed class Done5 : AutoService { }
        public sealed class Done6 : AutoService { }
        public sealed class Done7 : AutoService { }

        public sealed class Wait1 : AutoService { public Wait1(Boom b) { } }
        public sealed class Wait2 : AutoService { public Wait2(Boom b) { } }
        public sealed class Wait3 : AutoService { public Wait3(Boom b) { } }
        public sealed class Wait4 : AutoService { public Wait4(Boom b) { } }
        public sealed class Wait5 : AutoService { public Wait5(Boom b) { } }
        public sealed class Wait6 : AutoService { public Wait6(Boom b) { } }

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
        public async Task ASingleFailureCarriesTheOriginalAsInnerException()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<Alpha>();
                b.Add<Boom>();
                b.Add<Sibling>();
                b.Add<Dependent>();
            });
            var boom = container.Resolve<Boom>();
            var run = _tracker.Create(container, "session", _options);

            var running = run.RunAsync();
            await boom.Started;
            var original = new InvalidOperationException("catalog is down");
            boom.Fail(original);

            var error = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => running);

            Assert.That(error.InnerException, Is.SameAs(original));
            Assert.That(error.Scope, Is.EqualTo("session"));
            Assert.That(error.Service, Is.EqualTo("Boom"));
            Assert.That(error.Completed, Is.EqualTo(new[] { "Alpha" }));
            Assert.That(error.Failures.Select(f => f.Service), Is.EqualTo(new[] { "Boom" }));
            Assert.That(error.Message, Does.StartWith(
                "Initialization of scope 'session' failed: Boom threw InvalidOperationException after "));
            Assert.That(error.Message, Does.Contain("Completed (1): Alpha; unfinished (2): "));
            Assert.That(error.Message, Does.Contain("Dependent (blocked on Boom)"));
            Assert.That(error.Message, Does.EndWith("See InnerException."));
        }

        [Test]
        [Timeout(10000)]
        public async Task SiblingsAreCancelledAndDependentsSkipped()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<Boom>();
                b.Add<Sibling>();
                b.Add<Dependent>();
            });
            var boom = container.Resolve<Boom>();
            var sibling = container.Resolve<Sibling>();
            var run = _tracker.Create(container, "session", _options);

            var running = run.RunAsync();
            await boom.Started;
            await sibling.Started;
            boom.Fail(new InvalidOperationException("down"));

            await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => running);

            var status = run.GetStatus();
            Assert.That(status.State, Is.EqualTo(RunState.Failed));
            Assert.That(status.Service("Boom").State, Is.EqualTo(ServiceState.Failed));
            Assert.That(status.Service("Sibling").State, Is.EqualTo(ServiceState.Cancelled));
            Assert.That(status.Service("Dependent").State, Is.EqualTo(ServiceState.Skipped));
            Assert.That(status.Service("Dependent").WaitingOn, Is.EqualTo(new[] { "Boom" }));
            Assert.That(_observer.Contains("failed:session:Boom"), Is.True);
            Assert.That(_observer.Contains("run-failed:session:Boom"), Is.True);
        }

        [Test]
        [Timeout(10000)]
        public async Task EveryFailureIsCollectedIntoAnAggregateException()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<Boom>();
                b.Add<AlsoBoom>();
            });
            var boom = container.Resolve<Boom>();
            var alsoBoom = container.Resolve<AlsoBoom>();
            var run = _tracker.Create(container, "session", _options);

            var running = run.RunAsync();
            await boom.Started;
            await alsoBoom.Started;
            var first = new InvalidOperationException("first");
            var second = new NotSupportedException("second");
            boom.Fail(first);
            alsoBoom.Fail(second);

            var error = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => running);

            Assert.That(error.Failures.Select(f => f.Service), Is.EqualTo(new[] { "Boom", "AlsoBoom" }));
            var aggregate = error.InnerException as AggregateException;
            Assert.That(aggregate, Is.Not.Null, error.InnerException?.ToString());
            Assert.That(aggregate!.InnerExceptions, Is.EqualTo(new Exception[] { first, second }));
            Assert.That(error.Message, Does.StartWith("Initialization of scope 'session' failed: 2 services failed — Boom (InvalidOperationException after "));
            Assert.That(error.Message, Does.Contain("AlsoBoom (NotSupportedException after "));
            Assert.That(error.Message, Does.EndWith("InnerException is an AggregateException with the original exceptions."));
        }

        [Test]
        [Timeout(10000)]
        public async Task TheFailureIsAlsoLoggedAtErrorLevel()
        {
            var container = _tracker.Build(b => b.Add<Boom>());
            var boom = container.Resolve<Boom>();
            var run = _tracker.Create(container, "session", _options);

            var running = run.RunAsync();
            await boom.Started;
            boom.Fail(new InvalidOperationException("down"));
            await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => running);

            Assert.That(_log.Has(LogLevel.Error, "Initialization of scope 'session' failed: Boom threw InvalidOperationException"),
                Is.True, _log.Dump());
        }

        [Test]
        [Timeout(10000)]
        public async Task CompletedAndUnfinishedListsInTheFailureMessageAreCappedAtFiveItems()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<Done1>();
                b.Add<Done2>();
                b.Add<Done3>();
                b.Add<Done4>();
                b.Add<Done5>();
                b.Add<Done6>();
                b.Add<Done7>();
                b.Add<Boom>();
                b.Add<Wait1>();
                b.Add<Wait2>();
                b.Add<Wait3>();
                b.Add<Wait4>();
                b.Add<Wait5>();
                b.Add<Wait6>();
            });
            var boom = container.Resolve<Boom>();
            var run = _tracker.Create(container, "session", _options);

            var running = run.RunAsync();
            await boom.Started;
            boom.Fail(new InvalidOperationException("down"));

            var error = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => running);

            Assert.That(error.Completed, Has.Count.EqualTo(7));
            Assert.That(error.Unfinished, Has.Count.EqualTo(6));
            Assert.That(error.Message, Does.Contain(
                "Completed (7): Done1, Done2, Done3, Done4, Done5 and 2 more; " +
                "unfinished (6): Wait1 (blocked on Boom), Wait2 (blocked on Boom), Wait3 (blocked on Boom), " +
                "Wait4 (blocked on Boom), Wait5 (blocked on Boom) and 1 more. See InnerException."));
        }
    }
}
