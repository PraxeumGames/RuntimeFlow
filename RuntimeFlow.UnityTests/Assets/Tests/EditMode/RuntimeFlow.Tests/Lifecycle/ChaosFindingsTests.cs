using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Lifecycle
{
    /// <summary>
    /// Product bugs found by <see cref="RestartChaosTests"/>, each reduced to a small deterministic
    /// reproduction and kept as a regression test. The chaos generator no longer excludes the behaviour
    /// that triggered them (global halts, the strict start-after-dispose check for child runs).
    /// </summary>
    [TestFixture]
    public sealed class ChaosFindingsTests
    {
        public sealed class Journal
        {
            public int ConsumerStarts;
            public int DependentStarts;
        }

        public sealed class GlobalHalter : IAsyncInitializable
        {
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                context.Halt("maintenance");
                return Task.CompletedTask;
            }
        }

        /// <summary>Skipped by the halt: it never initializes.</summary>
        [DependsOn(typeof(GlobalHalter))]
        public sealed class GlobalAfterHalter : AutoService { }

        public sealed class SessionConsumer : IAsyncInitializable
        {
            private readonly Journal _journal;

            public SessionConsumer(Journal journal, GlobalAfterHalter dependency) => _journal = journal;

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _journal.ConsumerStarts++;
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Chaos seeds 100, 166, 216, 241, 291, 333, 364, 389 (first run). A global service halts the global
        /// run; <c>StartCoreAsync</c> (RuntimeFlowHost.cs, after <c>await _globalRun.RunAsync</c>) ignores the
        /// Halted outcome and goes on to build the session. The session's externals map the global's
        /// Skipped/Cancelled nodes to Completed (GraphBuilder.ExternalState), so a session service whose
        /// dependency never initialized starts, and StartAsync reports the session's Completed instead of the
        /// halt.
        /// </summary>
        [Test]
        [Timeout(15000)]
        public async Task AHaltInTheGlobalScopeStopsTheStartupInsteadOfBuildingTheSession()
        {
            var log = new CapturingLogger();
            var journal = new Journal();
            var sessionBuilds = 0;
            await using var host = new RuntimeFlowHost(
                builder =>
                {
                    builder.RegisterInstance(journal);
                    builder.Add<GlobalHalter>();
                    builder.Add<GlobalAfterHalter>();
                },
                builder =>
                {
                    sessionBuilds++;
                    builder.Add<SessionConsumer>();
                },
                TestScope.Options(log));

            var startup = host.StartAsync();
            var winner = await Task.WhenAny(startup, Task.Delay(5000));
            Assert.That(winner, Is.SameAs(startup), "the startup never settled\n" + log.Dump());
            var result = await startup;

            Assert.That(journal.ConsumerStarts, Is.EqualTo(0),
                "a session service started although its global dependency was skipped by the halt\n" + log.Dump());
            Assert.That(sessionBuilds, Is.EqualTo(0), "no session is built after the global run halted\n" + log.Dump());
            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Halted), log.Dump());
            Assert.That(result.HaltedBy, Is.EqualTo(nameof(GlobalHalter)));
            Assert.That(host.State, Is.EqualTo(RunState.Halted));
        }

        /// <summary>Completes only when the test opens its gate, ignoring cancellation.</summary>
        public sealed class GatedIgnoringToken : IAsyncInitializable
        {
            public TaskCompletionSource<bool> Started { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Gate { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                Started.TrySetResult(true);
                await Gate.Task;
            }
        }

        public sealed class DependentOfGated : IAsyncInitializable
        {
            private readonly Journal _journal;

            public DependentOfGated(Journal journal, GatedIgnoringToken gated) => _journal = journal;

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _journal.DependentStarts++;
                return Task.CompletedTask;
            }
        }

        /// <summary>Ignores its token for longer than the grace, so disposing its run takes the whole grace.</summary>
        public sealed class SlowToCancel : IAsyncInitializable
        {
            public TaskCompletionSource<bool> Started { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                Started.TrySetResult(true);
                await Task.Delay(600);
            }
        }

        /// <summary>
        /// Chaos seed 317 (first run). <c>DisposeAsync</c> disposes child runs one after the other
        /// (RuntimeFlowHost.DisposeChildRunsAsync awaits each <c>DisposeRunQuietlyAsync</c> in turn) and
        /// cancels them only then; when the chain has already settled, AbandonChainAsync does not cancel
        /// them up front either. While the newest child waits out its grace, an older child is still live:
        /// a service that completes there releases its dependents, which start on a host that is being
        /// disposed.
        /// </summary>
        [Test]
        [Timeout(15000)]
        public async Task DisposingTheHostStartsNoFurtherServiceInAnyChildRun()
        {
            var log = new CapturingLogger();
            var journal = new Journal();
            var host = new RuntimeFlowHost(
                builder => builder.RegisterInstance(journal),
                builder => { },
                TestScope.Options(log));
            try
            {
                await host.StartAsync();

                var older = host.Global.CreateScope(builder =>
                {
                    builder.Add<GatedIgnoringToken>();
                    builder.Add<DependentOfGated>();
                });
                var olderRun = host.InitializeScopeAsync(older, "older");
                var gated = older.Resolve<GatedIgnoringToken>();
                await gated.Started.Task;

                var newer = host.Global.CreateScope(builder => builder.Add<SlowToCancel>());
                var newerRun = host.InitializeScopeAsync(newer, "newer");
                await newer.Resolve<SlowToCancel>().Started.Task;

                var disposal = host.DisposeAsync().AsTask();
                // The older child's service finishes while the newer child is still being disposed.
                gated.Gate.TrySetResult(true);

                var winner = await Task.WhenAny(disposal, Task.Delay(5000));
                Assert.That(winner, Is.SameAs(disposal), "the disposal never settled\n" + log.Dump());
                await Task.WhenAny(olderRun, Task.Delay(1000));
                await Task.WhenAny(newerRun, Task.Delay(1000));
                _ = olderRun.Exception;
                _ = newerRun.Exception;

                Assert.That(journal.DependentStarts, Is.EqualTo(0),
                    "a service of a child run started after RuntimeFlowHost.DisposeAsync was called\n" + log.Dump());
            }
            finally
            {
                await host.DisposeAsync();
            }
        }
    }
}
