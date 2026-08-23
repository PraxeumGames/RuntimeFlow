using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Content;
using RuntimeFlow.Contexts;
using RuntimeFlow.Flow;
using RuntimeFlow.Testing;

namespace RuntimeFlow.Tests
{
    /// <summary>
    /// Viability demo for the golden path on a midcore-game loading flow:
    /// remote config (global, required) → Google Play Games sign-in (session Platform stage,
    /// user-gated dialog marker, optional with anonymous fallback) → server profile chained
    /// after auth → Addressables-style catalog (Content stage, offline-safe) → loading UI →
    /// gameplay scene bootstrap. Covers the degraded branch and warm session restart
    /// (global content is NOT refetched; session content is).
    /// </summary>
    public sealed class MidcoreFlowDemoTests
    {
        // ---------- snapshots ----------
        public sealed class RemoteConfigSnapshot { public string Environment { get; set; } = "dev"; public int DailyGiftCoins { get; set; } }
        public sealed class AuthSnapshot { public string PlayerId { get; set; } = "anonymous"; public bool SignedIn { get; set; } }
        public sealed class ServerProfileSnapshot { public string DisplayName { get; set; } = "Guest"; public int Level { get; set; } }
        public sealed class CatalogSnapshot { public List<string> Bundles { get; } = new(); }

        // ---------- sources (game code; one small class per concern) ----------
        private sealed class RemoteConfigSource : ContentSource<RemoteConfigSnapshot>, IGlobalInitializableService
        {
            public static int LoadCount;

            public override string SourceName => "remote-config";

            protected override async Task<RemoteConfigSnapshot> LoadAsync(CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref LoadCount);
                await Task.Delay(1, cancellationToken);
                return new RemoteConfigSnapshot { Environment = "prod", DailyGiftCoins = 250 };
            }
        }

        private sealed class PlayGamesAuthSource : ContentSource<AuthSnapshot>,
            ISessionInitializableService, IPlatformStartupInitializableService, IUserInteractionGatedInitializableService
        {
            public PlayGamesAuthSource() => Policy(optional: true, fallback: new AuthSnapshot());
            public static bool ServerUnreachable;

            public override string SourceName => "play-games-auth";

            protected override Task<AuthSnapshot> LoadAsync(CancellationToken cancellationToken)
            {
                if (ServerUnreachable) throw new InvalidOperationException("play services unreachable");
                return Task.FromResult(new AuthSnapshot { PlayerId = "gpg-777", SignedIn = true });
            }
        }

