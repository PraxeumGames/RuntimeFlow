using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using RuntimeFlow.Content;

namespace RuntimeFlow.Demo.Midcore
{
    // ---------- platform SDKs (Platform stage: run before Content stage) ----------

    public sealed class AnalyticsService : ISessionInitializableService, IPlatformStartupInitializableService
    {
        private readonly IContentSource<AuthData> _auth;
        private readonly FakeBackend _backend;

        public static bool Initialized { get; private set; }
        public static void ResetInitialized() => Initialized = false;

        public AnalyticsService(IContentSource<AuthData> auth, FakeBackend backend)
        {
            _auth = auth; _backend = backend;
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            _backend.Log($"analytics:init:{_auth.Data.PlayerId}");
            Initialized = true;
            return Task.CompletedTask;
        }
    }

    public sealed class AdsService : ISessionInitializableService, IPlatformStartupInitializableService
    {
        public static bool Initialized { get; private set; }
        public static void ResetInitialized() => Initialized = false;

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Initialized = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>IAP restore: user-gated (store dialog exempt from watchdog).</summary>
    public sealed class IapRestoreService : ISessionInitializableService, IUserInteractionGatedInitializableService
    {
        public static bool Restored { get; private set; }
        public static void ResetRestored() => Restored = false;

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Restored = true;
            return Task.CompletedTask;
        }
    }

    // ---------- game services (Content/Session stage: consume profile + catalog) ----------

    public sealed class EconomyService : ISessionInitializableService, IContentStartupInitializableService
    {
        private readonly IContentSource<ProfileData> _profile;
        private readonly IContentSource<RemoteConfigData> _config;

        public static int StartingCoins { get; private set; }
        public static void ResetStartingCoins() => StartingCoins = 0;

        public EconomyService(IContentSource<ProfileData> profile, IContentSource<RemoteConfigData> config)
        {
            _profile = profile; _config = config;
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            StartingCoins = _profile.Data.Coins + _config.Data.GiftCoins;
            return Task.CompletedTask;
        }
    }

    public sealed class InventoryService : ISessionInitializableService, IContentStartupInitializableService
    {
        private readonly IContentSource<CatalogData> _catalog;

        public static int ReadyItems { get; private set; }
        public static void ResetReadyItems() => ReadyItems = 0;

        public InventoryService(IContentSource<CatalogData> catalog)
        {
            _catalog = catalog;
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            ReadyItems = _catalog.Data.Bundles.Count;
            return Task.CompletedTask;
        }
    }

    /// <summary>Quest board: UI stage (runs after all content services).</summary>
    public sealed class QuestBoardService : ISessionInitializableService, IUiStartupInitializableService
    {
        private readonly IContentSource<ProfileData> _profile;

        public static string? Header { get; private set; }
        public static void ResetHeader() => Header = null;

        public QuestBoardService(IContentSource<ProfileData> profile) => _profile = profile;

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Header = $"{_profile.Data.DisplayName} (lvl {_profile.Data.Level})";
            return Task.CompletedTask;
        }
    }
}
