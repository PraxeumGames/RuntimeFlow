using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using UnityEngine;
using RuntimeFlow.Pipeline;

namespace RuntimeFlow.Demo
{
    public enum DemoChaosMode
    {
        Normal = 0,
        ThrowExceptionOnProfile = 1,
        StallOnWorldGeneration = 2
    }

    public static class DemoChaosController
    {
        public static DemoChaosMode CurrentMode = DemoChaosMode.Normal;
    }

    // ==========================================
    // GLOBAL SERVICES (Application Lifetime)
    // ==========================================

    public interface IAppConfigService : IGlobalInitializableService
    {
        string Environment { get; }
    }

    public sealed class AppConfigService : IAppConfigService, IInitializationThreadAffinityProvider
    {
        public string Environment => "Production-Demo";
        public InitializationThreadAffinity ThreadAffinity => InitializationThreadAffinity.AnyThread;

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            Debug.Log("[Global] Loading AppConfig from remote CDN...");
            await Task.Delay(250, cancellationToken);
            Debug.Log("[Global] AppConfig loaded successfully.");
        }
    }

    public interface IAnalyticsService : IGlobalInitializableService
    {
        void Track(string eventName);
    }

    [DependsOn(typeof(IAppConfigService))]
    public sealed class AnalyticsService : IAnalyticsService
    {
        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            Debug.Log("[Global] Initializing Analytics SDK (depends on AppConfig)...");
            await Task.Delay(150, cancellationToken);
            Debug.Log("[Global] Analytics SDK ready.");
        }

        public void Track(string eventName)
        {
            Debug.Log($"[Analytics] Event tracked: '{eventName}'");
        }
    }

    // ==========================================
    // SESSION SERVICES (Session Startup Stages)
    // ==========================================

    public interface IAuthService : IPlatformStartupInitializableService
    {
        string UserId { get; }
        bool IsAuthenticated { get; }
    }

    public sealed class AuthService : IAuthService
    {
        public string UserId { get; private set; } = string.Empty;
        public bool IsAuthenticated => !string.IsNullOrEmpty(UserId);

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            Debug.Log("[Session:Stage 1] Authenticating user session with backend...");
            await Task.Delay(300, cancellationToken);
            UserId = $"Player_{UnityEngine.Random.Range(1000, 9999)}";
            Debug.Log($"[Session:Stage 1] Authenticated as {UserId}.");
        }
    }

    public interface IUserProfileService : IContentStartupInitializableService
    {
        string DisplayName { get; }
        int Level { get; }
    }

    [DependsOn(typeof(IAuthService))]
    public sealed class UserProfileService : IUserProfileService
    {
        public string DisplayName { get; private set; } = string.Empty;
        public int Level { get; private set; } = 1;

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            Debug.Log("[Session:Stage 2] Fetching User Profile & save data (depends on AuthService)...");
            await Task.Delay(150, cancellationToken);

            if (DemoChaosController.CurrentMode == DemoChaosMode.ThrowExceptionOnProfile)
            {
                Debug.LogError("[Session:Stage 2] CHAOS INJECTION: Simulating network failure on UserProfileService!");
                throw new InvalidOperationException("Failed to load user profile: Remote server returned 503 Service Unavailable (Simulated Error).");
            }

            DisplayName = "Commander Shepard";
            Level = 42;
            Debug.Log($"[Session:Stage 2] Profile loaded: {DisplayName} (Lvl {Level}).");
        }
    }

    public interface IInventoryStateService : ISessionStartupInitializableService, ISessionRestartAware, IAsyncDisposableService
    {
        int Gold { get; }
        int ItemCount { get; }
    }

    [DependsOn(typeof(IUserProfileService))]
    public sealed class InventoryStateService : IInventoryStateService
    {
        public int Gold { get; private set; } = 1500;
        public int ItemCount { get; private set; } = 12;

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            Debug.Log("[Session:Stage 3] Loading player inventory state & wallet...");
            await Task.Delay(200, cancellationToken);
            Debug.Log($"[Session:Stage 3] Inventory ready: {Gold} gold, {ItemCount} items.");
        }

        public void BeforeSessionRestart()
        {
            Debug.Log("[Session:Restart] Saving inventory dirty changes & flushing buffers before restart...");
        }

        public async Task DisposeAsync(CancellationToken cancellationToken)
        {
            Debug.Log("[Session:Teardown] Disposing InventoryStateService (Reverse order)...");
            await Task.Delay(50, cancellationToken);
        }
    }

    public interface IQuestService : IUiStartupInitializableService
    {
        string ActiveQuest { get; }
    }

    [DependsOn(typeof(IInventoryStateService))]
    public sealed class QuestService : IQuestService
    {
        public string ActiveQuest => "Infiltrate the Citadel";

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            Debug.Log("[Session:Stage 4] Synchronizing active quest log with server...");
            await Task.Delay(180, cancellationToken);
            Debug.Log($"[Session:Stage 4] Quests initialized. Active: '{ActiveQuest}'.");
        }
    }

    // ==========================================
    // SCENE SERVICES (Gameplay Scene Scope)
    // ==========================================

    public interface IWorldGenerationService : ISceneInitializableService
    {
        bool IsWorldReady { get; }
    }

    public sealed class WorldGenerationService : IWorldGenerationService
    {
        public bool IsWorldReady { get; private set; }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            Debug.Log("[Scene] Generating terrain chunks & navmesh grid...");

            if (DemoChaosController.CurrentMode == DemoChaosMode.StallOnWorldGeneration)
            {
                Debug.LogWarning("[Scene] CHAOS INJECTION: Simulating infinite hanging/stall on WorldGenerationService (will trigger Watchdog timeout!)...");
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
            else
            {
                await Task.Delay(300, cancellationToken);
            }

            IsWorldReady = true;
            Debug.Log("[Scene] World generation completed.");
        }
    }

    public interface IPlayerSpawnService : ISceneInitializableService
    {
        Vector3 SpawnPosition { get; }
    }

    [DependsOn(typeof(IWorldGenerationService))]
    public sealed class PlayerSpawnService : IPlayerSpawnService
    {
        public Vector3 SpawnPosition => new Vector3(0, 1.5f, 0);

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            Debug.Log("[Scene] Spawning player character & camera rig (depends on WorldGeneration)...");
            await Task.Delay(150, cancellationToken);
            Debug.Log($"[Scene] Player spawned at {SpawnPosition}.");
        }
    }

    // ==========================================
    // MODULE SERVICES (Main & Additive Modules)
    // ==========================================

    public interface IHudService : IModuleInitializableService
    {
        void ShowMessage(string message);
    }

    public sealed class HudService : IHudService
    {
        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            Debug.Log("[Module:HUD] Initializing Heads-Up Display & UI Canvas...");
            await Task.Delay(100, cancellationToken);
            Debug.Log("[Module:HUD] HUD Canvas ready.");
        }

        public void ShowMessage(string message)
        {
            Debug.Log($"[HUD Message] {message}");
        }
    }

    public interface IInventoryViewService : IModuleInitializableService
    {
        bool IsOpen { get; }
    }

    public sealed class InventoryViewService : IInventoryViewService
    {
        public bool IsOpen => true;

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            Debug.Log("[Additive Module:Inventory] Loading inventory UI grid & item icons...");
            await Task.Delay(120, cancellationToken);
            Debug.Log("[Additive Module:Inventory] Inventory UI opened.");
        }
    }

    public interface IMinimapService : IModuleInitializableService
    {
        float ZoomLevel { get; }
    }

    public sealed class MinimapService : IMinimapService
    {
        public float ZoomLevel => 1.5f;

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            Debug.Log("[Additive Module:Minimap] Initializing Radar & Minimap render texture...");
            await Task.Delay(100, cancellationToken);
            Debug.Log("[Additive Module:Minimap] Minimap rendering active.");
        }
    }
}
