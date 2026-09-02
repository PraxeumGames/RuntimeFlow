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
    }
}
