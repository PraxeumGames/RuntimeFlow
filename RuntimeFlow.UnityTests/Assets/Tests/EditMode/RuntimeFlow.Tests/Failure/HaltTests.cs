using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Failure
{
    /// <summary>Halt stops startup on purpose: no exception, in-flight work cancelled, first call wins.</summary>
    [TestFixture]
    public sealed class HaltTests
    {
        public sealed class UserUpdateCheck : IAsyncInitializable
        {
            public string Reason { get; set; } = "user-update.required";

            public int Halts { get; set; } = 1;

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                for (var i = 0; i < Halts; i++) context.Halt(Reason);
                return Task.CompletedTask;
            }
        }

        public sealed class Busy : ControlledService { }

        public sealed class Never : ControlledService { }

        private CapturingLogger _log = null!;
        private CollectingObserver _observer = null!;
        private RuntimeFlowOptions _options = null!;

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _observer = new CollectingObserver();
            _options = TestScope.Options(_log, _observer);
        }

        [Test]
        [Timeout(10000)]
        public async Task HaltCompletesTheRunWithoutThrowing()
        {
            var container = TestScope.Build(b =>
            {
                b.RegisterInstance(new UserUpdateCheck()).AsImplementedInterfaces();
                b.Add<Never>();
            });
            var run = ScopeRun.Create(container, "session", _options);

            var result = await run.RunAsync();

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Halted));
            Assert.That(result.HaltReason, Is.EqualTo("user-update.required"));
            Assert.That(result.HaltedBy, Is.EqualTo("UserUpdateCheck"));
            Assert.That(run.State, Is.EqualTo(RunState.Halted));
            Assert.That(run.GetStatus().Service("Never").State, Is.EqualTo(ServiceState.Skipped));
            Assert.That(_observer.Contains("run-halted:session:UserUpdateCheck"), Is.True);
        }

        [Test]
        [Timeout(10000)]
        public async Task HaltCancelsWorkThatIsAlreadyInFlight()
        {
            var container = TestScope.Build(b =>
            {
                b.Add<Busy>();
                b.RegisterInstance(new UserUpdateCheck()).AsImplementedInterfaces();
            });
            var busy = container.Resolve<Busy>();
            var run = ScopeRun.Create(container, "session", _options);

            var result = await run.RunAsync();

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Halted));
            Assert.That(busy.Token.IsCancellationRequested, Is.True);
            Assert.That(run.GetStatus().Service("Busy").State, Is.EqualTo(ServiceState.Cancelled));
        }

        [Test]
        [Timeout(10000)]
        public async Task TheFirstHaltWins()
        {
            var check = new UserUpdateCheck { Halts = 3, Reason = "first" };
            var container = TestScope.Build(b => b.RegisterInstance(check).AsImplementedInterfaces());
            var run = ScopeRun.Create(container, "session", _options);

            var result = await run.RunAsync();

            Assert.That(result.HaltReason, Is.EqualTo("first"));
            Assert.That(_log.Messages(LogLevel.Warning).Count(m => m.Contains("ignored; the run is no longer accepting one")),
                Is.EqualTo(2), _log.Dump());
        }

        [Test]
        [Timeout(10000)]
        public async Task TheHaltIsLoggedWithWhatWasCompletedAndCancelled()
        {
            var container = TestScope.Build(b =>
            {
                b.Add<Busy>();
                b.RegisterInstance(new UserUpdateCheck()).AsImplementedInterfaces();
                b.Add<Never>();
            });
            var run = ScopeRun.Create(container, "session", _options);

            await run.RunAsync();

            var message = _log.Find(LogLevel.Information, "halted by");
            Assert.That(message, Is.Not.Null, _log.Dump());
            Assert.That(message, Does.StartWith("[RuntimeFlow] session: halted by UserUpdateCheck — 'user-update.required' after "));
            Assert.That(message, Does.EndWith("(1/3 services completed; cancelled: Busy)."));
        }

        [Test]
        [Timeout(10000)]
        public async Task TheScopeStaysUsableAfterAHalt()
        {
            var container = TestScope.Build(b => b.RegisterInstance(new UserUpdateCheck()).AsImplementedInterfaces());
            var run = ScopeRun.Create(container, "session", _options);

            await run.RunAsync();

            Assert.That(container.Resolve<IAsyncInitializable>(), Is.Not.Null);
            await run.DisposeAsync();
            Assert.That(run.State, Is.EqualTo(RunState.Disposed));
        }
    }
}
