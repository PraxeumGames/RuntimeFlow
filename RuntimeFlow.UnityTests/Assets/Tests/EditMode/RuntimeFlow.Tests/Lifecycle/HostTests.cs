using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Internal;
using RuntimeFlow.Tests.Support;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.Tests.Lifecycle
{
    /// <summary>
    /// The host composes both scopes, runs global before session, aggregates their status, and hands the
    /// VContainer entry points of each scope a chance to run inside Build()/CreateScope().
    /// </summary>
    [TestFixture]
    public sealed class HostTests
    {
        public sealed class Trace
        {
            public List<string> Entries { get; } = new List<string>();
            public bool GlobalDisposed { get; set; }
        }

        public sealed class GlobalService : IAsyncInitializable
        {
            private readonly Trace _trace;

            public GlobalService(Trace trace) => _trace = trace;

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _trace.Entries.Add($"global:{context.Scope}");
                return Task.CompletedTask;
            }
        }

        [Init(Weight = 3)]
        public sealed class Gate : ControlledService
        {
        }

        public sealed class SessionService : IAsyncInitializable
        {
            private readonly Trace _trace;

            public SessionService(Trace trace) => _trace = trace;

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _trace.Entries.Add($"session:{context.Scope}");
                return Task.CompletedTask;
            }
        }

        public sealed class EarlyEntryPoint : IInitializable
        {
            private readonly Trace _trace;

            public EarlyEntryPoint(Trace trace) => _trace = trace;

            public void Initialize() => _trace.Entries.Add("entry-point");
        }

        public sealed class ThrowingEntryPoint : IInitializable
        {
            public void Initialize() => throw new InvalidOperationException("entry point exploded");
        }

        public sealed class ForeignMarker : IDisposable
        {
            private readonly Trace _trace;

            public ForeignMarker(Trace trace) => _trace = trace;

            public void Dispose() => _trace.GlobalDisposed = true;
        }

        private CapturingLogger _log = null!;
        private RuntimeFlowOptions _options = null!;
        private Trace _trace = null!;

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _options = TestScope.Options(_log);
            _trace = new Trace();
        }

        [Test]
        [Timeout(10000)]
        public async Task TheScopesExistOnlyAfterStartAndTheHostIsResolvableFromBoth()
        {
            var host = new RuntimeFlowHost(
                builder => builder.RegisterInstance(_trace),
                builder => { },
                _options);

            Assert.Throws<InvalidOperationException>(() => _ = host.Global);
            Assert.Throws<InvalidOperationException>(() => _ = host.Session);
            Assert.That(host.State, Is.EqualTo(RunState.NotStarted));

            await using (host)
            {
                await host.StartAsync();

                Assert.That(host.Global.Resolve<RuntimeFlowHost>(), Is.SameAs(host));
                Assert.That(host.Session.Resolve<RuntimeFlowHost>(), Is.SameAs(host));
                Assert.That(host.Session.Parent, Is.Not.Null);
                Assert.That(host.State, Is.EqualTo(RunState.Completed));
                Assert.That(host.Generation, Is.EqualTo(0));
                Assert.That(host.RestartCount, Is.EqualTo(0));
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task GlobalIsInitializedBeforeSession()
        {
            await using var host = new RuntimeFlowHost(
                builder =>
                {
                    builder.RegisterInstance(_trace);
                    builder.Add<GlobalService>();
                },
                builder => builder.Add<SessionService>(),
                _options);

            var result = await host.StartAsync();

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(result.Scope, Is.EqualTo("session"));
            Assert.That(_trace.Entries, Is.EqualTo(new[] { "global:global", "session:session" }));
        }

        [Test]
        [Timeout(10000)]
        public async Task StatusAggregatesTheWeightsOfBothScopes()
        {
            var gate = new Gate();
            await using var host = new RuntimeFlowHost(
                builder =>
                {
                    builder.RegisterInstance(_trace);
                    builder.Add<GlobalService>();
                },
                builder => builder.RegisterInstance(gate).AsImplementedInterfaces(),
                _options);

            var startup = host.StartAsync();
            await gate.Started;

            var midRun = host.GetStatus();
            Assert.That(midRun.TotalCount, Is.EqualTo(2), midRun.ToString());
            Assert.That(midRun.CompletedCount, Is.EqualTo(1));
            Assert.That(midRun.Scope, Is.EqualTo("session"));
            Assert.That(midRun.State, Is.EqualTo(RunState.Running));
            ProgressAssertions.Percent(midRun, 25.0);

            gate.Release();
            await startup;

            var done = host.GetStatus();
            Assert.That(done.Services, Has.Count.EqualTo(2));
            Assert.That(done.Services[0].Scope, Is.EqualTo("global"));
            Assert.That(done.Services[1].Scope, Is.EqualTo("session"));
            ProgressAssertions.AllCompleted(done);
        }

        [Test]
        [Timeout(10000)]
        public async Task DescribeRendersGlobalThenSession()
        {
            await using var host = new RuntimeFlowHost(
                builder =>
                {
                    builder.RegisterInstance(_trace);
                    builder.Add<GlobalService>();
                },
                builder => builder.Add<SessionService>(),
                _options);

            Assert.That(host.Describe(), Is.EqualTo("RuntimeFlowHost: no scope has been built yet."));

            await host.StartAsync();

            var description = host.Describe();
            Assert.That(description, Does.Contain("scope 'global'"));
            Assert.That(description, Does.Contain("scope 'session'"));
            Assert.That(description.IndexOf("scope 'global'", StringComparison.Ordinal),
                Is.LessThan(description.IndexOf("scope 'session'", StringComparison.Ordinal)));
        }

        [Test]
        [Timeout(10000)]
        public async Task EntryPointsRunInsideCreateScopeBeforeTheAsyncGraph()
        {
            await using var host = new RuntimeFlowHost(
                builder => builder.RegisterInstance(_trace),
                builder =>
                {
                    builder.Register<EarlyEntryPoint>(Lifetime.Singleton).AsImplementedInterfaces();
                    builder.Add<SessionService>();
                },
                _options);

            await host.StartAsync();

            Assert.That(_trace.Entries, Is.EqualTo(new[] { "entry-point", "session:session" }));
        }

        [Test]
        [Timeout(10000)]
        public async Task AnEntryPointExceptionFailsTheRun()
        {
            await using var host = new RuntimeFlowHost(
                builder => builder.RegisterInstance(_trace),
                builder =>
                {
                    builder.Register<ThrowingEntryPoint>(Lifetime.Singleton).AsImplementedInterfaces();
                    builder.Add<SessionService>();
                },
                _options);

            var failure = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => host.StartAsync());

            Assert.That(failure.Scope, Is.EqualTo("session"));
            Assert.That(failure.Service, Is.EqualTo(nameof(ThrowingEntryPoint)));
            Assert.That(failure.Message, Does.Contain("entry point exploded"));
            Assert.That(failure.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(_trace.Entries, Is.Empty, "the async graph must not run when an entry point threw");
        }

        [Test]
        [Timeout(10000)]
        public async Task FromRunsTheForeignGlobalGraphButNeverDisposesTheContainer()
        {
            var global = TestScope.Build(builder =>
            {
                builder.RegisterInstance(_trace);
                builder.Add<GlobalService>();
                builder.Register<ForeignMarker>(Lifetime.Singleton);
            });
            global.Resolve<ForeignMarker>();

            var host = RuntimeFlowHost.From(global, builder => builder.Add<SessionService>(), _options);
            await host.StartAsync();

            Assert.That(_trace.Entries, Is.EqualTo(new[] { "global:global", "session:session" }));
            Assert.That(host.Session.Resolve<RuntimeFlowHost>(), Is.SameAs(host), "the host registers itself in the session");

            await host.DisposeAsync();

            Assert.That(_trace.GlobalDisposed, Is.False, "From() must not dispose a container it does not own");
            global.Dispose();
            Assert.That(_trace.GlobalDisposed, Is.True);
        }

        [Test]
        [Timeout(10000)]
        public async Task TheRegistryTracksLiveHostsAndForgetsDisposedOnes()
        {
            var host = new RuntimeFlowHost(builder => { }, builder => { }, _options);

            Assert.That(FlowRegistry.Live, Has.Member(host));

            await host.StartAsync();
            await host.DisposeAsync();

            Assert.That(FlowRegistry.Live, Has.No.Member(host));
        }

        [Test]
        [Timeout(10000)]
        public void ADroppedHostIsPrunedFromTheRegistryOnRead()
        {
            var weak = CreateForgottenHost();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.That(weak.TryGetTarget(out _), Is.False,
                "an undisposed host must stay collectable: the registry holds weak references and " +
                "Application.quitting is bridged through a hook, not through the host itself");
            foreach (var live in FlowRegistry.Live)
            {
                Assert.That(live, Is.Not.Null, "Live never yields a collected entry");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private WeakReference<RuntimeFlowHost> CreateForgottenHost()
            => new WeakReference<RuntimeFlowHost>(new RuntimeFlowHost(builder => { }, builder => { }, _options));

        [Test]
        [Timeout(10000)]
        public async Task ClearingTheRegistryDropsEveryEntry()
        {
            await using var host = new RuntimeFlowHost(builder => { }, builder => { }, _options);
            Assert.That(FlowRegistry.Live, Has.Member(host));

            FlowRegistry.Clear();

            Assert.That(FlowRegistry.Live, Is.Empty);
        }

        [Test]
        [Timeout(10000)]
        public async Task QuittingCancelsTheRunAndRefusesRestarts()
        {
            var gate = new Gate();
            await using var host = new RuntimeFlowHost(
                builder => builder.RegisterInstance(_trace),
                builder => builder.RegisterInstance(gate).AsImplementedInterfaces(),
                _options);

            var startup = host.StartAsync();
            await gate.Started;

            host.OnQuitting();
            Assert.That(host.IsQuitting, Is.True);

            await AsyncTestAssert.ThrowsAsync<OperationCanceledException>(() => startup);

            var refused = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(
                () => host.RestartAsync("after-quit"));
            Assert.That(refused.Message, Does.Contain("after-quit"));
            Assert.That(host.RestartCount, Is.EqualTo(0));
            Assert.That(_log.Has(Microsoft.Extensions.Logging.LogLevel.Warning,
                "restart 'after-quit' refused: the application is quitting."), Is.True, _log.Dump());
        }
    }
}
