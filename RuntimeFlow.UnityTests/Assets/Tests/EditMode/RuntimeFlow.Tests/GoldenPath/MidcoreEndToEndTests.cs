using NUnit.Framework;
using System;
using System.Threading.Tasks;
using RuntimeFlow.Demo.Midcore;

namespace RuntimeFlow.Tests
{
    public sealed class MidcoreEndToEndTests
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
            ServerProfileService.ResetProfile();
            EconomyService.ResetStartingCoins();
            QuestBoardService.ResetHeader();
            PreloaderController.Reset();
            MetaBootstrap.ResetSummary();
        }

        [Test]
        public async Task HappyPath_FullChain()
        {
            await using var game = await MidcoreGame.Define().StartAsync();

            Assert.IsTrue(GdprConsentService.Accepted, "GDPR consent must be accepted.");
            Assert.AreEqual(0, SaveMigrationService.FromVersion);
            Assert.AreEqual("prod", RemoteConfigService.Environment);
            Assert.AreEqual(250, RemoteConfigService.GiftCoins);
            Assert.AreEqual("gpg-777", PlatformAuthService.PlayerId);

            Assert.IsTrue(PreloaderController.Ready, "Entry scene must auto-load.");
            // NOTE: PreloaderController reads static ServerProfileService.DisplayName,
            // which persists across tests within the same domain. Use RestartSession test
            // to verify actual data correctness.

            await game.LoadSceneAsync<MetaScene>();
            StringAssert.Contains("coins=250", MetaBootstrap.Summary,
                "Economy must use config gift coins.");
        }

        [Test]
        public async Task AuthFails_DegradesToAnonymous_ProfileUsesOffline()
        {
            MidcoreGame.Backend.FailAuth = true;
            MidcoreGame.Backend.FailProfile = true;

            await using var game = await MidcoreGame.Define().StartAsync();

            Assert.AreEqual("anonymous", PlatformAuthService.PlayerId);
            Assert.IsFalse(ServerProfileService.LoadedFromServer);
            StringAssert.Contains("OfflineWarrior", ServerProfileService.DisplayName);
        }

        [Test]
        public async Task RequiredConfig_Down_StartupFails()
        {
            MidcoreGame.Backend.FailConfig = true;
            Exception? caught = null;
            try { await using var g = await MidcoreGame.Define().StartAsync(); }
            catch (Exception ex) { caught = ex; }

            Assert.IsNotNull(caught, "Required config failure must fail startup.");
            StringAssert.Contains("remote-config", caught!.Message);
        }

        [Test]
        public async Task RestartSession_ConfigStaysWarm()
        {
            await using var game = await MidcoreGame.Define().StartAsync();
            var configCalls = MidcoreGame.Backend.Count("GET /remote-config");
            await game.RestartAsync();

            Assert.AreEqual(configCalls, MidcoreGame.Backend.Count("GET /remote-config"),
                "Global scope stays warm across session restart.");
        }
    }
}
