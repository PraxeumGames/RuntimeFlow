using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using RuntimeFlow.Demo.Midcore;
using RuntimeFlow.Flow;

namespace RuntimeFlow.Demo.Midcore
{
    /// <summary>
    /// THE midcore-game loading flow expressed on the golden path.
    ///
    /// Global scope: remote config + auth (loaded once per app launch, stay warm across restarts).
    /// Session scope: server profile + catalog + game services (rebuilt per restart).
    /// Entry scene: preloader (auto-loaded after boot).
    /// </summary>
    public static class MidcoreGame
    {
        public static FakeBackend Backend { get; } = new();

        public static GameFlowBuilder Define() => GameFlow.Create()
            .DeterministicScheduler()
            .Advanced(b =>
            {
                b.Global().RegisterInstance(Backend);
                b.Session().RegisterInstance(Backend);
            })
            .Config<RemoteConfigContent, RemoteConfigData>()
            .Auth<AuthContent, AuthData>()
            .Profile<ProfileSource, ProfileData>()
            .Catalog<CatalogSource, CatalogData>()
            .Service<AnalyticsService>()
            .Service<AdsService>()
            .Service<IapRestoreService>()
            .Service<EconomyService>()
            .Service<InventoryService>()
            .LoadingUi<QuestBoardService>()
            .Scene<PreloaderScene>()
            .Entry<PreloaderScene>()
            .Scene<MetaScene>();

        /// <summary>Navigates from entry (preloader) to meta — game-driven hop after loading.</summary>
        public static Task NavigateToMetaAsync(this GameHandle game, CancellationToken ct = default)
            => game.LoadSceneAsync<MetaScene>(ct);
    }
}
