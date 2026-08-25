using NUnit.Framework;
using System;
using System.Threading.Tasks;
using RuntimeFlow.Demo.Midcore;
using RuntimeFlow.Flow;

namespace RuntimeFlow.Tests
{
    public sealed class EntryRoutingTests
    {
        [SetUp]
        public void Reset()
        {
            MidcoreGame.Backend.Reset();
            PersistentState.ResetAll();
            GdprConsentService.ResetAccepted();
            SaveMigrationService.ResetMigration();
            RemoteConfigService.ResetConfig();
            PlatformAuthService.ResetAuth();
            PreloaderController.Reset();
            TutorialController.Reset();
            SessionRejoinService.Reset();
        }

        private static GameFlowBuilder RoutingFlow()
        {
            return GameFlow.Create().DeterministicScheduler()
                .Advanced(b =>
                {
                    b.Global().RegisterInstance(MidcoreGame.Backend);
                    b.Global().Register<GdprConsentService>(DiLifetime.Singleton);
                    b.Global().Register<SaveMigrationService>(DiLifetime.Singleton);
                    b.Global().Register<RemoteConfigService>(DiLifetime.Singleton);
                    b.Global().Register<PlatformAuthService>(DiLifetime.Singleton);
                    b.Session().RegisterInstance(MidcoreGame.Backend);
                })
                .Scene<TutorialScene>()
                .Scene<SessionRejoinScene>()
                .Scene<MetaScene>()
                .ResolveEntryWith<MidcoreEntryRouteResolver>();
        }

        [Test]
        public async Task NewPlayer_RoutesToTutorial()
        {
            MidcoreEntryRouteResolver.Scenario = "new-player";
            await using var game = await RoutingFlow().StartAsync();
            Assert.IsTrue(TutorialController.Completed, "New player must route to tutorial.");
        }

        [Test]
        public async Task SessionRejoin_RoutesToRejoin()
        {
            MidcoreEntryRouteResolver.Scenario = "session-rejoin";
            await using var game = await RoutingFlow().StartAsync();
            Assert.IsTrue(SessionRejoinService.Rejoined, "Session rejoin must load rejoin scene.");
        }

        [Test]
        public async Task Default_RoutesToMeta()
        {
            MidcoreEntryRouteResolver.Scenario = "default";
            await using var game = await RoutingFlow().StartAsync();
            Assert.IsNotNull(MetaBootstrap.Summary, "Default routing must land in meta.");
        }
    }
}
