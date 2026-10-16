using System;
using System.Globalization;
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
    /// Calls the host refuses instead of hanging or running on the wrong thread — a startup awaited from
    /// inside its own graph, calls without a synchronization context, a restart on a disposed host — plus
    /// the diagnostics that must never fail silently or break teardown: VContainer reflection, registry
    /// subscribers, culture-invariant rendering and cached status snapshots.
    /// </summary>
    [TestFixture]
    public sealed class HostGuardTests
    {
        public sealed class Probe
        {
            public Exception? Error { get; set; }
            public bool Returned { get; set; }
        }

        /// <summary>Awaits the host's startup from inside its own initialization, after a yield.</summary>
        public sealed class StartingFromInside : IAsyncInitializable
        {
            private readonly RuntimeFlowHost _host;
            private readonly Probe _probe;

            public StartingFromInside(RuntimeFlowHost host, Probe probe)
            {
                _host = host;
                _probe = probe;
            }

            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                // The refusal must hold in a continuation too, not only in the synchronous part of the call.
                await Task.Yield();
                try
                {
                    await _host.StartAsync();
                    _probe.Returned = true;
                }
                catch (InvalidOperationException error)
                {
                    _probe.Error = error;
                }
            }
        }

        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

        private CapturingLogger _log = null!;
        private RuntimeFlowOptions _options = null!;

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _options = TestScope.Options(_log);
        }

        /// <summary>Awaits a task, failing the test (instead of hanging it) when it never settles.</summary>
        private async Task<T> Within<T>(Task<T> task, string what)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Bound));
            Assert.That(winner, Is.SameAs(task), $"{what} never settled.\n{_log.Dump()}");
            return await task;
        }

        [TestCase("global")]
        [TestCase("session")]
        [Timeout(10000)]
        public async Task StartAsyncAwaitedFromInsideTheGraphFaultsInsteadOfHanging(string scope)
        {
            var probe = new Probe();
            await using var host = new RuntimeFlowHost(
                builder =>
                {
                    builder.RegisterInstance(probe);
                    if (scope == "global") builder.Add<StartingFromInside>();
                },
                builder =>
                {
                    if (scope == "session") builder.Add<StartingFromInside>();
                },
                _options);

            var result = await Within(host.StartAsync(), "the startup");

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed), _log.Dump());
            Assert.That(probe.Returned, Is.False);
            Assert.That(probe.Error, Is.Not.Null, "the service's StartAsync must fault");
            Assert.That(probe.Error!.Message, Does.StartWith(
                $"RuntimeFlowHost.StartAsync was called from inside the InitializeAsync of a {scope} service"));
            Assert.That(_log.Has(LogLevel.Warning,
                $"[RuntimeFlow] StartAsync refused: RuntimeFlowHost.StartAsync was called from inside the InitializeAsync of a {scope} service"),
                Is.True, "the refusal is logged, not only returned\n" + _log.Dump());
        }

        [Test]
        [Timeout(10000)]
        public async Task StartAsyncAfterTheStartupStillReturnsTheResult()
        {
            await using var host = new RuntimeFlowHost(builder => { }, builder => { }, _options);

            var first = await Within(host.StartAsync(), "the startup");
            var again = await Within(host.StartAsync(), "the second call");

            Assert.That(again, Is.SameAs(first), "outside the graph StartAsync keeps returning the current run");
        }

        [Test]
        [Timeout(10000)]
        public async Task CallsWithoutASynchronizationContextAreRefusedBeforeAnythingIsBuilt()
        {
            var builds = 0;
            var host = new RuntimeFlowHost(builder => builds++, builder => { }, _options);

            Task<StartupResult> start;
            Task dispose;
            var saved = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                start = host.StartAsync();
                dispose = host.DisposeAsync().AsTask();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(saved);
            }

            var refused = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(() => start);
            Assert.That(refused.Message, Does.StartWith("RuntimeFlowHost.StartAsync must be called on a thread with a SynchronizationContext"));
            var disposeRefused = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(() => dispose);
            Assert.That(disposeRefused.Message, Does.Contain("SynchronizationContext"));
            Assert.That(builds, Is.EqualTo(0), "nothing may be built off the main thread");
            Assert.That(host.State, Is.EqualTo(RunState.NotStarted), "a refused DisposeAsync tears nothing down");

            var result = await Within(host.StartAsync(), "the startup on the main thread");
            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(builds, Is.EqualTo(1));

            Task<StartupResult> restart;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                restart = host.RestartAsync("off-thread");
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(saved);
            }
            var restartRefused = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(() => restart);
            Assert.That(restartRefused.Message, Does.StartWith("RuntimeFlowHost.RestartAsync must be called"));
            Assert.That(host.RestartCount, Is.EqualTo(0));

            // Joining the finished startup off the main thread is refused too: the continuations of the
            // returned task would run on the thread pool.
            Task<StartupResult> join;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                join = host.StartAsync();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(saved);
            }
            await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(() => join);

            // Every refusal is logged once, naming the member: a dropped faulted task must not hide it.
            var errors = _log.Messages(LogLevel.Error).Where(m => m.Contains("must be called on a thread with a SynchronizationContext")).ToList();
            Assert.That(errors, Is.EqualTo(new[]
            {
                "[RuntimeFlow] StartAsync refused: " + refused.Message,
                "[RuntimeFlow] DisposeAsync refused: " + disposeRefused.Message,
                "[RuntimeFlow] RestartAsync refused: " + restartRefused.Message,
                "[RuntimeFlow] StartAsync refused: " + refused.Message
            }), _log.Dump());

            await host.DisposeAsync();
            Assert.That(host.State, Is.EqualTo(RunState.Disposed));
        }

        [Test]
        [Timeout(10000)]
        public async Task ARestartOnADisposedHostIsRefusedThroughItsTask()
        {
            var host = new RuntimeFlowHost(builder => { }, builder => { }, _options);
            await Within(host.StartAsync(), "the startup");
            await host.DisposeAsync();

            Task<StartupResult>? restart = null;
            Assert.DoesNotThrow(() => restart = host.RestartAsync("too-late"), "a refusal never throws synchronously");
            await AsyncTestAssert.ThrowsAsync<ObjectDisposedException>(() => restart!);
            Assert.That(_log.Has(LogLevel.Warning, "[RuntimeFlow] restart 'too-late' refused: the host has been disposed."),
                Is.True, _log.Dump());

            Assert.Throws<ArgumentNullException>(() => host.RestartAsync(null!), "argument validation stays synchronous");
        }

        [Test]
        public void AMissingVContainerMemberIsReportedOnce()
        {
            var handlerType = typeof(EntryPointsBuilder).Assembly.GetType("VContainer.Unity.EntryPointExceptionHandler");
            var container = TestScope.Build(builder => { });
            try
            {
                var warned = 0;
                Assert.That(RuntimeFlowHost.ConsumerHandlerOf(container, handlerType, null, true, _log, ref warned), Is.Null);
                Assert.That(RuntimeFlowHost.ConsumerHandlerOf(container, null, null, true, _log, ref warned), Is.Null);

                var warnings = _log.Messages(LogLevel.Warning)
                    .Where(m => m.StartsWith("[RuntimeFlow] cannot read VContainer's entry-point exception handler"))
                    .ToList();
                Assert.That(warnings, Has.Count.EqualTo(1), _log.Dump());
                Assert.That(warnings[0], Does.Contain("the field 'VContainer.Unity.EntryPointExceptionHandler.handler' was not found"));
            }
            finally
            {
                container.Dispose();
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task TheSupportedVContainerExposesTheEntryPointHandlerMembers()
        {
            var caught = 0;
            await using var host = new RuntimeFlowHost(
                builder => builder.RegisterEntryPointExceptionHandler(_ => caught++),
                builder => { },
                _options);

            await Within(host.StartAsync(), "the startup");

            Assert.That(_log.Has(LogLevel.Warning, "cannot read VContainer's entry-point exception handler"), Is.False,
                "the reflection over VContainer internals must keep working with the pinned VContainer\n" + _log.Dump());
        }

        [Test]
        [Timeout(10000)]
        public async Task AThrowingRegistrySubscriberNeverBreaksAHost()
        {
            Action thrower = () => throw new InvalidOperationException("dashboard bug");
            FlowRegistry.Changed += thrower;
            try
            {
                var host = new RuntimeFlowHost(builder => { }, builder => { }, _options);
                await Within(host.StartAsync(), "the startup");
                await AsyncTestAssert.DoesNotThrowAsync(() => host.DisposeAsync().AsTask());

                Assert.That(host.State, Is.EqualTo(RunState.Disposed));
                Assert.That(FlowRegistry.Live, Has.No.Member(host));
                Assert.That(_log.Messages(LogLevel.Error)
                        .Count(m => m == "[RuntimeFlow] a FlowRegistry.Changed subscriber threw InvalidOperationException; ignored."),
                    Is.EqualTo(2), "once for the construction, once for the disposal\n" + _log.Dump());
            }
            finally
            {
                FlowRegistry.Changed -= thrower;
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task StartingAPlayerLoopSessionDropsRegistrySubscribers()
        {
            var calls = 0;
            Action counter = () => calls++;
            FlowRegistry.Changed += counter;
            try
            {
                FlowRegistry.ResetForPlayerLoop();

                await using var host = new RuntimeFlowHost(builder => { }, builder => { }, _options);

                Assert.That(calls, Is.EqualTo(0), "a subscriber of the previous session must not survive it");
                Assert.That(FlowRegistry.Live, Has.Member(host), "the registry itself keeps working");
            }
            finally
            {
                FlowRegistry.Changed -= counter;
            }
        }

        [Test]
        public void StartupResultRendersCultureInvariant()
        {
            var saved = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            try
            {
                var result = new StartupResult(StartupOutcome.Completed, "session", TimeSpan.FromSeconds(1.5), Array.Empty<string>());
                Assert.That(result.ToString(), Is.EqualTo("session: completed in 1.5s"));
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task AConcludedRunHandsOutOneStatusSnapshot()
        {
            await using var host = new RuntimeFlowHost(builder => { }, builder => { }, _options);
            await Within(host.StartAsync(), "the startup");

            var first = host.GetStatus();
            Assert.That(host.GetStatus(), Is.SameAs(first), "nothing changed: no new snapshot per poll");
            Assert.That(host.SessionRun!.GetStatus(), Is.SameAs(host.SessionRun.GetStatus()));

            await Within(host.RestartAsync("refresh"), "the restart");

            var after = host.GetStatus();
            Assert.That(after, Is.Not.SameAs(first), "a restart produces a new snapshot");
            Assert.That(after.RestartCount, Is.EqualTo(1));
            Assert.That(after.State, Is.EqualTo(RunState.Completed));
        }
    }
}
