using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.Tests.Testing
{
    /// <summary>
    /// The test harness runs the production installers unchanged and swaps single services for fakes:
    /// the replaced service is never constructed, the fake takes part in the graph, and startup is
    /// bounded by a deadline that reports what was still running.
    /// </summary>
    [TestFixture]
    public sealed class TestFlowTests
    {
        public interface IBackend
        {
            string Name { get; }
        }

        public interface IFakeBackend : IBackend, IAsyncInitializable, IAsyncDisposable
        {
        }

        public sealed class Probe
        {
            public int Constructed { get; set; }
            public List<string> Initialized { get; } = new List<string>();
            public int EntryPointRuns { get; set; }
        }

        public sealed class RealBackend : IBackend, IAsyncInitializable
        {
            private readonly Probe _probe;

            public RealBackend(Probe probe)
            {
                _probe = probe;
                probe.Constructed++;
            }

            public string Name => "real";

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _probe.Initialized.Add("real");
                return Task.CompletedTask;
            }
        }

        public sealed class FakeBackend : IBackend, IAsyncInitializable
        {
            private readonly Probe _probe;

            public FakeBackend(Probe probe) => _probe = probe;

            public string Name => "fake";

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _probe.Initialized.Add("fake");
                return Task.CompletedTask;
            }
        }

        public sealed class Consumer : IAsyncInitializable
        {
            private readonly Probe _probe;

            public Consumer(Probe probe, IBackend backend)
            {
                _probe = probe;
                Backend = backend;
            }

            public IBackend Backend { get; }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _probe.Initialized.Add($"consumer:{Backend.Name}");
                return Task.CompletedTask;
            }
        }

        public sealed class HangingService : IAsyncInitializable
        {
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
                => Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public sealed class CountingEntryPoint : IInitializable
        {
            private readonly Probe _probe;

            public CountingEntryPoint(Probe probe) => _probe = probe;

            public void Initialize() => _probe.EntryPointRuns++;
        }

        private Probe _probe = null!;

        [SetUp]
        public void SetUp() => _probe = new Probe();

        private static bool Logged(IReadOnlyList<string> log, string fragment)
        {
            foreach (var line in log)
            {
                if (line.Contains(fragment)) return true;
            }
            return false;
        }

        private static void AssertLogged(IReadOnlyList<string> log, string fragment)
        {
            foreach (var line in log)
            {
                if (line.Contains(fragment)) return;
            }
            Assert.Fail($"no log line contains '{fragment}':{Environment.NewLine}{string.Join(Environment.NewLine, log)}");
        }

        private void Nothing(IContainerBuilder builder) => builder.RegisterInstance(_probe);

        private void SessionWithRealBackend(IContainerBuilder builder)
        {
            builder.RegisterInstance(_probe);
            builder.Register<RealBackend>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces();
            builder.Add<Consumer>();
        }

        [Test]
        [Timeout(10000)]
        public async Task AnOverrideReplacesTheRealServiceWhichIsNeverConstructed()
        {
            await using var app = await TestFlow
                .Create(Nothing, SessionWithRealBackend)
                .Override<IBackend>(new FakeBackend(_probe))
                .StartAsync();

            Assert.That(_probe.Constructed, Is.EqualTo(0), "the real service must never be constructed");
            Assert.That(_probe.Initialized, Is.EqualTo(new[] { "fake", "consumer:fake" }));
            Assert.That(app.Resolve<IBackend>().Name, Is.EqualTo("fake"));
            Assert.That(app.Resolve<Consumer>().Backend.Name, Is.EqualTo("fake"));
            Assert.That(app.Result!.Outcome, Is.EqualTo(StartupOutcome.Completed));
        }

        [Test]
        [Timeout(10000)]
        public async Task TheImplementationVariantRegistersTheReplacementAsASingleton()
        {
            await using var app = await TestFlow
                .Create(Nothing, SessionWithRealBackend)
                .Override<IBackend, FakeBackend>()
                .StartAsync();

            Assert.That(_probe.Constructed, Is.EqualTo(0));
            Assert.That(app.Resolve<IBackend>().Name, Is.EqualTo("fake"));
            Assert.That(app.Resolve<IBackend>(), Is.SameAs(app.Resolve<IBackend>()));
            Assert.That(_probe.Initialized, Does.Contain("fake"));
        }

        [Test]
        [Timeout(10000)]
        public async Task AnOverrideOfAnUnregisteredTypeNamesTheTypeAndTheScopes()
        {
            var flow = TestFlow.Create(Nothing, Nothing).Override<IBackend>(new FakeBackend(_probe));

            var failure = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(() => flow.StartAsync());

            Assert.That(failure.Message, Does.Contain("Override<IBackend>"));
            Assert.That(failure.Message, Does.Contain("global, session"));
            await flow.DisposeAsync();
        }

        [Test]
        [Timeout(10000)]
        public async Task AnOverrideInTheGlobalInstallerIsVisibleFromTheSession()
        {
            await using var app = await TestFlow
                .Create(SessionWithRealBackend, builder => builder.Add<Consumer>())
                .Override<IBackend>(new FakeBackend(_probe))
                .StartAsync();

            Assert.That(_probe.Constructed, Is.EqualTo(0));
            Assert.That(app.Resolve<IBackend>().Name, Is.EqualTo("fake"));
            Assert.That(_probe.Initialized, Is.EqualTo(new[] { "fake", "consumer:fake", "consumer:fake" }));
        }

        [Test]
        [Timeout(10000)]
        public async Task TheEntryPointDispatcherIsRegisteredExactlyOnce()
        {
            await using var app = await TestFlow
                .Create(Nothing, builder =>
                {
                    builder.RegisterInstance(_probe);
                    builder.RegisterEntryPoint<CountingEntryPoint>();
                    EntryPointsBuilder.EnsureDispatcherRegistered(builder);
                })
                .StartAsync();

            Assert.That(_probe.EntryPointRuns, Is.EqualTo(1));
            Assert.That(app.Session.TryGetRegistration(typeof(EntryPointDispatcher), out var registration), Is.True);
            Assert.That(registration.Provider, Is.Not.InstanceOf<IEnumerable<Registration>>(),
                "a second dispatcher registration would turn it into a collection");
        }

        [Test]
        [Timeout(10000)]
        public async Task TheStartupDeadlineReportsTheServiceThatHung()
        {
            var flow = TestFlow
                .Create(Nothing, builder => builder.Add<HangingService>())
                .WithStartupTimeout(TimeSpan.FromMilliseconds(300));

            var failure = await AsyncTestAssert.ThrowsAsync<TimeoutException>(() => flow.StartAsync());

            Assert.That(failure.Message, Does.StartWith("Startup did not finish within 0.3s."));
            Assert.That(failure.Message, Does.Contain("Running: HangingService"));
            await flow.DisposeAsync();
        }

        [Test]
        [Timeout(10000)]
        public async Task TheLogCapturesTheFrameworkDiagnostics()
        {
            await using var app = await TestFlow
                .Create(Nothing, SessionWithRealBackend)
                .StartAsync();

            AssertLogged(app.Log, "Information: [RuntimeFlow] session: started");
            AssertLogged(app.Log, "Information: [RuntimeFlow] session: completed");
            AssertLogged(app.Log, "RealBackend completed");
            Assert.That(_probe.Constructed, Is.EqualTo(1));
        }

        [Test]
        [Timeout(10000)]
        public async Task AFakeThatFailsInitializationBringsTheRunDown()
        {
            var fake = LifecycleFake.Of<IFakeBackend>(configure: behavior => behavior.FailInitializeAttempts(1));
            var flow = TestFlow.Create(Nothing, SessionWithRealBackend).Override<IBackend>(fake);

            var failure = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => flow.StartAsync());

            Assert.That(failure.Scope, Is.EqualTo("session"));
            Assert.That(failure.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(failure.InnerException!.Message, Does.Contain("initialize attempt 1 configured to fail"));
            await flow.DisposeAsync();
        }

        [Test]
        [Timeout(10000)]
        public async Task AHangingFakeProducesAStallWarning()
        {
            var fake = LifecycleFake.Of<IFakeBackend>(configure: behavior => behavior.Hang());
            var flow = TestFlow.Create(Nothing, SessionWithRealBackend)
                .Override<IBackend>(fake)
                .Configure(options => options.StallWarningAfter = TimeSpan.FromMilliseconds(100));

            // The warning is awaited through the log rather than through a startup deadline: a deadline
            // long enough to be reliable is time this test would spend doing nothing.
            var starting = flow.StartAsync();
            await AsyncTestAssert.Until(
                () => Logged(flow.Log, "Warning: [RuntimeFlow] session: no progress for"),
                TimeSpan.FromSeconds(5),
                string.Join(Environment.NewLine, flow.Log));

            AssertLogged(flow.Log, "Warning: [RuntimeFlow] session: no progress for");

            await flow.DisposeAsync();
            try
            {
                await starting;
            }
            catch (Exception)
            {
                // Disposing the flow cancels the hanging run; the startup task is observed, not asserted.
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task ADisposalFailureIsLoggedAndSwallowed()
        {
            var fake = LifecycleFake.Of<IFakeBackend>(configure: behavior => behavior.FailDisposeAttempts(1));
            var app = await TestFlow.Create(Nothing, SessionWithRealBackend).Override<IBackend>(fake).StartAsync();

            await AsyncTestAssert.DoesNotThrowAsync(async () => await app.DisposeAsync());

            AssertLogged(app.Log, "threw InvalidOperationException; continuing teardown.");
        }

        [Test]
        [Timeout(10000)]
        public async Task ADelayedFakeStillCompletesTheRun()
        {
            var handle = LifecycleFake.OfHandle<IFakeBackend>(
                configure: behavior => behavior.DelayInitialize(TimeSpan.FromMilliseconds(50)));

            await using var app = await TestFlow
                .Create(Nothing, SessionWithRealBackend)
                .Override<IBackend>(handle.Service)
                .StartAsync();

            Assert.That(app.Result!.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(handle.Log.WasInvoked("initialize"), Is.True);
            Assert.That(_probe.Constructed, Is.EqualTo(0));
            Assert.That(_probe.Initialized, Has.Count.EqualTo(1), "only the consumer records a line; the fake is a proxy");
        }
    }
}
