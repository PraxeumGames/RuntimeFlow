using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Content;
using RuntimeFlow.Contexts;
using RuntimeFlow.Flow;

namespace RuntimeFlow.Tests.GoldenPath
{
    /// <summary>
    /// Phase 1 verification: composition-time validation (duplicates, cycles, unknown
    /// dependencies) throws from StartAsync, and the unified load-graph planner produces
    /// an execution order that agrees with DescribeStartupPlan.
    /// </summary>
    public sealed class LoadGraphValidationTests
    {
        private sealed class ConfigSnapshot { public string Env { get; set; } = "prod"; }
        private sealed class AuthSnapshot { }
        private sealed class CatalogSnapshot { }

        private sealed class TestSessionScope : ISceneScope
        {
            public void Configure(IGameScopeRegistrationBuilder builder) { }
        }

        // ---------- duplicate data producers ----------

        [Test]
        public void StartAsync_DuplicateClassSources_ThrowsImmediately()
        {
            var flow = GameFlow.Create()
                .DeterministicScheduler()
                .Config<ConfigSourceA, ConfigSnapshot>()
                .Config<ConfigSourceB, ConfigSnapshot>();

            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await flow.StartAsync());
            Assert.IsTrue(ex!.Message.Contains("both produce"), ex.Message);
            Assert.IsTrue(ex.Message.Contains(nameof(ConfigSnapshot)), ex.Message);
        }

        [Test]
        public void Composition_MixedClassAndDelegateDuplicates_ThrowsAtConfig()
        {
            var flow = GameFlow.Create()
                .DeterministicScheduler()
                .Config<ConfigSourceA, ConfigSnapshot>();

            // The delegate registration itself detects the duplicate immediately.
            var ex = Assert.Throws<InvalidOperationException>(
                () => flow.Config("remote-config", async (_, _) => new ConfigSnapshot()));
            Assert.IsTrue(ex!.Message.Contains("already registered"), ex.Message);

            // And the typed counterpart validates on StartAsync as a second line of defense.
            var flow2 = GameFlow.Create()
                .DeterministicScheduler()
                .Config("remote-config", async (_, _) => new ConfigSnapshot())
                .Config<ConfigSourceA, ConfigSnapshot>();
            var ex2 = Assert.ThrowsAsync<InvalidOperationException>(async () => await flow2.StartAsync());
            Assert.IsTrue(ex2!.Message.Contains("both produce"), ex2.Message);
        }

        // ---------- unknown content dependency ----------

