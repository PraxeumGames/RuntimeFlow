using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Content;
using RuntimeFlow.Contexts;
using RuntimeFlow.Flow;
using RuntimeFlow.Demo.Midcore;

namespace RuntimeFlow.Tests
{
    /// <summary>
    /// Golden-path graph semantics: implicit constructor data-flow edges, stage validation,
    /// startup-plan inspection, and entry-scene auto-load.
    /// </summary>
    public sealed class GameFlowGraphTests
    {
        public sealed class ConfigSnapshot { public string Value { get; set; } = "cfg"; }
        public sealed class ProfileSnapshot { public string Owner { get; set; } = "none"; }
        public sealed class CatalogSnapshot { public List<string> Bundles { get; } = new(); }

        private sealed class ConfigSource : ContentSource<ConfigSnapshot>, IGlobalInitializableService
        {
            public static int LoadCount;
            public override string SourceName => "config";
            protected override Task<ConfigSnapshot> LoadAsync(CancellationToken ct)
            {
                Interlocked.Increment(ref LoadCount);
                return Task.FromResult(new ConfigSnapshot { Value = "prod" });
            }
        }

        // NO [DependsOn]: the IContentSource<ConfigSnapshot> constructor parameter IS the edge.
        private sealed class ChainedProfileSource : ContentSource<ProfileSnapshot>,
            ISessionInitializableService, IContentStartupInitializableService
        {
            private readonly IContentSource<ConfigSnapshot> _config;

            public static int LoadCount;
            public static readonly List<string> Order = new();

            public ChainedProfileSource(IContentSource<ConfigSnapshot> config) => _config = config;

            public override string SourceName => "profile";

            protected override async Task<ProfileSnapshot> LoadAsync(CancellationToken ct)
            {
                Interlocked.Increment(ref LoadCount);
                await Task.Yield();
                Order.Add("profile-loaded:" + _config.Data.Value);
                return new ProfileSnapshot { Owner = "owner-" + _config.Data.Value };
            }
        }

        [Test]
        public async Task ImplicitConstructorEdge_ChainsProfileAfterConfig_DataFlows()
        {
            ConfigSource.LoadCount = 0;
            ChainedProfileSource.LoadCount = 0;
            ChainedProfileSource.Order.Clear();

            await using var game = await GameFlow.Create().DeterministicScheduler()
                .Config<ConfigSource, ConfigSnapshot>()
                .Profile<ChainedProfileSource, ProfileSnapshot>()
                .StartAsync();

            Assert.Greater(ChainedProfileSource.LoadCount, 0);
            CollectionAssert.Contains(ChainedProfileSource.Order, "profile-loaded:prod");
        }

        [Test]
        public void DescribeStartupPlan_ListsNodesEdgesAndOrder()
        {
            var builder = GameFlow.Create()
                .Config<ConfigSource, ConfigSnapshot>()
                .Profile<ChainedProfileSource, ProfileSnapshot>();

            var plan = builder.DescribeStartupPlan();

            Assert.AreEqual(2, plan.Count);
            Assert.AreEqual(GameContextType.Global, plan[0].Scope);
            Assert.AreEqual("ConfigSource", plan[0].SourceName);
            Assert.IsTrue(plan[0].Required);

            Assert.AreEqual(GameContextType.Session, plan[1].Scope);
            Assert.AreEqual("ChainedProfileSource", plan[1].SourceName);
            Assert.That(plan[1].DependsOn, Does.Contain("ConfigSource"),
                "The implicit constructor data-flow edge must appear in the plan.");
        }

        [Test]
        public void DescribeStartupPlan_UnregisteredContentDependency_ThrowsLoudly()
        {
            var builder = GameFlow.Create()
                .Profile<OrphanProfileSource, ProfileSnapshot>();

            Assert.Throws<InvalidOperationException>(() => builder.DescribeStartupPlan(),
                "A closed IContentSource<T> constructor dependency without a registered source must fail at plan time.");
        }

        private sealed class OrphanProfileSource : ContentSource<ProfileSnapshot>,
            ISessionInitializableService, IContentStartupInitializableService
        {
            public OrphanProfileSource(IContentSource<ConfigSnapshot> missing) { }

            public override string SourceName => "orphan-profile";

            protected override Task<ProfileSnapshot> LoadAsync(CancellationToken ct)
                => Task.FromResult(new ProfileSnapshot());
        }

        private sealed class NotPlatformAuthSource : ContentSource<AuthSnapshotStub>
        {
            public override string SourceName => "auth";
            protected override Task<AuthSnapshotStub> LoadAsync(CancellationToken ct)
                => Task.FromResult(new AuthSnapshotStub());
        }

        public sealed class AuthSnapshotStub { }

        private sealed class PreloaderScene : ISceneScope
        {
            public void Configure(IGameScopeRegistrationBuilder builder)
            {
                builder.Register<PreloaderService>(DiLifetime.Singleton);
            }
        }

        private sealed class PreloaderService : ISceneInitializableService
        {
            public static int InitCount;
            public Task InitializeAsync(CancellationToken ct) { Interlocked.Increment(ref InitCount); return Task.CompletedTask; }
        }

        [Test]
        public async Task EntryScene_AutoLoadsRightAfterBoot()
        {
            PreloaderService.InitCount = 0;

            await using var game = await GameFlow.Create().DeterministicScheduler()
                .Scene<PreloaderScene>()
                .Entry<PreloaderScene>()
                .StartAsync();

            Assert.AreEqual(1, PreloaderService.InitCount,
                "The entry scene must load automatically as part of StartAsync.");
            Assert.IsNotNull(game.SceneContext);
        }

        [Test]
        public async Task DelegateContent_OptionalPolicy_DegradesToFallback()
        {
            await using var game = await GameFlow.Create().DeterministicScheduler()
                .Catalog<CatalogSnapshot>(
                    sourceName: "catalog",
                    load: (_, _) => throw new InvalidOperationException("offline"),
                    policy: ContentPolicy<CatalogSnapshot>.Optional(new CatalogSnapshot()))
                .StartAsync();

            var catalog = game.Get<IContentSource<CatalogSnapshot>>();
            Assert.IsTrue(catalog.UsedFallback);
            Assert.AreEqual(0, catalog.Data.Bundles.Count);
        }
    }
}
