using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Demo;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Demo
{
    /// <summary>
    /// The demo game is the framework's end-to-end fixture: the same two installers the scene uses are
    /// started headlessly, one <see cref="ChaosToggles"/> flag at a time, so every failure semantic —
    /// required failure, optional degradation, timeout, stall, halt and the user gate — is pinned to the
    /// exact service name and message the dashboard is supposed to show.
    /// </summary>
    [TestFixture]
    public sealed class DemoFlowTests
    {
        private FakeBackend _backend = null!;
        private ChaosToggles _chaos = null!;
        private CapturingLogger _log = null!;

        [SetUp]
        public void SetUp()
        {
            _backend = new FakeBackend();
            _chaos = new ChaosToggles { GdprAlreadyAccepted = true };
            _log = new CapturingLogger();
        }

        /// <summary>The demo installers behind the headless harness, with the demo's phases applied.</summary>
        private TestFlow Flow()
            => TestFlow
                .Create(
                    builder => DemoGame.ConfigureGlobal(builder, _backend, _chaos),
                    builder => DemoGame.ConfigureSession(builder, _backend, _chaos))
                .Configure(options => options.Phases = DemoGame.Phases);

        /// <summary>A host built exactly the way the scene builds it, for the tests that need it mid-run.</summary>
        private RuntimeFlowHost Host(Action<RuntimeFlowOptions>? configure = null)
        {
            var options = TestScope.Options(_log);
            configure?.Invoke(options);
            return DemoGame.CreateHost(_backend, _chaos, options);
        }

        [Test]
        [Timeout(10000)]
        public async Task TheHappyPathCompletesEveryServiceAndReachesAHundredPercent()
        {
            await using var app = await Flow().StartAsync();

            var profile = app.Resolve<PlayerProfileService>().Profile;
            var catalog = app.Resolve<CatalogService>();
            var warmup = app.Resolve<QuestWarmupService>();
            var status = app.Host.GetStatus();

            Assert.That(app.Result!.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(status.Names(ServiceState.Completed), Is.EquivalentTo(new[]
            {
                "RemoteConfigService", "PlatformAuthService", "GdprConsentService",
                "MaintenanceGateService", "PlayerProfileService", "CatalogService", "QuestWarmupService"
            }));
            Assert.That(status.Percent, Is.EqualTo(100.0).Within(0.001));

            Assert.That(profile, Is.Not.Null);
            Assert.That(profile!.PlayerId, Is.EqualTo("gpg-777"), "the profile carries the id the auth service resolved");
            Assert.That(profile.DisplayName, Is.EqualTo("Hero_gpg-777"));
            Assert.That(profile.Coins, Is.EqualTo(350), "100 stored coins plus the 250 gift coins from the config");
            Assert.That(catalog.RequestedVersion, Is.EqualTo("2026.09.1"), "the catalog asks for the configured revision");
            Assert.That(catalog.Catalog!.Version, Is.EqualTo("2026.09.1"));
            Assert.That(warmup.WarmedSteps, Is.EqualTo(4));
            Assert.That(warmup.Quests, Is.Not.Null);
        }

        [Test]
        [Timeout(10000)]
        public async Task AnAuthOutageDegradesTheOptionalServiceAndYieldsAnAnonymousProfile()
        {
            _backend.Fail(FakeBackend.Endpoints.Auth);

            await using var app = await Flow().StartAsync();

            var auth = app.Resolve<PlatformAuthService>();
            var profile = app.Resolve<PlayerProfileService>().Profile;
            var status = app.Host.GetStatus();

            Assert.That(app.Result!.Outcome, Is.EqualTo(StartupOutcome.Completed), "an optional failure does not stop the run");
            Assert.That(status.Service("PlatformAuthService").State, Is.EqualTo(ServiceState.Degraded));
            Assert.That(auth.IsAnonymous, Is.True);
            Assert.That(profile!.PlayerId, Is.EqualTo(PlatformAuthService.AnonymousPlayerId));
            Assert.That(profile.DisplayName, Is.EqualTo("Guest"));

            // StartupResult.Degraded is per scope run and StartAsync returns the session's result, so a
            // global service that degraded shows up in the merged status snapshot, not in that result.
            Assert.That(app.Result.Degraded, Is.Empty);
            Assert.That(status.Names(ServiceState.Degraded), Is.EqualTo(new[] { "PlatformAuthService" }));
        }

        [Test]
        [Timeout(10000)]
        public async Task AConfigOutageFailsTheGlobalScopeAndNamesTheService()
        {
            _backend.Fail(FakeBackend.Endpoints.Config);
            var flow = Flow();

            var failure = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => flow.StartAsync());

            Assert.That(failure.Scope, Is.EqualTo("global"));
            Assert.That(failure.Service, Is.EqualTo("RemoteConfigService"));
            Assert.That(failure.Message, Does.Contain("RemoteConfigService threw InvalidOperationException"));
            Assert.That(failure.InnerException!.Message, Does.Contain("'/config' is unreachable"));
            await flow.DisposeAsync();
        }

        [Test]
        [Timeout(10000)]
        public async Task ARestartKeepsTheGlobalScopeWarmAndRebuildsTheSession()
        {
            var observer = new CollectingObserver();
            await using var app = await Flow().ObserveWith(observer).StartAsync();

            var config = app.Resolve<RemoteConfigService>();
            var profileBefore = app.Resolve<PlayerProfileService>();

            var result = await app.Host.RestartAsync("test-restart");

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(app.Resolve<RemoteConfigService>(), Is.SameAs(config), "the global scope is not rebuilt");
            Assert.That(app.Resolve<PlayerProfileService>(), Is.Not.SameAs(profileBefore), "the session scope is");

            Assert.That(_backend.CountOf(FakeBackend.Endpoints.Config), Is.EqualTo(1), "the config stays warm");
            Assert.That(_backend.CountOf(FakeBackend.Endpoints.Auth), Is.EqualTo(1));
            Assert.That(_backend.CountOf(FakeBackend.Endpoints.Profile), Is.EqualTo(2), "the profile is refetched");
            Assert.That(_backend.CountOf(FakeBackend.Endpoints.Catalog), Is.EqualTo(2));

            Assert.That(observer.Events, Does.Contain("run-started:session:start"));
            Assert.That(observer.Events, Does.Contain("run-started:session:restart"));
            Assert.That(app.Host.RestartCount, Is.EqualTo(1));
        }

        [Test]
        [Timeout(10000)]
        public async Task AThrowingProfileFailsTheRunAndLeavesTheWarmupBlocked()
        {
            _chaos.ThrowInProfile = true;
            var flow = Flow();

            var failure = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => flow.StartAsync());

            Assert.That(failure.Scope, Is.EqualTo("session"));
            Assert.That(failure.Service, Is.EqualTo("PlayerProfileService"));
            Assert.That(failure.Phase, Is.EqualTo("content"));
            Assert.That(failure.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(failure.InnerException!.Message, Is.EqualTo("profile service exploded (chaos)"));
            Assert.That(failure.Completed, Does.Contain("GdprConsentService"));
            Assert.That(string.Join("; ", failure.Unfinished), Does.Contain("QuestWarmupService"));
            await flow.DisposeAsync();
        }

        [Test]
        [Timeout(10000)]
        public async Task AHangingWarmupWarnsAboutTheStallAndThenHitsTheStartupDeadline()
        {
            _chaos.HangInQuestWarmup = true;
            var flow = Flow()
                .Configure(options => options.StallWarningAfter = TimeSpan.FromMilliseconds(100))
                .WithStartupTimeout(TimeSpan.FromSeconds(1));

            var failure = await AsyncTestAssert.ThrowsAsync<TimeoutException>(() => flow.StartAsync());

            Assert.That(failure.Message, Does.StartWith("Startup did not finish within 1.0s."));
            Assert.That(failure.Message, Does.Contain("Running: QuestWarmupService"));
            Assert.That(Find(flow.Log, "Warning: [RuntimeFlow] session: no progress for"),
                Does.Contain("Running: QuestWarmupService"), flow.Log.Count + " captured lines");
            await flow.DisposeAsync();
        }

        [Test]
        [Timeout(10000)]
        public async Task ASlowCatalogTimesOutAndTheWarmupReportsWhatItWasWaitingFor()
        {
            _chaos.TimeoutInCatalog = true;
            var flow = Flow().Configure(options => options.TimeoutMultiplier = 0.05);

            var failure = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => flow.StartAsync());

            Assert.That(failure.Service, Is.EqualTo("CatalogService"));
            Assert.That(failure.InnerException, Is.TypeOf<TimeoutException>());
            Assert.That(failure.InnerException!.Message, Does.StartWith("CatalogService did not complete within 0.1s"));

            var blocked = string.Join("; ", failure.Unfinished);
            Assert.That(blocked, Does.Contain("QuestWarmupService (blocked on CatalogService"));
            await flow.DisposeAsync();
        }

        [Test]
        [Timeout(10000)]
        public async Task AMaintenanceWindowHaltsTheRunWithoutAnException()
        {
            _chaos.MaintenanceHalt = true;

            await using var app = await Flow().StartAsync();

            var status = app.Host.GetStatus();

            Assert.That(app.Result!.Outcome, Is.EqualTo(StartupOutcome.Halted));
            Assert.That(app.Result.HaltedBy, Is.EqualTo("MaintenanceGateService"));
            Assert.That(app.Result.HaltReason, Is.EqualTo(MaintenanceGateService.MaintenanceReason));
            Assert.That(status.Error, Is.Null);
            Assert.That(status.Names(ServiceState.Skipped),
                Is.SupersetOf(new[] { "PlayerProfileService", "CatalogService", "QuestWarmupService" }),
                "the content and warmup phases never start");
            Assert.That(_backend.CountOf(FakeBackend.Endpoints.Profile), Is.EqualTo(0));
        }

        [Test]
        [Timeout(10000)]
        public async Task TheUserGateStaysAwaitingPlayerUntilTheDialogIsAccepted()
        {
            _chaos.GdprAlreadyAccepted = false;
            await using var host = Host();

            var startup = host.StartAsync();
            var gate = await AwaitGateAsync(host);

            Assert.That(gate.State, Is.EqualTo(ServiceState.Running));
            Assert.That(gate.AwaitingPlayer, Is.True);
            Assert.That(gate.UserGated, Is.True);
            Assert.That(host.GetStatus().Names(ServiceState.Pending), Does.Contain("CatalogService"));

            host.Session.Resolve<GdprConsentService>().Accept();
            var result = await startup;

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(host.Session.Resolve<GdprConsentService>().IsAccepted, Is.True);
            Assert.That(host.GetStatus().Percent, Is.EqualTo(100.0).Within(0.001));
        }

        [Test]
        [Timeout(10000)]
        public async Task WaitingForThePlayerIsReportedAsInformationNotAsAStallWarning()
        {
            _chaos.GdprAlreadyAccepted = false;
            await using var host = Host(options => options.StallWarningAfter = TimeSpan.FromMilliseconds(100));

            var startup = host.StartAsync();
            await AwaitGateAsync(host);
            await Task.Delay(400);

            Assert.That(_log.Find(LogLevel.Information, "awaiting player: GdprConsentService"), Is.Not.Null, _log.Dump());
            Assert.That(_log.Has(LogLevel.Warning, "no progress for"), Is.False, _log.Dump());

            host.Session.Resolve<GdprConsentService>().Accept();
            await startup;
        }

        /// <summary>Polls the host until the consent service reports it is waiting for the player.</summary>
        private static async Task<ServiceStatus> AwaitGateAsync(RuntimeFlowHost host)
        {
            for (var attempt = 0; attempt < 300; attempt++)
            {
                foreach (var service in host.GetStatus().Services)
                {
                    if (service.Name == "GdprConsentService" && service.AwaitingPlayer) return service;
                }

                await Task.Delay(10);
            }

            throw new AssertionException("GdprConsentService never reached AwaitingPlayer.");
        }

        private static string Find(IReadOnlyList<string> lines, string fragment)
        {
            foreach (var line in lines)
            {
                if (line.Contains(fragment)) return line;
            }

            throw new AssertionException(
                $"No captured line contains '{fragment}'.{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
        }
    }
}
