using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Lifecycle
{
    /// <summary>
    /// Scopes created below the host (navigation screens, feature scopes) run their own graph: only
    /// their services are scheduled, the parents are externals, and their teardown happens first.
    /// </summary>
    [TestFixture]
    public sealed class ScopesTests
    {
        public sealed class Recorder
        {
            public List<string> Initialized { get; } = new List<string>();
            public List<string> Disposed { get; } = new List<string>();
        }

        public sealed class SessionService : IAsyncInitializable, IAsyncDisposable
        {
            private readonly Recorder _recorder;

            public SessionService(Recorder recorder) => _recorder = recorder;

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _recorder.Initialized.Add(nameof(SessionService));
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                _recorder.Disposed.Add(nameof(SessionService));
                return new ValueTask();
            }
        }

        public sealed class ScreenService : IAsyncInitializable, IAsyncDisposable
        {
            private readonly Recorder _recorder;

            public ScreenService(Recorder recorder, SessionService session)
            {
                _recorder = recorder;
                Session = session;
            }

            public SessionService Session { get; }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _recorder.Initialized.Add($"{nameof(ScreenService)}:{context.Scope}");
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                _recorder.Disposed.Add(nameof(ScreenService));
                return new ValueTask();
            }
        }

        private CapturingLogger _log = null!;
        private RuntimeFlowOptions _options = null!;
        private Recorder _recorder = null!;

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _options = TestScope.Options(_log);
            _recorder = new Recorder();
        }

        private RuntimeFlowHost Host() => new RuntimeFlowHost(
            builder => builder.RegisterInstance(_recorder),
            builder => builder.Add<SessionService>(),
            _options);

        [Test]
        [Timeout(10000)]
        public async Task AChildScopeInitializesOnlyItsOwnServicesAndSeesTheParentsAsExternals()
        {
            await using var host = Host();
            await host.StartAsync();

            var screen = host.Session.CreateScope(builder => builder.Add<ScreenService>());
            var run = await host.InitializeScopeAsync(screen, "lobby");

            var status = run.GetStatus();
            Assert.That(status.Services, Has.Count.EqualTo(1));
            Assert.That(status.Services[0].Name, Is.EqualTo(nameof(ScreenService)));
            Assert.That(status.Services[0].Dependencies, Does.Contain(nameof(SessionService)),
                "the parent service is an ordering edge, not a scheduled node");
            Assert.That(_recorder.Initialized,
                Is.EqualTo(new[] { nameof(SessionService), $"{nameof(ScreenService)}:lobby" }));
            Assert.That(run.Describe(), Does.Contain("external (from parent scopes)"));
            Assert.That(screen.Resolve<ScreenService>().Session, Is.SameAs(host.Session.Resolve<SessionService>()));
        }

        [Test]
        [Timeout(10000)]
        public async Task AScopeFromAnotherContainerIsRejected()
        {
            await using var host = Host();
            await host.StartAsync();

            var foreign = TestScope.Build(builder => builder.RegisterInstance(_recorder));
            var foreignScope = foreign.CreateScope(builder => builder.Add<SessionService>());

            var failure = await AsyncTestAssert.ThrowsAsync<InitGraphException>(
                () => host.InitializeScopeAsync(foreignScope, "foreign"));

            Assert.That(failure.Scope, Is.EqualTo("foreign"));
            Assert.That(failure.Message, Does.Contain("does not belong to this host"));
            foreign.Dispose();
        }

        [Test]
        [Timeout(10000)]
        public async Task ChildRunsAreTornDownBeforeTheSessionOnRestart()
        {
            await using var host = Host();
            await host.StartAsync();

            var screen = host.Session.CreateScope(builder => builder.Add<ScreenService>());
            await host.InitializeScopeAsync(screen, "lobby");

            await host.RestartAsync("bundles-updated");

            Assert.That(_recorder.Disposed, Is.EqualTo(new[] { nameof(ScreenService), nameof(SessionService) }));
        }

        [Test]
        [Timeout(10000)]
        public async Task ChildRunsAreTornDownBeforeTheSessionOnDispose()
        {
            var host = Host();
            await host.StartAsync();

            var screen = host.Session.CreateScope(builder => builder.Add<ScreenService>());
            var run = await host.InitializeScopeAsync(screen, "lobby");

            await host.DisposeAsync();

            Assert.That(_recorder.Disposed, Is.EqualTo(new[] { nameof(ScreenService), nameof(SessionService) }));
            Assert.That(run.State, Is.EqualTo(RunState.Disposed));
            Assert.That(host.State, Is.EqualTo(RunState.Disposed));
        }

        [Test]
        [Timeout(10000)]
        public async Task AScopeCreatedUnderGlobalIsAcceptedToo()
        {
            await using var host = Host();
            await host.StartAsync();

            var tools = host.Global.CreateScope(builder => builder.Add<SessionService>());
            var run = await host.InitializeScopeAsync(tools, "tools");

            Assert.That(run.State, Is.EqualTo(RunState.Completed));
            Assert.That(_recorder.Initialized, Has.Count.EqualTo(2));
        }
    }
}
