using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Lifecycle
{
    /// <summary>
    /// The host under hostile timing: a halted global scope, quits and disposals that land mid-chain,
    /// re-entrant requests from constructors and disposers, and the state it reports afterwards.
    /// </summary>
    [TestFixture]
    public sealed class HostRobustnessTests
    {
        public sealed class Journal
        {
            public RuntimeFlowHost? Host;
            public List<string> Entries { get; } = new List<string>();
            public int Generation0CtorRestarts;
            public bool BreakGraph = true;
            public bool? TokenCancelledRightAfterDispose;
            public Task? Disposal;
            public Func<ValueTask>? OnSessionDispose;
            public ScopeRun? Victim;
        }

        // ------------------------------------------------------------------ A1: global halt

        public sealed class GlobalHalter : IAsyncInitializable
        {
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                context.Halt("maintenance");
                return Task.CompletedTask;
            }
        }

        [DependsOn(typeof(GlobalHalter))]
        public sealed class SkippedByHalt : AutoService { }

        public sealed class SessionService : AutoService { }

        public sealed class ChildConsumer : IAsyncInitializable
        {
            private readonly Journal _journal;

            public ChildConsumer(Journal journal, SkippedByHalt dependency) => _journal = journal;

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _journal.Entries.Add("child-consumer");
                return Task.CompletedTask;
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task AfterAGlobalHaltTheHostReportsTheHaltEverywhereAndRefusesARestart()
        {
            var log = new CapturingLogger();
            await using var host = new RuntimeFlowHost(
                builder =>
                {
                    builder.Add<GlobalHalter>();
                    builder.Add<SkippedByHalt>();
                },
                builder => builder.Add<SessionService>(),
                TestScope.Options(log));

            var result = await host.StartAsync();

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Halted), log.Dump());
            Assert.That(result.Scope, Is.EqualTo("global"));
            Assert.That(result.HaltReason, Is.EqualTo("maintenance"));
            Assert.That(result.HaltedBy, Is.EqualTo(nameof(GlobalHalter)));
            Assert.That(host.State, Is.EqualTo(RunState.Halted));

            var status = host.GetStatus();
            Assert.That(status.State, Is.EqualTo(RunState.Halted));
            Assert.That(status.HaltReason, Is.EqualTo("maintenance"));
            Assert.That(status.HaltedBy, Is.EqualTo(nameof(GlobalHalter)));
            Assert.That(status.Scope, Is.EqualTo("global"));

            var session = Assert.Throws<InvalidOperationException>(() => _ = host.Session);
            Assert.That(session!.Message, Does.Contain("halted by GlobalHalter"), session.Message);
            Assert.That(session.Message, Does.Contain("'maintenance'"));

            var restart = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(() => host.RestartAsync("retry"));
            Assert.That(restart.Message, Does.Contain("global scope was halted"), restart.Message);
            Assert.That(host.RestartCount, Is.EqualTo(0));

            Assert.That(await host.StartAsync(), Is.SameAs(result), "StartAsync keeps returning the halt");
        }

        [Test]
        [Timeout(10000)]
        public async Task ASessionHaltReportsTheSameServiceInTheStartupResultAndHostStatus()
        {
            await using var host = new RuntimeFlowHost(
                _ => { }, builder => builder.Add<GlobalHalter>(), TestScope.Options(new CapturingLogger()));

            var result = await host.StartAsync();
            var status = host.GetStatus();

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Halted));
            Assert.That(result.Scope, Is.EqualTo("session"));
            Assert.That(result.HaltedBy, Is.EqualTo(nameof(GlobalHalter)));
            Assert.That(status.State, Is.EqualTo(RunState.Halted));
            Assert.That(status.Scope, Is.EqualTo("session"));
            Assert.That(status.HaltReason, Is.EqualTo(result.HaltReason));
            Assert.That(status.HaltedBy, Is.EqualTo(result.HaltedBy));
        }

        [Test]
        [Timeout(10000)]
        public async Task AChildOfAHaltedRunDoesNotStartServicesOnSkippedParents()
        {
            var log = new CapturingLogger();
            var journal = new Journal();
            var options = TestScope.Options(log);
            var root = TestScope.Build(builder =>
            {
                builder.RegisterInstance(journal);
                builder.Add<GlobalHalter>();
                builder.Add<SkippedByHalt>();
            });
            try
            {
                var parent = ScopeRun.Create(root, "global", options);
                var parentResult = await parent.RunAsync();
                Assert.That(parentResult.Outcome, Is.EqualTo(StartupOutcome.Halted));

                var child = root.CreateScope(builder => builder.Add<ChildConsumer>());
                var run = ScopeRun.Create(child, "child", options, new[] { parent }, ownsScope: true);
                var failure = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => run.RunAsync());

                Assert.That(journal.Entries, Is.Empty, "a child service started on a parent service the halt skipped\n" + log.Dump());
                var cause = failure.InnerException!.Message;
                Assert.That(cause, Does.Contain("SkippedByHalt"), cause);
                Assert.That(cause, Does.Contain("Skipped"), cause);
                await run.DisposeAsync();
                await parent.DisposeAsync();
            }
            finally
            {
                root.Dispose();
            }
        }

        // ------------------------------------------------------------------ B1: caller-owned containers

        public sealed class CallerOwned : IAsyncInitializable, IAsyncDisposable
        {
            public int Disposed;
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;

            public ValueTask DisposeAsync()
            {
                Disposed++;
                return default;
            }
        }

        public interface INeverRegistered { }

        [DependsOn(typeof(INeverRegistered))]
        public sealed class BrokenGraph : AutoService { }

        [Test]
        [Timeout(10000)]
        public async Task AGraphErrorNeverDisposesTheServicesOfACallerOwnedContainer()
        {
            var log = new CapturingLogger();
            var owned = new CallerOwned();
            var global = TestScope.Build(builder =>
            {
                builder.RegisterInstance(owned).As<IAsyncInitializable>();
                builder.Add<BrokenGraph>();
            });
            try
            {
                Assert.Throws<InitGraphException>(() => ScopeRun.Create(global, "external", TestScope.Options(log)));
                Assert.That(owned.Disposed, Is.EqualTo(0), "ScopeRun.Create(ownsScope: false) released the caller's singleton");

                var host = RuntimeFlowHost.From(global, builder => { }, TestScope.Options(log));
                await AsyncTestAssert.ThrowsAsync<InitGraphException>(() => host.StartAsync());
                await host.DisposeAsync();
                Assert.That(owned.Disposed, Is.EqualTo(0), "RuntimeFlowHost.From released the caller's singleton");
            }
            finally
            {
                global.Dispose();
            }
        }

        // ------------------------------------------------------------------ B2: quit and child runs

        public sealed class ChildGate : ControlledService { }

        public sealed class AfterChildGate : AutoService
        {
            public AfterChildGate(ChildGate gate) { }
        }

        [Test]
        [Timeout(10000)]
        public async Task AQuitThatAbortsARestartStillSettlesTheFrozenChildRuns()
        {
            var log = new CapturingLogger();
            var host = new RuntimeFlowHost(builder => { }, builder => builder.Add<SessionService>(), TestScope.Options(log));
            try
            {
                await host.StartAsync();
                var scope = host.Session.CreateScope(builder =>
                {
                    builder.Add<ChildGate>();
                    builder.Add<AfterChildGate>();
                });
                var child = host.InitializeScopeAsync(scope, "child");
                await scope.Resolve<ChildGate>().Started;

                var restart = host.RestartAsync("bundles");
                host.OnQuitting();

                var winner = await Task.WhenAny(child, Task.Delay(3000));
                Assert.That(winner, Is.SameAs(child), "the frozen child run was never cancelled\n" + log.Dump());
                Assert.That(child.IsCanceled, Is.True, log.Dump());
                await Task.WhenAny(restart, Task.Delay(1000));
                _ = restart.Exception;

                var late = host.Global.CreateScope(builder => builder.Add<SessionService>());
                var refused = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(() => host.InitializeScopeAsync(late, "late"));
                Assert.That(refused.Message, Does.Contain("quitting"), refused.Message);
                late.Dispose();
            }
            finally
            {
                await host.DisposeAsync();
            }
        }

        // ------------------------------------------------------------------ B3: restart from a child constructor

        public sealed class RestartingChildCtor : IAsyncInitializable
        {
            private readonly Journal _journal;

            public RestartingChildCtor(Journal journal, RuntimeFlowHost host)
            {
                _journal = journal;
                _ = host.RestartAsync("from-child-ctor");
            }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _journal.Entries.Add("child-started");
                return Task.CompletedTask;
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task AChildWhoseConstructorRequestsARestartIsRefusedInsteadOfStarting()
        {
            var log = new CapturingLogger();
            var journal = new Journal();
            await using var host = new RuntimeFlowHost(
                builder => builder.RegisterInstance(journal),
                builder => builder.Add<SessionService>(),
                TestScope.Options(log));
            await host.StartAsync();

            var scope = host.Session.CreateScope(builder => builder.Add<RestartingChildCtor>());
            var refused = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(() => host.InitializeScopeAsync(scope, "child"));

            Assert.That(refused.Message, Does.Contain("restart"), refused.Message);
            Assert.That(journal.Entries, Is.Empty, "the child started on a session a restart already doomed\n" + log.Dump());
            Assert.That(host.ChildRuns, Is.Empty);
            await AsyncTestAssert.Until(() => host.State == RunState.Completed && host.Generation == 1, TimeSpan.FromSeconds(3), log.Dump());
        }

        // ------------------------------------------------------------------ B4: budget leaks

        public sealed class FailingGlobalRestartingOnDispose : IAsyncInitializable, IAsyncDisposable
        {
            private readonly Journal _journal;
            private readonly RuntimeFlowHost _host;

            public FailingGlobalRestartingOnDispose(Journal journal, RuntimeFlowHost host)
            {
                _journal = journal;
                _host = host;
            }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
                => _journal.BreakGraph ? Task.FromException(new InvalidOperationException("global down")) : Task.CompletedTask;

            public async ValueTask DisposeAsync()
            {
                if (!_journal.BreakGraph) return;
                var request = _host.RestartAsync("during-global-teardown");
                _ = request.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                await Task.Yield();
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task ARestartFoldedIntoAFailingGlobalTeardownDoesNotEatTheBudget()
        {
            var log = new CapturingLogger();
            var journal = new Journal();
            var options = TestScope.Options(log);
            options.MaxRestartsPerWindow = 1;
            await using var host = new RuntimeFlowHost(
                builder =>
                {
                    builder.RegisterInstance(journal);
                    builder.Add<FailingGlobalRestartingOnDispose>();
                },
                builder => builder.Add<SessionService>(),
                options);

            await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => host.StartAsync());
            journal.BreakGraph = false;
            await host.StartAsync();

            var restart = await host.RestartAsync("the only one");
            Assert.That(restart.Outcome, Is.EqualTo(StartupOutcome.Completed), log.Dump());
        }

        public sealed class CtorRestartThenBrokenGraph : IAsyncInitializable
        {
            public CtorRestartThenBrokenGraph(Journal journal, RuntimeFlowHost host)
            {
                if (journal.Generation0CtorRestarts++ > 0) return;
                _ = host.RestartAsync("from-session-ctor").ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        [Test]
        [Timeout(10000)]
        public async Task ARestartFoldedIntoASessionBuildThatFailsDoesNotEatTheBudget()
        {
            var log = new CapturingLogger();
            var journal = new Journal();
            var options = TestScope.Options(log);
            options.MaxRestartsPerWindow = 1;
            await using var host = new RuntimeFlowHost(
                builder => builder.RegisterInstance(journal),
                builder =>
                {
                    builder.Add<CtorRestartThenBrokenGraph>();
                    if (journal.BreakGraph) builder.Add<BrokenGraph>();
                },
                options);

            await AsyncTestAssert.ThrowsAsync<InitGraphException>(() => host.StartAsync());
            journal.BreakGraph = false;

            var retry = await host.RestartAsync("retry");
            Assert.That(retry.Outcome, Is.EqualTo(StartupOutcome.Completed), log.Dump());
        }

        // ------------------------------------------------------------------ B5: children disposing children

        public sealed class DisposesAnotherChildOnCancel : IAsyncInitializable
        {
            private readonly Journal _journal;

            public DisposesAnotherChildOnCancel(Journal journal) => _journal = journal;

            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                cancellationToken.Register(() =>
                {
                    var victim = _journal.Victim;
                    if (victim != null) _ = victim.DisposeAsync();
                });
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }

        public sealed class SlowToCancel : IAsyncInitializable
        {
            public TaskCompletionSource<bool> Started { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                Started.TrySetResult(true);
                await Task.Delay(400);
            }
        }

        public sealed class Victim : AutoService { }

        [Test]
        [Timeout(10000)]
        public async Task ACancellationCallbackThatDisposesAnotherChildDoesNotBreakTheTeardown()
        {
            var log = new CapturingLogger();
            var journal = new Journal();
            var host = new RuntimeFlowHost(
                builder => builder.RegisterInstance(journal),
                builder => builder.Add<SlowToCancel>(),
                TestScope.Options(log));

            var start = host.StartAsync();
            var session = await WaitForSession(host);
            await session.Resolve<SlowToCancel>().Started.Task;

            // Global children may start while the session is still initializing.
            var victimRun = await host.InitializeScopeAsync(host.Global.CreateScope(b => b.Add<Victim>()), "victim");
            journal.Victim = victimRun;
            var killer = host.InitializeScopeAsync(host.Global.CreateScope(b => b.Add<DisposesAnotherChildOnCancel>()), "killer");
            await Task.Yield();

            Exception? thrown = null;
            try
            {
                await host.DisposeAsync();
            }
            catch (Exception exception)
            {
                thrown = exception;
            }

            Assert.That(thrown, Is.Null, "DisposeAsync threw: " + thrown + "\n" + log.Dump());
            Assert.That(victimRun.State, Is.EqualTo(RunState.Disposed));
            await Task.WhenAny(killer, Task.Delay(1000));
            await Task.WhenAny(start, Task.Delay(1000));
            _ = killer.Exception;
            _ = start.Exception;
            Assert.That(host.State, Is.EqualTo(RunState.Disposed));
        }

        private static async Task<IScopedObjectResolver> WaitForSession(RuntimeFlowHost host)
        {
            IScopedObjectResolver? session = null;
            await AsyncTestAssert.Until(() =>
            {
                try { session = host.Session; }
                catch (InvalidOperationException) { }
                return session != null;
            }, TimeSpan.FromSeconds(3), "the session was never published");
            return session!;
        }

        // ------------------------------------------------------------------ B6: DisposeAsync from InitializeAsync

        public sealed class DisposesTheHost : IAsyncInitializable
        {
            private readonly Journal _journal;

            public DisposesTheHost(Journal journal) => _journal = journal;

            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _journal.Disposal = _journal.Host!.DisposeAsync().AsTask();
                _journal.TokenCancelledRightAfterDispose = cancellationToken.IsCancellationRequested;
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task DisposingTheHostFromInsideInitializeAsyncDoesNotCancelTheCallerSynchronously()
        {
            var log = new CapturingLogger();
            var journal = new Journal();
            var host = new RuntimeFlowHost(
                builder => builder.RegisterInstance(journal),
                builder => builder.Add<DisposesTheHost>(),
                TestScope.Options(log));
            journal.Host = host;

            var start = host.StartAsync();
            await AsyncTestAssert.Until(() => journal.Disposal != null, TimeSpan.FromSeconds(3), log.Dump());
            await journal.Disposal!;
            await Task.WhenAny(start, Task.Delay(1000));
            _ = start.Exception;

            Assert.That(journal.TokenCancelledRightAfterDispose, Is.False,
                "DisposeAsync cancelled the calling service's own token inside its InitializeAsync");
            Assert.That(host.State, Is.EqualTo(RunState.Disposed));
        }

        // ------------------------------------------------------------------ B7: quit mid-teardown

        public sealed class QuitsOnDispose : IAsyncInitializable, IAsyncDisposable
        {
            private readonly Journal _journal;

            public QuitsOnDispose(Journal journal) => _journal = journal;

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;

            public ValueTask DisposeAsync()
            {
                _journal.Host!.OnQuitting();
                return default;
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task AQuitThatAbortsARestartAfterTheTeardownLeavesACoherentState()
        {
            var log = new CapturingLogger();
            var journal = new Journal();
            await using var host = new RuntimeFlowHost(
                builder => builder.RegisterInstance(journal),
                builder => builder.Add<QuitsOnDispose>(),
                TestScope.Options(log));
            journal.Host = host;
            await host.StartAsync();

            var restart = host.RestartAsync("aborted-by-quit");
            await Task.WhenAny(restart, Task.Delay(3000));
            Assert.That(restart.IsCanceled, Is.True, "the restart aborted by the quit ends cancelled\n" + log.Dump());

            Assert.That(host.State, Is.EqualTo(RunState.Cancelled), "no session exists, so the host is not Completed");
            Assert.That(host.GetStatus().State, Is.EqualTo(RunState.Cancelled));
            var session = Assert.Throws<InvalidOperationException>(() => _ = host.Session);
            Assert.That(session!.Message, Does.Contain("quitting"), session.Message);
        }

        // ------------------------------------------------------------------ B8: getters after dispose

        [Test]
        [Timeout(10000)]
        public async Task TheScopeGettersThrowOnceTheHostIsDisposed()
        {
            var log = new CapturingLogger();
            var host = new RuntimeFlowHost(builder => { }, builder => builder.Add<SessionService>(), TestScope.Options(log));
            await host.StartAsync();
            await host.DisposeAsync();

            Assert.Throws<ObjectDisposedException>(() => _ = host.Session);
            Assert.Throws<ObjectDisposedException>(() => _ = host.Global);
        }
    }
}
