using System.Threading;
using RuntimeFlow.Contexts;
using RuntimeFlow.Demo.Midcore;
using RuntimeFlow.Flow;

namespace RuntimeFlow.Demo.Midcore
{
    public static class MidcoreGame
    {
        public static FakeBackend Backend { get; } = new();

        public static GameFlowBuilder Define() => GameFlow.Create()
            .DeterministicScheduler()
            .Advanced(b =>
            {
                b.Global().RegisterInstance(Backend);
                b.Global().Register<GdprConsentService>(DiLifetime.Singleton);
                b.Global().Register<SaveMigrationService>(DiLifetime.Singleton);
                b.Global().Register<RemoteConfigService>(DiLifetime.Singleton);
                b.Global().Register<PlatformAuthService>(DiLifetime.Singleton);
                b.Session().RegisterInstance(Backend);
                b.Session().Register<ServerProfileService>(DiLifetime.Singleton);
                b.Session().Register<EconomyService>(DiLifetime.Singleton);
                b.Session().Register<QuestBoardService>(DiLifetime.Singleton);
            })
            .Scene<PreloaderScene>()
            .Entry<PreloaderScene>()
            .Scene<MetaScene>();
    }
}
