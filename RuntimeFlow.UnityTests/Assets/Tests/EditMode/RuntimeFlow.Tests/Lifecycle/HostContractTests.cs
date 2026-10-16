using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Internal;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.Tests.Lifecycle
{
    /// <summary>
    /// The host's contracts around its edges: entry-point exceptions after build, teardown that never
    /// aborts, child scopes (lineage, preconditions, lifetime), externally owned globals, the restart
    /// budget and the state reported mid-restart.
    /// </summary>
    [TestFixture]
    public sealed class HostContractTests
    {
        public sealed class Journal
        {
            public List<string> Entries { get; } = new List<string>();
            public List<Exception> Handled { get; } = new List<Exception>();
            public RuntimeFlowHost? Host { get; set; }
            public Func<Task>? DuringInitialize { get; set; }
            public Exception? Refusal { get; set; }
            public RunState? StateDuringTeardown { get; set; }
        }

        public sealed class SessionService : IAsyncInitializable, IAsyncDisposable
        {
            private readonly Journal _journal;

            public SessionService(Journal journal) => _journal = journal;

            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _journal.Entries.Add($"session:{context.Generation}");
                if (_journal.DuringInitialize != null) await _journal.DuringInitialize();
            }

            public ValueTask DisposeAsync()
            {
                if (_journal.Host != null) _journal.StateDuringTeardown = _journal.Host.State;
                return default;
            }
        }

        public sealed class ThrowsOnDispose : IDisposable
        {
            public void Dispose() => throw new InvalidOperationException("dispose exploded");
        }

        public sealed class DisposeRecorder : IDisposable
        {
            private readonly Journal _journal;

            public DisposeRecorder(Journal journal) => _journal = journal;

            public void Dispose() => _journal.Entries.Add("recorder-disposed");
        }

        public sealed class LobbyService : AutoService { }

        [DependsOn(typeof(LobbyService))]
        public sealed class MatchService : AutoService { }

        public sealed class ToolService : AutoService { }

        public sealed class GlobalAsyncDisposable : IAsyncInitializable, IAsyncDisposable
        {
            public int Disposed { get; private set; }
            public bool Fail { get; set; }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
                => Fail ? Task.FromException(new InvalidOperationException("global exploded")) : Task.CompletedTask;

            public ValueTask DisposeAsync()
            {
                Disposed++;
                return default;
            }
        }

        public sealed class Helper
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            public static void Explode() => throw new System.IO.IOException("disk gone");
        }

        public sealed class EntryPointCallingAHelper : IInitializable
        {
            public void Initialize() => Helper.Explode();
        }

        public sealed class ThrowingEntryPoint : IInitializable
        {
            public void Initialize() => throw new InvalidOperationException("entry point exploded");
        }

        private CapturingLogger _log = null!;
        private RuntimeFlowOptions _options = null!;
        private Journal _journal = null!;

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _options = TestScope.Options(_log);
            _journal = new Journal();
        }

        private RuntimeFlowHost Host(Action<IContainerBuilder>? session = null, Action<IContainerBuilder>? global = null)
        {
            var host = new RuntimeFlowHost(
                builder =>
                {
                    builder.RegisterInstance(_journal);
                    global?.Invoke(builder);
                },
                builder =>
                {
                    builder.Add<SessionService>();
                    session?.Invoke(builder);
                },
                _options);
            _journal.Host = host;
            return host;
        }

        private static IEnumerable<Registration> Handlers(IObjectResolver scope)
        {
            var type = typeof(EntryPointsBuilder).Assembly.GetType("VContainer.Unity.EntryPointExceptionHandler")!;
            if (scope.TryGetRegistration(type, out var registration) && registration != null) yield return registration;
        }

        [Test]
        [Timeout(10000)]
        public async Task AnEntryPointExceptionAfterBuildIsLoggedAndNotCollectedIntoTheNextBuild()
        {
            await using var host = Host();
            await host.StartAsync();

            // What VContainer does when an ITickable or IStartable throws on the player loop.
            var handlerType = typeof(EntryPointsBuilder).Assembly.GetType("VContainer.Unity.EntryPointExceptionHandler")!;
            var handler = host.Global.Resolve(handlerType);
            handlerType.GetMethod("Publish")!.Invoke(handler, new object[] { new InvalidOperationException("tick exploded") });

            Assert.That(_log.Has(LogLevel.Error, "threw InvalidOperationException in a VContainer entry point after its scope was built: tick exploded"),
                Is.True, _log.Dump());

            var restart = await host.RestartAsync("after-tick");
            Assert.That(restart.Outcome, Is.EqualTo(StartupOutcome.Completed), "a player-loop exception must not fail the next session build");
            Assert.That(Handlers(host.Session).Count(), Is.EqualTo(1),
                "the session has its own collector, registered before the installer, so VContainer 1.19 adds no default handler");
        }

        [Test]
        [Timeout(10000)]
        public async Task AConsumerHandlerInTheGlobalInstallerAlsoReceivesTheSessionsEntryPointExceptions()
        {
            await using var host = Host(
                session: builder => builder.RegisterEntryPoint<ThrowingEntryPoint>(),
                global: builder => builder.RegisterEntryPointExceptionHandler(e => _journal.Handled.Add(e)));

            var result = await host.StartAsync();

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(_journal.Handled.Select(e => e.Message), Is.EqualTo(new[] { "entry point exploded" }));
        }

        [Test]
        [Timeout(10000)]
        public async Task TheEntryPointThatThrewIsNamedEvenWhenAHelperThrewForIt()
        {
            await using var host = Host(session: builder => builder.RegisterEntryPoint<EntryPointCallingAHelper>());

            var failure = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => host.StartAsync());

            Assert.That(failure.Service, Is.EqualTo(nameof(EntryPointCallingAHelper)), failure.Message);
            Assert.That(failure.Message, Does.Contain("EntryPointCallingAHelper threw IOException"));
        }

        [Test]
        [Timeout(10000)]
        public async Task AThrowingDisposeNeitherWedgesARestartNorAbortsDisposal()
        {
            var host = Host(session: builder =>
            {
                builder.Register<ThrowsOnDispose>(Lifetime.Singleton);
                builder.Register<DisposeRecorder>(Lifetime.Singleton);
                builder.RegisterBuildCallback(scope =>
                {
                    scope.Resolve<DisposeRecorder>();
                    scope.Resolve<ThrowsOnDispose>();
                });
            });
            await host.StartAsync();

            var restart = await host.RestartAsync("after-dispose-failure");

            Assert.That(restart.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(_journal.Entries, Does.Contain("recorder-disposed"), "the remaining disposables still ran");
            Assert.That(_log.Has(LogLevel.Error, "session: disposing ThrowsOnDispose synchronously threw InvalidOperationException"), Is.True, _log.Dump());

            await host.DisposeAsync();
            Assert.That(host.State, Is.EqualTo(RunState.Disposed));
            Assert.That(FlowRegistry.Live, Has.No.Member(host));
        }

        [Test]
        [Timeout(10000)]
        public async Task AChildScopeCannotStartWhileItsParentIsStillInitializing()
        {
            await using var host = Host();
            _journal.DuringInitialize = async () =>
            {
                _journal.DuringInitialize = null;
                try
                {
                    await host.InitializeScopeAsync(host.Session.CreateScope(b => b.Add<LobbyService>()), "lobby");
                }
                catch (Exception exception)
                {
                    _journal.Refusal = exception;
                }
            };

            await host.StartAsync();

            Assert.That(_journal.Refusal, Is.TypeOf<InvalidOperationException>());
            Assert.That(_journal.Refusal!.Message, Does.Contain("cannot be initialized"));
        }

        [Test]
        [Timeout(10000)]
        public async Task TheHostsOwnScopesAndAScopeInitializedTwiceAreRejected()
        {
            await using var host = Host();
            await host.StartAsync();

            await AsyncTestAssert.ThrowsAsync<InitGraphException>(() => host.InitializeScopeAsync(host.Session, "session-again"));

            var lobby = host.Session.CreateScope(b => b.Add<LobbyService>());
            await host.InitializeScopeAsync(lobby, "lobby");
            var twice = await AsyncTestAssert.ThrowsAsync<InitGraphException>(() => host.InitializeScopeAsync(lobby, "lobby-again"));
            Assert.That(twice.Message, Does.Contain("has already been initialized"));
        }

        [Test]
        [Timeout(10000)]
        public async Task AScopeBelowASessionThatWasRestartedAwayIsRejected()
        {
            await using var host = Host();
            await host.StartAsync();
            var stale = host.Session;
            await host.RestartAsync("replace");

            var orphan = stale.CreateScope(b => b.Add<LobbyService>());
            var failure = await AsyncTestAssert.ThrowsAsync<InitGraphException>(() => host.InitializeScopeAsync(orphan, "orphan"));

            Assert.That(failure.Message, Does.Contain("a restart has replaced"));
        }

        [Test]
        [Timeout(10000)]
        public async Task AGrandchildScopeSeesItsParentChildRun()
        {
            await using var host = Host();
            await host.StartAsync();

            var lobby = host.Session.CreateScope(b => b.Add<LobbyService>());
            await host.InitializeScopeAsync(lobby, "lobby");
            var match = lobby.CreateScope(b => b.Add<MatchService>());
            var run = await host.InitializeScopeAsync(match, "match");

            Assert.That(run.State, Is.EqualTo(RunState.Completed));
            Assert.That(run.GetStatus().Service("MatchService").Dependencies, Is.EqualTo(new[] { "LobbyService" }));
            Assert.That(run.Describe(), Does.Contain("LobbyService [lobby, initialized]"), run.Describe());
        }

        [Test]
        [Timeout(10000)]
        public async Task ADisposedChildRunIsForgottenAndGlobalChildrenSurviveRestarts()
        {
            await using var host = Host();
            await host.StartAsync();

            var lobby = await host.InitializeScopeAsync(host.Session.CreateScope(b => b.Add<LobbyService>()), "lobby");
            var tools = await host.InitializeScopeAsync(host.Global.CreateScope(b => b.Add<ToolService>()), "tools");
            await lobby.DisposeAsync();

            Assert.That(host.ChildRuns, Is.EqualTo(new[] { tools }));

            await host.RestartAsync("keep-tools");

            Assert.That(tools.State, Is.EqualTo(RunState.Completed), "a global child does not depend on the session");
            Assert.That(host.ChildRuns, Is.EqualTo(new[] { tools }));
        }

        [Test]
        [Timeout(10000)]
        public async Task AnExternallyOwnedGlobalKeepsItsServicesAndCannotBeRetriedAfterAFailure()
        {
            var service = new GlobalAsyncDisposable();
            var global = TestScope.Build(b => b.RegisterInstance(service).As<IAsyncInitializable>());
            try
            {
                var host = RuntimeFlowHost.From(global, b => b.Add<ToolService>(), _options);
                await host.StartAsync();
                await host.DisposeAsync();
                Assert.That(service.Disposed, Is.EqualTo(0), "the host never disposes services of a container it does not own");

                service.Fail = true;
                var failing = RuntimeFlowHost.From(global, b => b.Add<ToolService>(), _options);
                await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => failing.StartAsync());
                var retry = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(() => failing.StartAsync());
                Assert.That(retry.Message, Does.Contain("cannot be initialized twice"));
                await failing.DisposeAsync();
                Assert.That(service.Disposed, Is.EqualTo(0));
            }
            finally
            {
                global.Dispose();
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task RefusedRestartsDoNotConsumeTheBudget()
        {
            _options.MaxRestartsPerWindow = 2;
            await using var host = Host();
            await host.StartAsync();
            await host.RestartAsync("first");
            await host.RestartAsync("second");

            await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => host.RestartAsync("third"));
            var fourth = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => host.RestartAsync("fourth"));

            Assert.That(fourth.Message, Is.EqualTo(
                "Restart budget exceeded: 3 restarts within 60s (limit 2). Reasons: first, second, fourth"));
        }

        [Test]
        [Timeout(10000)]
        public async Task AWindowOfZeroCountsRestartsOverTheHostsLifetime()
        {
            _options.MaxRestartsPerWindow = 1;
            _options.RestartWindow = TimeSpan.Zero;
            await using var host = Host();
            await host.StartAsync();
            await host.RestartAsync("first");

            var refused = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => host.RestartAsync("second"));

            Assert.That(refused.Message, Is.EqualTo(
                "Restart budget exceeded: 2 restarts over the host's lifetime (limit 1). Reasons: first, second"));
        }

        [Test]
        [Timeout(10000)]
        public async Task TheHostReportsRunningWhileARestartTearsTheOldGenerationDown()
        {
            await using var host = Host();
            await host.StartAsync();

            await host.RestartAsync("observe");

            Assert.That(_journal.StateDuringTeardown, Is.EqualTo(RunState.Running));
        }
    }
}