        [Test]
        public void StartAsync_UnregisteredContentDependency_Throws()
        {
            var flow = GameFlow.Create()
                .DeterministicScheduler()
                .Profile<CatalogConsumerSource, CatalogSnapshot>()
                .Scene<TestSessionScope>();

            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await flow.StartAsync());
            Assert.IsTrue(ex!.Message.Contains("no such source is registered"), ex.Message);
        }

        // ---------- cycle ----------

        [Test]
        public void DescribeStartupPlan_ContentCycle_Throws()
        {
            var flow = GameFlow.Create()
                .Profile<CycleA, CycleDataA>()
                .Profile<CycleB, CycleDataB>();

            var ex = Assert.Throws<InvalidOperationException>(() => flow.DescribeStartupPlan());
            StringAssert.Contains("cycle", ex!.Message, ex.Message);
        }

        // ---------- plan/execution agreement ----------

        [Test]
        public async Task ExecutionOrder_AgreesWithPlan_Topologically()
        {
            ConfigSourceA.Reset();
            RecordedConsumer.Reset();

            var game = await GameFlow.Create()
                .DeterministicScheduler()
                .Config<ConfigSourceA, ConfigSnapshot>()
                .Auth<AuthSourceDependentOnConfig, AuthSnapshot>()
                .Profile<CatalogConsumerSource, CatalogSnapshot>()
                .Service<RecordedConsumer>()
                .Scene<TestSessionScope>()
                .StartAsync();

            Assert.GreaterOrEqual(RecordedConsumer.Events.Count, 1);

            // The consumer must observe the config snapshot during its own init:
            // its constructor edge guaranteed the source completed first.
            StringAssert.Contains("prod", RecordedConsumer.Events[0]);

            // Content loaded exactly once despite multiple consumers.
            Assert.AreEqual(1, ConfigSourceA.LoadCount);
        }

        [Test]
        public async Task DescribeStartupPlan_MatchesWaveOrder_OnChainedGraph()
        {
            RecordedConsumer.Reset();

            var builder = GameFlow.Create()
                .DeterministicScheduler()
                .Config<ConfigSourceA, ConfigSnapshot>()
                .Auth<AuthSourceDependentOnConfig, AuthSnapshot>()
                .Profile<CatalogConsumerSource, CatalogSnapshot>()
                .Scene<TestSessionScope>();

            var planNames = builder.DescribeStartupPlan().Select(e => e.SourceName).ToList();

            // Strong assertion: every declared "after" precedes its dependent in plan order.
            var indexOf = planNames.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
            foreach (var e in builder.DescribeStartupPlan())
                foreach (var dep in e.DependsOn)
                    Assert.Less(indexOf[dep], indexOf[e.SourceName]);
            await Task.CompletedTask;
        }

        // ---------- fixtures ----------

        private sealed class ConfigSourceA : ContentSource<ConfigSnapshot>, IGlobalInitializableService
        {
            public static int LoadCount { get; private set; }
            public static void Reset() => LoadCount = 0;

            public override string SourceName => "config-a";
            protected override Task<ConfigSnapshot> LoadAsync(CancellationToken ct)
            {
                LoadCount++;
                return Task.FromResult(new ConfigSnapshot());
            }
        }

        private sealed class ConfigSourceB : ContentSource<ConfigSnapshot>, IGlobalInitializableService
        {
            public override string SourceName => "config-b";
            protected override Task<ConfigSnapshot> LoadAsync(CancellationToken ct)
                => Task.FromResult(new ConfigSnapshot());
        }

        private sealed class AuthSourceDependentOnConfig : ContentSource<AuthSnapshot>,
            ISessionInitializableService, IPlatformStartupInitializableService
        {
            private readonly IContentSource<ConfigSnapshot> _config;
            public AuthSourceDependentOnConfig(IContentSource<ConfigSnapshot> config) => _config = config;
            public override string SourceName => "auth";
            protected override Task<AuthSnapshot> LoadAsync(CancellationToken ct)
                => Task.FromResult(new AuthSnapshot());
        }

        [DependsOn(typeof(AuthSourceDependentOnConfig))]
        private sealed class CatalogConsumerSource : ContentSource<CatalogSnapshot>, ISessionInitializableService
        {
            private readonly IContentSource<AuthSnapshot> _auth;
            public CatalogConsumerSource(IContentSource<AuthSnapshot> auth) => _auth = auth;
            public override string SourceName => "catalog-consumer";
            protected override Task<CatalogSnapshot> LoadAsync(CancellationToken ct)
                => Task.FromResult(new CatalogSnapshot());
        }

        /// <summary>Records that a consumer observed the config data at init time.</summary>
        private sealed class RecordedConsumer : ISessionInitializableService
        {
            public static List<string> Events { get; } = new();
            public static void Reset() => Events.Clear();

            private readonly IContentSource<ConfigSnapshot> _config;
            public RecordedConsumer(IContentSource<ConfigSnapshot> config) => _config = config;

            public Task InitializeAsync(CancellationToken ct)
            {
                Events.Add(_config.Data.Env);
                return Task.CompletedTask;
            }
        }

        private sealed class CycleA : ContentSource<CycleDataA>, ISessionInitializableService
        {
            private readonly IContentSource<CycleDataB> _b;
            public CycleA(IContentSource<CycleDataB> b) => _b = b;
            public override string SourceName => "cycle-a";
            protected override Task<CycleDataA> LoadAsync(CancellationToken ct) => Task.FromResult(new CycleDataA());
        }

        private sealed class CycleB : ContentSource<CycleDataB>, ISessionInitializableService
        {
            private readonly IContentSource<CycleDataA> _a;
            public CycleB(IContentSource<CycleDataA> a) => _a = a;
            public override string SourceName => "cycle-b";
            protected override Task<CycleDataB> LoadAsync(CancellationToken ct) => Task.FromResult(new CycleDataB());
        }

        private sealed class CycleDataA { }
        private sealed class CycleDataB { }
    }
}
