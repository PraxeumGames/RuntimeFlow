using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.Tests.Lifecycle
{
    /// <summary>
    /// Restart chains the way a live-ops client produces them: a service finds the content catalog
    /// outdated, downloads it and requests a restart; in the new generation a later service finds the
    /// configs outdated and does the same. Every chain must reach its final generation, whatever the
    /// requesting service does after asking and wherever it lives, and a failed or interrupted chain must
    /// leave the host in a state it can recover from.
    /// </summary>
    [TestFixture]
    public sealed class RestartChainTests
    {
        /// <summary>What a service does right after it requested a restart.</summary>
        public enum Reaction
        {
            /// <summary>The documented pattern: fire the restart, then wait on the own token.</summary>
            ParkOnToken,

            /// <summary>Fire the restart and complete normally.</summary>
            Return,

            /// <summary>Fire the restart and throw <see cref="OperationCanceledException"/> right away.</summary>
            ThrowCancelled,

            /// <summary>Pass the own token to RestartAsync, then wait on it.</summary>
            ParkOnTokenPassingIt
        }

        public enum BuildFailure { Installer, EntryPoint, Graph }

        /// <summary>The "server" state the services react to; lives in the global scope, so it survives restarts.</summary>
        public sealed class World
        {
            public Reaction Addressables { get; set; } = Reaction.Return;
            public Reaction Configs { get; set; } = Reaction.Return;
            public bool AddressablesInGlobal { get; set; }
            public bool AddressablesOutdated { get; set; } = true;
            public bool ConfigsOutdated { get; set; } = true;
            public int SessionBuilds { get; set; }
            public Action<IContainerBuilder, int>? OnSessionBuild { get; set; }
            public Action? OnFirstGenerationCancelled { get; set; }
            public Task<StartupResult>? GlobalRequest { get; set; }
            public List<string> Starts { get; } = new List<string>();
        }

        private static async Task React(Reaction reaction, RuntimeFlowHost host, string reason, CancellationToken token)
        {
            await Task.Yield(); // the download

            switch (reaction)
            {
                case Reaction.ParkOnToken:
                    _ = host.RestartAsync(reason);
                    await Task.Delay(Timeout.Infinite, token);
                    return;
                case Reaction.Return:
                    _ = host.RestartAsync(reason);
                    return;
                case Reaction.ThrowCancelled:
                    _ = host.RestartAsync(reason);
                    throw new OperationCanceledException("restarting");
                case Reaction.ParkOnTokenPassingIt:
                    _ = host.RestartAsync(reason, token);
                    await Task.Delay(Timeout.Infinite, token);
                    return;
            }
        }

        public sealed class AddressablesUpdater : IAsyncInitializable
        {
            private readonly World _world;
            private readonly RuntimeFlowHost _host;

            public AddressablesUpdater(World world, RuntimeFlowHost host)
            {
                _world = world;
                _host = host;
            }

            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _world.Starts.Add($"Addressables:{context.Generation}");
                if (!_world.AddressablesOutdated) return;
                _world.AddressablesOutdated = false;
                await React(_world.Addressables, _host, "addressables-updated", cancellationToken);
            }
        }

        /// <summary>Checks the configs only once the content is current, i.e. after the addressables.</summary>
        public sealed class ConfigUpdater : IAsyncInitializable
        {
            private readonly World _world;
            private readonly RuntimeFlowHost _host;

            public ConfigUpdater(World world, RuntimeFlowHost host, AddressablesUpdater addressables)
            {
                _world = world;
                _host = host;
            }

            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _world.Starts.Add($"Configs:{context.Generation}");
                if (!_world.ConfigsOutdated) return;
                _world.ConfigsOutdated = false;
                await React(_world.Configs, _host, "configs-updated", cancellationToken);
            }
        }

        public sealed class Gameplay : IAsyncInitializable
        {
            private readonly World _world;

            public Gameplay(World world, ConfigUpdater configs) => _world = world;

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _world.Starts.Add($"Gameplay:{context.Generation}");
                return Task.CompletedTask;
            }
        }

        /// <summary>Hands the first generation's token to the test, so it can act in the middle of its teardown.</summary>
        public sealed class TeardownProbe : IAsyncInitializable
        {
            private readonly World _world;

            public TeardownProbe(World world) => _world = world;

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                if (context.Generation == 0) cancellationToken.Register(() => _world.OnFirstGenerationCancelled?.Invoke());
                return Task.CompletedTask;
            }
        }

        /// <summary>Requests a restart synchronously from its first initialization and completes.</summary>
        public sealed class SyncRequester : IAsyncInitializable
        {
            private readonly RuntimeFlowHost _host;

            public SyncRequester(RuntimeFlowHost host) => _host = host;

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                if (context.Generation == 0) _ = _host.RestartAsync("sync");
                return Task.CompletedTask;
            }
        }

        public sealed class DependentOfRequester : IAsyncInitializable
        {
            private readonly World _world;

            public DependentOfRequester(World world, SyncRequester requester) => _world = world;

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _world.Starts.Add($"Dependent:{context.Generation}");
                return Task.CompletedTask;
            }
        }

        /// <summary>A global service that requests a restart and then fails the global run.</summary>
        public sealed class FailingGlobalRequester : IAsyncInitializable
        {
            private readonly World _world;
            private readonly RuntimeFlowHost _host;

            public FailingGlobalRequester(World world, RuntimeFlowHost host)
            {
                _world = world;
                _host = host;
            }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                if (!_world.AddressablesOutdated) return Task.CompletedTask;
                _world.AddressablesOutdated = false;
                _world.GlobalRequest = _host.RestartAsync("addressables-updated");
                throw new InvalidOperationException("catalog corrupted");
            }
        }

        public sealed class ThrowingEntryPoint : IInitializable
        {
            public void Initialize() => throw new InvalidOperationException("entry point exploded");
        }

        [DependsOn(typeof(CycleB))]
        public sealed class CycleA : AutoService { }

        [DependsOn(typeof(CycleA))]
        public sealed class CycleB : AutoService { }

        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

        private CapturingLogger _log = null!;
        private RuntimeFlowOptions _options = null!;
        private World _world = null!;

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _options = TestScope.Options(_log);
            _world = new World();
        }

        private RuntimeFlowHost Host(Action<IContainerBuilder>? global = null) => new RuntimeFlowHost(
            builder =>
            {
                builder.RegisterInstance(_world);
                if (_world.AddressablesInGlobal) builder.Add<AddressablesUpdater>();
                global?.Invoke(builder);
            },
            builder =>
            {
                var build = _world.SessionBuilds++;
                _world.OnSessionBuild?.Invoke(builder, build);
                if (!_world.AddressablesInGlobal) builder.Add<AddressablesUpdater>();
                builder.Add<ConfigUpdater>();
                builder.Add<Gameplay>();
                builder.Add<TeardownProbe>();
            },
            _options);

        /// <summary>Awaits a task of the chain, failing the test (instead of hanging it) when it never settles.</summary>
        private async Task<T> Within<T>(Task<T> task, RuntimeFlowHost host, string what)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Bound));
            Assert.That(winner, Is.SameAs(task),
                $"{what} never settled: state {host.State}, generation {host.Generation}.\n{_log.Dump()}");
            // [assembly: FailOnEscapedCancellation] already fails a test whose task ends Canceled (Unity's
            // runner alone would report it as passed); checking here adds the host state and the log dump.
            Assert.That(task.IsCanceled, Is.False,
                $"{what} was cancelled: state {host.State}, generation {host.Generation}.\n{_log.Dump()}");
            return await task;
        }

        private async Task Settled(Task task, RuntimeFlowHost host, string what)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Bound));
            Assert.That(winner, Is.SameAs(task),
                $"{what} never settled: state {host.State}, generation {host.Generation}.\n{_log.Dump()}");
        }

        [TestCase(Reaction.ParkOnToken)]
        [TestCase(Reaction.Return)]
        [TestCase(Reaction.ThrowCancelled)]
        [TestCase(Reaction.ParkOnTokenPassingIt)]
        [Timeout(15000)]
        public async Task TwoSessionRestartsInARowReachGenerationTwo(Reaction reaction)
        {
            _world.Addressables = reaction;
            _world.Configs = reaction;
            await using var host = Host();

            var result = await Within(host.StartAsync(), host, "the startup");

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed), _log.Dump());
            Assert.That(host.Generation, Is.EqualTo(2));
            Assert.That(host.RestartCount, Is.EqualTo(2));
            Assert.That(host.State, Is.EqualTo(RunState.Completed));
            Assert.That(_world.SessionBuilds, Is.EqualTo(3));
            Assert.That(_world.Starts, Is.EqualTo(new[]
            {
                "Addressables:0",
                "Addressables:1", "Configs:1",
                "Addressables:2", "Configs:2", "Gameplay:2"
            }), "no dependent of a requesting service may start in the generation that is being replaced");
        }

        [TestCase(Reaction.Return, Reaction.ParkOnToken)]
        [TestCase(Reaction.Return, Reaction.Return)]
        [TestCase(Reaction.Return, Reaction.ThrowCancelled)]
        [Timeout(15000)]
        public async Task AChainStartedByAGlobalServiceReachesGenerationTwo(Reaction global, Reaction session)
        {
            _world.AddressablesInGlobal = true;
            _world.Addressables = global;
            _world.Configs = session;
            await using var host = Host();

            var result = await Within(host.StartAsync(), host, "the startup");

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed), _log.Dump());
            Assert.That(host.Generation, Is.EqualTo(2));
            Assert.That(host.RestartCount, Is.EqualTo(2));
            Assert.That(_world.SessionBuilds, Is.EqualTo(2),
                "a restart requested during the global phase builds the first session directly as a restart");
            Assert.That(_world.Starts, Is.EqualTo(new[]
            {
                "Addressables:0",
                "Configs:1",
                "Configs:2", "Gameplay:2"
            }));
            Assert.That(_log.Has(LogLevel.Information,
                "[RuntimeFlow] restart requested: 'addressables-updated' (no session yet: the next session is built as a restart)"),
                Is.True, _log.Dump());
            Assert.That(_log.Has(LogLevel.Warning,
                "[RuntimeFlow] restart 'addressables-updated' requested while the global scope is initializing: " +
                "global services are not rebuilt by RestartAsync"), Is.True, _log.Dump());
        }

        [Test]
        [Timeout(15000)]
        public async Task ARestartRequestedDuringAFailingGlobalPhaseIsDroppedWithTheStartup()
        {
            _world.ConfigsOutdated = false;
            await using var host = new RuntimeFlowHost(
                builder =>
                {
                    builder.RegisterInstance(_world);
                    builder.Add<FailingGlobalRequester>();
                },
                builder =>
                {
                    _world.SessionBuilds++;
                    builder.Add<Gameplay>();
                    builder.Add<ConfigUpdater>();
                    builder.Add<AddressablesUpdater>();
                },
                _options);

            var startup = host.StartAsync();
            var failure = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => Within(startup, host, "the startup"));
            var request = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(
                () => Within(_world.GlobalRequest!, host, "the restart request"));

            Assert.That(failure.Scope, Is.EqualTo("global"));
            Assert.That(request, Is.SameAs(failure), "the dropped request observes the startup failure");
            Assert.That(_world.SessionBuilds, Is.EqualTo(0));
            Assert.That(host.RestartCount, Is.EqualTo(0));

            var retry = await Within(host.StartAsync(), host, "the second startup");

            Assert.That(retry.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(host.Generation, Is.EqualTo(0), "the dropped request never lingers into the retried startup");
            Assert.That(_world.SessionBuilds, Is.EqualTo(1));
        }

        [Test]
        [Timeout(15000)]
        public async Task ADependentOfAServiceThatRequestedARestartNeverStartsInItsGeneration()
        {
            await using var host = new RuntimeFlowHost(
                builder => builder.RegisterInstance(_world),
                builder =>
                {
                    builder.Add<SyncRequester>();
                    builder.Add<DependentOfRequester>();
                },
                _options);

            var result = await Within(host.StartAsync(), host, "the startup");

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(host.Generation, Is.EqualTo(1));
            Assert.That(_world.Starts, Is.EqualTo(new[] { "Dependent:1" }));
        }

        [TestCase(BuildFailure.Installer)]
        [TestCase(BuildFailure.EntryPoint)]
        [TestCase(BuildFailure.Graph)]
        [Timeout(15000)]
        public async Task AFailedRebuildLeavesTheHostRestartable(BuildFailure kind)
        {
            _world.AddressablesOutdated = false;
            _world.ConfigsOutdated = false;
            _world.OnSessionBuild = (builder, build) =>
            {
                if (build == 1) Break(builder, kind);
            };
            await using var host = Host();
            await Within(host.StartAsync(), host, "the startup");

            var failure = await AsyncTestAssert.ThrowsAsync<Exception>(
                () => Within(host.RestartAsync("bundles-updated"), host, "the failing restart"));

            Assert.That(failure, Is.TypeOf(kind == BuildFailure.Installer ? typeof(InvalidOperationException)
                : kind == BuildFailure.EntryPoint ? typeof(RuntimeFlowException)
                : typeof(InitGraphException)), failure.ToString());
            Assert.That(host.State, Is.EqualTo(RunState.Failed));
            Assert.That(host.GetStatus().State, Is.EqualTo(RunState.Failed));
            Assert.That(host.GetStatus().Error, Is.SameAs(failure));
            var missing = Assert.Throws<InvalidOperationException>(() => _ = host.Session);
            Assert.That(missing!.Message, Does.Contain("RestartAsync"));
            Assert.That(missing.InnerException, Is.SameAs(failure));

            var retry = await Within(host.RestartAsync("retry"), host, "the retried restart");

            Assert.That(retry.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(host.State, Is.EqualTo(RunState.Completed));
            Assert.That(host.GetStatus().Error, Is.Null);
            Assert.That(host.Generation, Is.EqualTo(2));
            Assert.That(host.RestartCount, Is.EqualTo(2));
            Assert.That(_world.SessionBuilds, Is.EqualTo(3));
            Assert.That(host.Session.Resolve<Gameplay>(), Is.Not.Null);
        }

        private static void Break(IContainerBuilder builder, BuildFailure kind)
        {
            switch (kind)
            {
                case BuildFailure.Installer:
                    throw new InvalidOperationException("installer exploded");
                case BuildFailure.EntryPoint:
                    builder.RegisterEntryPoint<ThrowingEntryPoint>();
                    break;
                case BuildFailure.Graph:
                    builder.Add<CycleA>();
                    builder.Add<CycleB>();
                    break;
            }
        }

        [Test]
        [Timeout(15000)]
        public async Task AFailedFirstSessionBuildCanBeRetriedWithARestart()
        {
            _world.AddressablesOutdated = false;
            _world.ConfigsOutdated = false;
            _world.OnSessionBuild = (builder, build) =>
            {
                if (build == 0) throw new InvalidOperationException("installer exploded");
            };
            await using var host = Host();

            await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(() => Within(host.StartAsync(), host, "the startup"));
            Assert.That(host.State, Is.EqualTo(RunState.Failed));

            var retry = await Within(host.RestartAsync("retry"), host, "the retry");

            Assert.That(retry.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(host.Generation, Is.EqualTo(1));
            Assert.That(_world.Starts, Is.EqualTo(new[] { "Addressables:1", "Configs:1", "Gameplay:1" }));
        }

        [Test]
        [Timeout(15000)]
        public async Task ARestartWithTheRequestersOwnTokenIsNotCancelledByItsOwnTeardown()
        {
            _world.Addressables = Reaction.ParkOnTokenPassingIt;
            _world.ConfigsOutdated = false;
            await using var host = Host();

            var result = await Within(host.StartAsync(), host, "the startup");

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(host.Generation, Is.EqualTo(1));
            Assert.That(host.State, Is.EqualTo(RunState.Completed));
            Assert.That(_log.Has(LogLevel.Warning,
                "[RuntimeFlow] restart 'addressables-updated': the CancellationToken passed to RestartAsync belongs to " +
                "the run this restart tears down; it is ignored"), Is.True, _log.Dump());
        }

        [Test]
        [Timeout(15000)]
        public async Task AnAlreadyCancelledTokenCancelsTheRequestWithoutTearingAnythingDown()
        {
            _world.AddressablesOutdated = false;
            _world.ConfigsOutdated = false;
            await using var host = Host();
            await Within(host.StartAsync(), host, "the startup");
            var session = host.Session;

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var request = host.RestartAsync("too-late", cancelled.Token);
            await Task.Yield();
            await Task.Yield();

            Assert.That(request.IsCanceled, Is.True);
            Assert.That(host.Session, Is.SameAs(session));
            Assert.That(host.Generation, Is.EqualTo(0));
            Assert.That(host.RestartCount, Is.EqualTo(0));
            Assert.That(host.State, Is.EqualTo(RunState.Completed));

            var next = await Within(host.RestartAsync("next"), host, "a later restart");
            Assert.That(next.Outcome, Is.EqualTo(StartupOutcome.Completed), "the cancelled request is not budgeted or pending");
            Assert.That(host.Generation, Is.EqualTo(1));
        }

        [Test]
        [Timeout(15000)]
        public async Task DisposingTheHostDuringARestartBuildsNoNewSession()
        {
            _world.AddressablesOutdated = false;
            _world.ConfigsOutdated = false;
            var host = Host();
            try
            {
                await Within(host.StartAsync(), host, "the startup");
                Task? disposal = null;
                _world.OnFirstGenerationCancelled = () => disposal = host.DisposeAsync().AsTask();

                var restart = host.RestartAsync("bundles-updated");
                await Settled(restart, host, "the restart");

                Assert.That(disposal, Is.Not.Null, "the teardown of generation 0 disposed the host");
                await Settled(disposal!, host, "the disposal");
                Assert.That(restart.IsCanceled || restart.Exception?.InnerException is ObjectDisposedException
                            || restart.Exception?.InnerException is OperationCanceledException,
                    Is.True, restart.Exception?.ToString());
                Assert.That(_world.SessionBuilds, Is.EqualTo(1), "no session may be built on a disposed host");
                Assert.That(host.State, Is.EqualTo(RunState.Disposed));
            }
            finally
            {
                await host.DisposeAsync();
            }
        }

        [Test]
        [Timeout(15000)]
        public async Task QuittingDuringARestartBuildsNoNewSession()
        {
            _world.AddressablesOutdated = false;
            _world.ConfigsOutdated = false;
            await using var host = Host();
            await Within(host.StartAsync(), host, "the startup");
            _world.OnFirstGenerationCancelled = () => host.OnQuitting();

            var restart = host.RestartAsync("bundles-updated");
            await Settled(restart, host, "the restart");

            Assert.That(host.IsQuitting, Is.True);
            await AsyncTestAssert.ThrowsAsync<OperationCanceledException>(() => restart);
            Assert.That(_world.SessionBuilds, Is.EqualTo(1), "no session may be built into a quitting player");
        }
    }
}
