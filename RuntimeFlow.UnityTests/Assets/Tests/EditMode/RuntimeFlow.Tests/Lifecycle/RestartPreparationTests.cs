using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Internal;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Lifecycle
{
    /// <summary>
    /// <see cref="IRestartPreparation"/>: the hooks a game uses to put a loading cover up, reset its static
    /// service locators and tear its UI down before a restart replaces the session. They must run once per
    /// restart, on a session whose services no longer initialize but are not disposed yet, whatever state
    /// the session was in, and nothing they do may stop the restart.
    /// </summary>
    [TestFixture]
    public sealed class RestartPreparationTests
    {
        /// <summary>Shared record of what happened, in order; lives in the global scope.</summary>
        public sealed class Journal
        {
            public List<string> Log { get; } = new List<string>();
            public List<Probe> Probes { get; } = new List<Probe>();
            public bool ParkFirstGeneration { get; set; }
            public int FailGeneration { get; set; } = -1;
            public bool RequestFromConstructor { get; set; }
            public bool ExplodeHookConstructor { get; set; }
            public int SessionBuilds { get; set; }
            public int TransientHooksConstructed { get; set; }
            public int ScopedHooksConstructed { get; set; }
            public List<string> HookCalls { get; } = new List<string>();
            public Task<StartupResult>? ConstructorRequest { get; set; }
            public TaskCompletionSource<bool> FirstStarted { get; } = new TaskCompletionSource<bool>();
            public TaskCompletionSource<bool> HookEntered { get; } = new TaskCompletionSource<bool>();
        }

        /// <summary>A session service that records its initialization, cancellation and disposal.</summary>
        public sealed class Probe : IAsyncInitializable, IDisposable
        {
            private readonly Journal _journal;

            public Probe(Journal journal)
            {
                _journal = journal;
                Id = journal.Probes.Count;
                journal.Probes.Add(this);
            }

            public int Id { get; }
            public bool Disposed { get; private set; }

            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _journal.Log.Add($"init:{context.Generation}");
                if (context.Generation == 0 && _journal.ParkFirstGeneration)
                {
                    _journal.FirstStarted.TrySetResult(true);
                    try
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        _journal.Log.Add("cancelled:0");
                        throw;
                    }
                }
                if (context.Generation == _journal.FailGeneration) throw new InvalidOperationException("probe failed");
            }

            public void Dispose()
            {
                Disposed = true;
                _journal.Log.Add($"dispose:{Id}");
            }
        }

        /// <summary>A session service whose constructor requests a restart while the session is being built.</summary>
        public sealed class ConstructorRequester : IAsyncInitializable
        {
            private readonly Journal _journal;

            public ConstructorRequester(Journal journal, RuntimeFlowHost host)
            {
                _journal = journal;
                if (!journal.RequestFromConstructor) return;
                journal.RequestFromConstructor = false;
                journal.ConstructorRequest = host.RestartAsync("constructor-request");
            }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _journal.Log.Add($"requester:{context.Generation}");
                return Task.CompletedTask;
            }
        }

        /// <summary>A global service that requests a restart during the global phase of the startup.</summary>
        public sealed class GlobalRequester : IAsyncInitializable
        {
            private readonly RuntimeFlowHost _host;

            public GlobalRequester(RuntimeFlowHost host) => _host = host;

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _ = _host.RestartAsync("global-request");
                return Task.CompletedTask;
            }
        }

        /// <summary>Records every call, then yields once, so a test sees whether hooks are awaited one by one.</summary>
        public abstract class RecordingHook : IRestartPreparation
        {
            private readonly string _name;

            protected RecordingHook(Journal journal, string name)
            {
                Journal = journal;
                _name = name;
            }

            protected Journal Journal { get; }
            public List<RestartPreparationContext> Contexts { get; } = new List<RestartPreparationContext>();
            public CancellationToken Token { get; private set; }

            public async Task PrepareForRestartAsync(RestartPreparationContext context, CancellationToken cancellationToken)
            {
                Contexts.Add(context);
                Token = cancellationToken;
                Journal.Log.Add($"hook:{_name}:{context.Reason}:{context.Generation}:{context.SessionState}");
                await PrepareAsync(context, cancellationToken);
                await Task.Yield();
                Journal.Log.Add($"hook-done:{_name}");
            }

            protected virtual Task PrepareAsync(RestartPreparationContext context, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        public sealed class GlobalHook : RecordingHook
        {
            public GlobalHook(Journal journal) : base(journal, "global") { }
        }

        /// <summary>Resolves the doomed session's probe, the way a reset reads the services it clears.</summary>
        public sealed class SessionHookA : RecordingHook
        {
            public SessionHookA(Journal journal) : base(journal, "a") { }

            protected override Task PrepareAsync(RestartPreparationContext context, CancellationToken cancellationToken)
            {
                var probe = context.Session.Resolve<Probe>();
                Journal.Log.Add($"saw-probe:{probe.Id}:disposed={probe.Disposed}");
                return Task.CompletedTask;
            }
        }

        public sealed class SessionHookB : RecordingHook
        {
            public SessionHookB(Journal journal) : base(journal, "b") { }
        }

        public sealed class ThrowingHook : IRestartPreparation
        {
            public async Task PrepareForRestartAsync(RestartPreparationContext context, CancellationToken cancellationToken)
            {
                await Task.Yield();
                throw new InvalidOperationException("cover prefab missing");
            }
        }

        /// <summary>Never finishes and ignores its token.</summary>
        public sealed class HangingHook : IRestartPreparation
        {
            public CancellationToken Token { get; private set; }

            public Task PrepareForRestartAsync(RestartPreparationContext context, CancellationToken cancellationToken)
            {
                Token = cancellationToken;
                return new TaskCompletionSource<bool>().Task;
            }
        }

        /// <summary>Waits on its token until it is cancelled.</summary>
        public sealed class ParkingHook : IRestartPreparation
        {
            private readonly Journal _journal;

            public ParkingHook(Journal journal) => _journal = journal;

            public async Task PrepareForRestartAsync(RestartPreparationContext context, CancellationToken cancellationToken)
            {
                _journal.HookEntered.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _journal.Log.Add("hook-cancelled");
                    throw;
                }
            }
        }

        /// <summary>Counts its constructions: a hook of the global scope must not be built again for the session.</summary>
        public sealed class TransientHook : IRestartPreparation
        {
            private readonly Journal _journal;

            public TransientHook(Journal journal)
            {
                _journal = journal;
                journal.TransientHooksConstructed++;
            }

            public Task PrepareForRestartAsync(RestartPreparationContext context, CancellationToken cancellationToken)
            {
                _journal.HookCalls.Add("transient");
                return Task.CompletedTask;
            }
        }

        public sealed class ScopedHook : IRestartPreparation
        {
            private readonly Journal _journal;

            public ScopedHook(Journal journal)
            {
                _journal = journal;
                journal.ScopedHooksConstructed++;
            }

            public Task PrepareForRestartAsync(RestartPreparationContext context, CancellationToken cancellationToken)
            {
                _journal.HookCalls.Add("scoped");
                return Task.CompletedTask;
            }
        }

        public sealed class ExplodingHook : IRestartPreparation
        {
            public ExplodingHook(Journal journal)
            {
                if (journal.ExplodeHookConstructor) throw new InvalidOperationException("hook exploded");
            }

            public Task PrepareForRestartAsync(RestartPreparationContext context, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

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

        /// <summary>The suspension clock is process-wide: never leave it stopped for the next test.</summary>
        [TearDown]
        public void TearDown() => AppSuspension.Resume();

        private static void Hook<T>(IContainerBuilder builder) where T : class, IRestartPreparation
            => builder.Register<T>(Lifetime.Singleton).As<IRestartPreparation>().AsSelf();

        private RuntimeFlowHost Host(Action<IContainerBuilder>? global = null, Action<IContainerBuilder>? session = null)
            => new RuntimeFlowHost(
                builder =>
                {
                    builder.RegisterInstance(_journal);
                    global?.Invoke(builder);
                },
                builder =>
                {
                    _journal.SessionBuilds++;
                    builder.Register<Probe>(Lifetime.Singleton).AsSelf().As<IAsyncInitializable>();
                    session?.Invoke(builder);
                },
                _options);

        private async Task<T> Within<T>(Task<T> task, RuntimeFlowHost host, string what)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Bound));
            Assert.That(winner, Is.SameAs(task),
                $"{what} never settled: state {host.State}, generation {host.Generation}.\n{_log.Dump()}");
            return await task;
        }

        private async Task Settled(Task task, RuntimeFlowHost host, string what)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Bound));
            Assert.That(winner, Is.SameAs(task),
                $"{what} never settled: state {host.State}, generation {host.Generation}.\n{_log.Dump()}");
        }

        [Test]
        [Timeout(15000)]
        public async Task HooksRunOnASessionStillInitializingAfterItWasCancelledAndBeforeAnythingIsDisposed()
        {
            _journal.ParkFirstGeneration = true;
            await using var host = Host(Hook<GlobalHook>, Hook<SessionHookA>);

            var startup = host.StartAsync();
            await Within(_journal.FirstStarted.Task, host, "the first generation");
            var doomed = host.Session;
            var restart = host.RestartAsync("bundles-updated");
            var result = await Within(restart, host, "the restart");

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed), _log.Dump());
            Assert.That((await Within(startup, host, "the startup")).Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(host.Generation, Is.EqualTo(1));
            Assert.That(_journal.Log, Is.EqualTo(new[]
            {
                "init:0",
                "cancelled:0",
                "hook:global:bundles-updated:0:Running", "hook-done:global",
                "hook:a:bundles-updated:0:Running", "saw-probe:0:disposed=False", "hook-done:a",
                "dispose:0",
                "init:1"
            }), _log.Dump());

            var context = host.Global.Resolve<GlobalHook>().Contexts[0];
            Assert.That(context.WasInitializing, Is.True);
            Assert.That(context.Session, Is.SameAs(doomed));
            Assert.That(context.Generation, Is.EqualTo(0));
            Assert.That(context.Reason, Is.EqualTo("bundles-updated"));
        }

        [Test]
        [Timeout(15000)]
        public async Task GlobalHooksRunFirstThenTheSessionsOwnInRegistrationOrderEachOnce()
        {
            var shared = new GlobalHook(_journal);
            await using var host = Host(
                builder =>
                {
                    builder.RegisterInstance(shared).As<IRestartPreparation>().AsSelf();
                },
                builder =>
                {
                    Hook<SessionHookB>(builder);
                    Hook<SessionHookA>(builder);
                    // The same instance registered again below the global scope still runs once.
                    builder.RegisterInstance<IRestartPreparation>(shared);
                });
            await Within(host.StartAsync(), host, "the startup");

            await Within(host.RestartAsync("first"), host, "the first restart");
            await Within(host.RestartAsync("second"), host, "the second restart");

            Assert.That(_journal.Log, Is.EqualTo(new[]
            {
                "init:0",
                "hook:global:first:0:Completed", "hook-done:global",
                "hook:b:first:0:Completed", "hook-done:b",
                "hook:a:first:0:Completed", "saw-probe:0:disposed=False", "hook-done:a",
                "dispose:0",
                "init:1",
                "hook:global:second:1:Completed", "hook-done:global",
                "hook:b:second:1:Completed", "hook-done:b",
                "hook:a:second:1:Completed", "saw-probe:1:disposed=False", "hook-done:a",
                "dispose:1",
                "init:2"
            }), _log.Dump());
            Assert.That(shared.Contexts[0].WasInitializing, Is.False);
            Assert.That(shared.Contexts.Count, Is.EqualTo(2));
        }

        /// <summary>
        /// The global hooks come from the global scope only: resolving the collection from the session would
        /// construct a Transient global hook a second time and a Scoped one as a session instance.
        /// </summary>
        [Test]
        [Timeout(15000)]
        public async Task GlobalHooksAreConstructedOncePerSessionBuildAndNotAgainForTheSession()
        {
            await using var host = Host(
                builder =>
                {
                    builder.Register<TransientHook>(Lifetime.Transient).As<IRestartPreparation>();
                    builder.Register<ScopedHook>(Lifetime.Scoped).As<IRestartPreparation>();
                },
                Hook<SessionHookB>);
            await Within(host.StartAsync(), host, "the startup");

            Assert.That(_journal.TransientHooksConstructed, Is.EqualTo(1), "one construction for the one session build");
            Assert.That(_journal.ScopedHooksConstructed, Is.EqualTo(1), "the global scope's own instance only");

            await Within(host.RestartAsync("first"), host, "the first restart");
            await Within(host.RestartAsync("second"), host, "the second restart");

            Assert.That(_journal.TransientHooksConstructed, Is.EqualTo(3), "once per session build, three builds");
            Assert.That(_journal.ScopedHooksConstructed, Is.EqualTo(1), "the global Scoped hook is resolved from the global scope");
            Assert.That(_journal.HookCalls, Is.EqualTo(new[] { "transient", "scoped", "transient", "scoped" }), _log.Dump());
            Assert.That(_journal.Log, Is.EqualTo(new[]
            {
                "init:0",
                "hook:b:first:0:Completed", "hook-done:b",
                "dispose:0",
                "init:1",
                "hook:b:second:1:Completed", "hook-done:b",
                "dispose:1",
                "init:2"
            }), "the session's own hook runs after the global ones, once\n" + _log.Dump());
        }

        [Test]
        [Timeout(15000)]
        public async Task HooksRunForASessionBuiltButFrozenBeforeItsRunStarted()
        {
            _journal.RequestFromConstructor = true;
            await using var host = Host(Hook<GlobalHook>,
                builder => builder.Register<ConstructorRequester>(Lifetime.Singleton).AsSelf().As<IAsyncInitializable>());

            var result = await Within(host.StartAsync(), host, "the startup");

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed), _log.Dump());
            Assert.That(host.Generation, Is.EqualTo(1));
            await Within(_journal.ConstructorRequest!, host, "the constructor's request");
            Assert.That(_journal.Log, Is.EqualTo(new[]
            {
                "hook:global:constructor-request:0:NotStarted", "hook-done:global",
                "dispose:0",
                "init:1", "requester:1"
            }), "nothing of the frozen generation initialized\n" + _log.Dump());
            Assert.That(host.Global.Resolve<GlobalHook>().Contexts[0].WasInitializing, Is.False);
        }

        [Test]
        [Timeout(15000)]
        public async Task HooksRunForAFailedSession()
        {
            _journal.FailGeneration = 0;
            await using var host = Host(Hook<GlobalHook>);
            await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => Within(host.StartAsync(), host, "the startup"));

            var retry = await Within(host.RestartAsync("retry"), host, "the retry");

            Assert.That(retry.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(_journal.Log, Is.EqualTo(new[]
            {
                "init:0",
                "hook:global:retry:0:Failed", "hook-done:global",
                "dispose:0",
                "init:1"
            }), _log.Dump());
        }

        [Test]
        [Timeout(15000)]
        public async Task AThrowingHookIsLoggedAndTheRestartCompletes()
        {
            await using var host = Host(
                builder => builder.Register<ThrowingHook>(Lifetime.Singleton).As<IRestartPreparation>(),
                Hook<SessionHookB>);
            await Within(host.StartAsync(), host, "the startup");

            var result = await Within(host.RestartAsync("bundles-updated"), host, "the restart");

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(host.Generation, Is.EqualTo(1));
            Assert.That(_journal.Log, Is.EqualTo(new[]
            {
                "init:0",
                "hook:b:bundles-updated:0:Completed", "hook-done:b",
                "dispose:0",
                "init:1"
            }), "the hook after the throwing one still runs\n" + _log.Dump());
            Assert.That(_log.Has(LogLevel.Error,
                "[RuntimeFlow] restart 'bundles-updated': the restart preparation ThrowingHook threw InvalidOperationException; " +
                "the restart continues."), Is.True, _log.Dump());
        }

        [Test]
        [Timeout(15000)]
        public async Task AHangingHookIsCutAtTheTimeoutWithItsTokenCancelled()
        {
            _options.RestartPreparationTimeout = TimeSpan.FromMilliseconds(200);
            await using var host = Host(
                builder => builder.Register<HangingHook>(Lifetime.Singleton).As<IRestartPreparation>().AsSelf(),
                Hook<SessionHookB>);
            await Within(host.StartAsync(), host, "the startup");

            var result = await Within(host.RestartAsync("bundles-updated"), host, "the restart");

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(host.Global.Resolve<HangingHook>().Token.IsCancellationRequested, Is.True);
            Assert.That(_journal.Log, Is.EqualTo(new[]
            {
                "init:0",
                "hook:b:bundles-updated:0:Completed", "hook-done:b",
                "dispose:0",
                "init:1"
            }), _log.Dump());
            Assert.That(_log.Has(LogLevel.Error,
                "[RuntimeFlow] restart 'bundles-updated': the restart preparation HangingHook did not finish within 0.2s"),
                Is.True, _log.Dump());
        }

        /// <summary>
        /// The hook timeout counts active time, like service timeouts: a player who backgrounds the game
        /// while the loading cover goes up must not come back to a cut hook.
        /// </summary>
        [Test]
        [Timeout(15000)]
        public async Task TheHookTimeoutDoesNotCountTimeTheApplicationSpentSuspended()
        {
            _options.RestartPreparationTimeout = TimeSpan.FromMilliseconds(200);
            await using var host = Host(
                builder => builder.Register<HangingHook>(Lifetime.Singleton).As<IRestartPreparation>().AsSelf());
            await Within(host.StartAsync(), host, "the startup");
            var hook = host.Global.Resolve<HangingHook>();

            AppSuspension.Suspend();
            var restart = host.RestartAsync("bundles-updated");
            await Task.Delay(TimeSpan.FromMilliseconds(600));

            Assert.That(restart.IsCompleted, Is.False, "three times the timeout passed, all of it suspended\n" + _log.Dump());
            Assert.That(hook.Token.CanBeCanceled && !hook.Token.IsCancellationRequested, Is.True, "the hook is still waited for");

            AppSuspension.Resume();
            var result = await Within(restart, host, "the restart");

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(hook.Token.IsCancellationRequested, Is.True, "after resuming, the timeout runs again and cuts the hook");
            Assert.That(_log.Has(LogLevel.Error,
                "[RuntimeFlow] restart 'bundles-updated': the restart preparation HangingHook did not finish within 0.2s"),
                Is.True, _log.Dump());
        }

        [Test]
        [Timeout(15000)]
        public async Task DisposingTheHostDuringAHookCancelsItsTokenAndBuildsNoNewSession()
        {
            var host = Host(builder => builder.Register<ParkingHook>(Lifetime.Singleton).As<IRestartPreparation>());
            try
            {
                await Within(host.StartAsync(), host, "the startup");
                var restart = host.RestartAsync("bundles-updated");
                await Within(_journal.HookEntered.Task, host, "the hook");

                await Settled(host.DisposeAsync().AsTask(), host, "the disposal");
                await Settled(restart, host, "the restart");

                Assert.That(restart.IsCanceled || restart.Exception?.InnerException is ObjectDisposedException,
                    Is.True, restart.Exception?.ToString());
                Assert.That(_journal.Log, Is.EqualTo(new[] { "init:0", "hook-cancelled", "dispose:0" }), _log.Dump());
                Assert.That(_journal.SessionBuilds, Is.EqualTo(1));
            }
            finally
            {
                await host.DisposeAsync();
            }
        }

        [Test]
        [Timeout(15000)]
        public async Task DisposingTheHostCallsNoHook()
        {
            _journal.ParkFirstGeneration = true;
            var host = Host(Hook<GlobalHook>, Hook<SessionHookA>);
            var startup = host.StartAsync();
            await Within(_journal.FirstStarted.Task, host, "the first generation");

            await Settled(host.DisposeAsync().AsTask(), host, "the disposal");
            await Settled(startup, host, "the startup");

            Assert.That(_journal.Log, Is.EqualTo(new[] { "init:0", "cancelled:0", "dispose:0" }), _log.Dump());
        }

        [Test]
        [Timeout(15000)]
        public async Task ARestartFoldedIntoTheGlobalPhaseCallsNoHook()
        {
            await using var host = Host(
                builder =>
                {
                    Hook<GlobalHook>(builder);
                    builder.Add<GlobalRequester>();
                });

            var result = await Within(host.StartAsync(), host, "the startup");

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed), _log.Dump());
            Assert.That(host.Generation, Is.EqualTo(1));
            Assert.That(_journal.Log, Is.EqualTo(new[] { "init:1" }), "no session existed to prepare\n" + _log.Dump());
            Assert.That(host.IsRestarting, Is.False);
        }

        [Test]
        [Timeout(15000)]
        public async Task AHookWhoseConstructorThrowsFailsTheSessionBuild()
        {
            _journal.ExplodeHookConstructor = true;
            await using var host = Host(session: builder =>
                builder.Register<ExplodingHook>(Lifetime.Singleton).As<IRestartPreparation>());

            var failure = await AsyncTestAssert.ThrowsAsync<Exception>(() => Within(host.StartAsync(), host, "the startup"));

            Assert.That(failure.ToString(), Does.Contain("hook exploded"));
            Assert.That(host.State, Is.EqualTo(RunState.Failed));
            Assert.That(_journal.Probes[0].Disposed, Is.True, "the half-built session is released");
            Assert.That(_journal.Log, Is.EqualTo(new[] { "dispose:0" }), _log.Dump());

            _journal.ExplodeHookConstructor = false;
            var retry = await Within(host.RestartAsync("retry"), host, "the retry");

            Assert.That(retry.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(host.Generation, Is.EqualTo(1));
        }
    }
}