        [DependsOn(typeof(PlayGamesAuthSource))]
        private sealed class ServerProfileSource : ContentSource<ServerProfileSnapshot>,
            ISessionInitializableService, IContentStartupInitializableService
        {
            private readonly IContentSource<AuthSnapshot> _auth;

            public static int LoadCount;

            public ServerProfileSource(IContentSource<AuthSnapshot> auth)
            {
                _auth = auth;
                Policy(optional: true, fallback: new ServerProfileSnapshot { DisplayName = "OfflineWarrior", Level = 1 });
            }

            public override string SourceName => "server-profile";

            protected override async Task<ServerProfileSnapshot> LoadAsync(CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref LoadCount);
                await Task.Delay(1, cancellationToken);
                if (!_auth.Data.SignedIn)
                    throw new InvalidOperationException("server rejects anonymous players (401)");
                return new ServerProfileSnapshot { DisplayName = $"Hero_{_auth.Data.PlayerId}", Level = 42 };
            }
        }

        private sealed class CatalogSource : ContentSource<CatalogSnapshot>,
            ISessionInitializableService, IContentStartupInitializableService
        {
            public CatalogSource() => Policy(optional: true, fallback: new CatalogSnapshot());
            public static bool OfflineMode;

            public override string SourceName => "addressables-catalog";
            private readonly bool _optional = true;                    // offline-safe

            protected override Task<CatalogSnapshot> LoadAsync(CancellationToken cancellationToken)
            {
                if (OfflineMode) throw new InvalidOperationException("no connection");
                var snapshot = new CatalogSnapshot();
                snapshot.Bundles.Add("heroes.base");
                snapshot.Bundles.Add("world.biome01");
                return Task.FromResult(snapshot);
            }
        }

        private sealed class LoadingUi : ISessionInitializableService, IUiStartupInitializableService
        {
            public static readonly List<string> Timeline = new();

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                Timeline.Add("ui");
                return Task.CompletedTask;
            }
        }

        // ---------- gameplay scene ----------
        private sealed class GameplayScene : ISceneScope
        {
            public void Configure(IGameScopeRegistrationBuilder builder)
            {
                builder.Register<GameplayBootstrap>(DiLifetime.Singleton);
            }
        }

        [DependsOn(typeof(ServerProfileSource))]
        private sealed class GameplayBootstrap : ISceneInitializableService
        {
            private readonly IContentSource<ServerProfileSnapshot> _profile;
            private readonly IContentSource<CatalogSnapshot> _catalog;

            public GameplayBootstrap(
                IContentSource<ServerProfileSnapshot> profile,
                IContentSource<CatalogSnapshot> catalog)
            {
                _profile = profile;
                _catalog = catalog;
            }

            public string Summary { get; private set; } = "";

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                LoadingUi.Timeline.Add("scene-bootstrap");
                Summary = $"{_profile.Data.DisplayName} lvl{_profile.Data.Level} bundles={_catalog.Data.Bundles.Count}";
                return Task.CompletedTask;
            }
        }

        private static GameFlowBuilder DemoFlow() => GameFlow.Create()
            .Config<RemoteConfigSource, RemoteConfigSnapshot>()
            .Auth<PlayGamesAuthSource, AuthSnapshot>()
            .Profile<ServerProfileSource, ServerProfileSnapshot>()
            .Catalog<CatalogSource, CatalogSnapshot>()
            .LoadingUi<LoadingUi>()
            .Scene<GameplayScene>();

        [SetUp]
        public void LogStart()
            => UnityEngine.Debug.LogWarning($"[t] START {TestContext.CurrentContext.Test.Name}");

        [TearDown]
        public void LogEnd()
            => UnityEngine.Debug.LogWarning($"[t] END {TestContext.CurrentContext.Test.Name}: {TestContext.CurrentContext.Result.Outcome}");

        [SetUp]
        public void ResetStatics()
        {
            RemoteConfigSource.LoadCount = 0;
            ServerProfileSource.LoadCount = 0;
            PlayGamesAuthSource.ServerUnreachable = false;
            CatalogSource.OfflineMode = false;
            LoadingUi.Timeline.Clear();
        }

        [Test]
        public async Task HappyPath_ChainsAuthIntoProfile_AndBootsSceneWithEverything()
        {
            await using var game = await DemoFlow().StartAsync();

            Assert.AreEqual("prod", game.Get<IContentSource<RemoteConfigSnapshot>>().Data.Environment);
            Assert.AreEqual("gpg-777", game.Get<IContentSource<AuthSnapshot>>().Data.PlayerId);

            await game.LoadSceneAsync<GameplayScene>();

            var bootstrap = game.SceneContext!.Resolve<GameplayBootstrap>();
            Assert.AreEqual("Hero_gpg-777 lvl42 bundles=2", bootstrap.Summary);
            Assert.That(LoadingUi.Timeline, Does.Contain("ui").And.Contain("scene-bootstrap"));
            Assert.That(LoadingUi.Timeline.IndexOf("ui"), Is.LessThan(LoadingUi.Timeline.IndexOf("scene-bootstrap")),
                "Loading UI must initialize before the scene bootstrap.");
        }

        [Test]
        public async Task DegradedPath_AuthAndCatalogDown_GameStartsWithFallbacks()
        {
            PlayGamesAuthSource.ServerUnreachable = true;
            CatalogSource.OfflineMode = true;

            await using var game = await DemoFlow().StartAsync();
            await game.LoadSceneAsync<GameplayScene>();

            var auth = game.Get<IContentSource<AuthSnapshot>>();
            var catalog = game.SceneContext!.Resolve<IContentSource<CatalogSnapshot>>();
            Assert.IsTrue(auth.UsedFallback);
            Assert.AreEqual("anonymous", auth.Data.PlayerId);
            Assert.IsTrue(catalog.UsedFallback);

            var bootstrap = game.SceneContext.Resolve<GameplayBootstrap>();
            Assert.AreEqual("OfflineWarrior lvl1 bundles=0", bootstrap.Summary);
        }

        [Test]
        public async Task RestartSession_GlobalStaysWarm_SessionRebuilds()
        {
            await using var game = await DemoFlow().StartAsync();
            await game.LoadSceneAsync<GameplayScene>();
            var configLoadsAfterStart = RemoteConfigSource.LoadCount;
            var profileLoadsAfterStart = ServerProfileSource.LoadCount;
            LoadingUi.Timeline.Clear();

            await game.RestartAsync();

            Assert.AreEqual(configLoadsAfterStart, RemoteConfigSource.LoadCount,
                "Global content must stay warm across a session restart.");
            Assert.AreEqual(profileLoadsAfterStart + 1, ServerProfileSource.LoadCount,
                "Session content must reload on restart.");

            await game.LoadSceneAsync<GameplayScene>();
            var bootstrap = game.SceneContext!.Resolve<GameplayBootstrap>();
            Assert.AreEqual("Hero_gpg-777 lvl42 bundles=2", bootstrap.Summary);
            CollectionAssert.Contains(LoadingUi.Timeline, "ui");
        }

        [Test]
        public async Task GoldenPath_DelegateConfig_ServesDataThroughFlowLoadContext()
        {
            await using var game = await GameFlow.Create().DeterministicScheduler()
                .Advanced(b => b.Global().Register<IConfigDependency, ConfigDependency>(DiLifetime.Singleton))
                .Config("remote-config",
                    load: async (flow, ct) =>
                    {
                        var extra = flow.Get<IConfigDependency>().Tag;
                        await Task.Delay(1, ct);
                        return new RemoteConfigSnapshot { Environment = "env:" + extra };
                    },
                    policy: ContentPolicy<RemoteConfigSnapshot>.Required())
                .StartAsync();

            Assert.AreEqual("env:dep", game.Get<IContentSource<RemoteConfigSnapshot>>().Data.Environment);
        }

        private interface IConfigDependency { string Tag { get; } }

        private sealed class ConfigDependency : IConfigDependency
        {
            public string Tag => "dep";
        }
    }
}
