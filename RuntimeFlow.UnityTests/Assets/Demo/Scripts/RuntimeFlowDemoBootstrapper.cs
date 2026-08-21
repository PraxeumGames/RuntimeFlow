using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using UnityEngine;
using VContainer;

namespace RuntimeFlow.Demo
{
    public sealed class RuntimeFlowDemoBootstrapper : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] private bool _autoStartOnPlay = true;

        private RuntimePipeline? _pipeline;
        private CancellationTokenSource? _cts;
        private string _lastLogMessage = "Ready. Press Start or use Editor Dashboard.";
        private bool _isInventoryLoaded;
        private bool _isMinimapLoaded;
        private bool _isBusy;

        private async void Start()
        {
            if (_autoStartOnPlay)
            {
                await StartBootstrapFlowAsync();
            }
        }

        private void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _pipeline?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }

        public async Task StartBootstrapFlowAsync()
        {
            if (_isBusy) return;
            _isBusy = true;
            _lastLogMessage = "Starting Bootstrap Flow...";

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            try
            {
                _pipeline = RuntimePipeline.Create(
                    builder =>
                    {
                        // Global Scope
                        builder.DefineGlobalScope();
                        builder.Global()
                            .Register<IAppConfigService, AppConfigService>(Lifetime.Singleton)
                            .Register<IAnalyticsService, AnalyticsService>(Lifetime.Singleton);

                        // Session Scope
                        builder.DefineSessionScope();
                        builder.Session()
                            .Register<IAuthService, AuthService>(Lifetime.Singleton)
                            .Register<IUserProfileService, UserProfileService>(Lifetime.Singleton)
                            .Register<IInventoryStateService, InventoryStateService>(Lifetime.Singleton)
                            .Register<IQuestService, QuestService>(Lifetime.Singleton);

                        // Scopes
                        builder.Scene<GameplaySceneScope>();
                        builder.Module<HudModuleScope>();
                        builder.Module<InventoryModuleScope>();
                        builder.Module<MinimapModuleScope>();
                    },
                    options =>
                    {
                        options.Health.Enabled = true;
                        options.Health.MinimumServiceTimeout = TimeSpan.FromSeconds(5);
                        options.ReplayFlowOnSessionRestart = true;
                    });

                // Configure Flow Scenario
                _pipeline.ConfigureFlow(new DemoGameStartupFlow());

                _lastLogMessage = "Executing pipeline scenario...";
                await _pipeline.RunAsync(new NoOpSceneLoader(), cancellationToken: _cts.Token);

                _lastLogMessage = "Pipeline execution completed. Ready!";
                Debug.Log("[RuntimeFlow Demo] Bootstrap flow finished successfully!");
            }
            catch (Exception ex)
            {
                _lastLogMessage = $"Error: {ex.Message}";
                Debug.LogError($"[RuntimeFlow Demo] Bootstrap error: {ex}");
            }
            finally
            {
                _isBusy = false;
            }
        }

        public async Task RestartSessionAsync()
        {
            if (_pipeline == null || _isBusy) return;
            _isBusy = true;
            _lastLogMessage = "Restarting Session...";

            try
            {
                await _pipeline.RestartSessionAsync(
                    new RuntimeRestartRequest("demo.manual.restart", "User clicked Restart Session in Demo GUI"),
                    CancellationToken.None);
                _lastLogMessage = "Session restarted cleanly!";
                Debug.Log("[RuntimeFlow Demo] Session restarted.");
            }
            catch (Exception ex)
            {
                _lastLogMessage = $"Restart failed: {ex.Message}";
                Debug.LogError($"[RuntimeFlow Demo] Restart error: {ex}");
            }
            finally
            {
                _isBusy = false;
            }
        }

        public async Task ToggleInventoryModuleAsync()
        {
            if (_pipeline == null || _isBusy) return;
            _isBusy = true;

            try
            {
                if (!_isInventoryLoaded)
                {
                    _lastLogMessage = "Loading additive InventoryModuleScope...";
                    await _pipeline.LoadAdditiveModuleAsync<InventoryModuleScope>(cancellationToken: CancellationToken.None);
                    _isInventoryLoaded = true;
                    _lastLogMessage = "Additive InventoryModuleScope loaded!";
                }
                else
                {
                    _lastLogMessage = "Unloading additive InventoryModuleScope...";
                    await _pipeline.UnloadAdditiveModuleAsync<InventoryModuleScope>(cancellationToken: CancellationToken.None);
                    _isInventoryLoaded = false;
                    _lastLogMessage = "Additive InventoryModuleScope unloaded!";
                }
            }
            catch (Exception ex)
            {
                _lastLogMessage = $"Inventory module error: {ex.Message}";
                Debug.LogError(ex);
            }
            finally
            {
                _isBusy = false;
            }
        }

        public async Task ToggleMinimapModuleAsync()
        {
            if (_pipeline == null || _isBusy) return;
            _isBusy = true;

            try
            {
                if (!_isMinimapLoaded)
                {
                    _lastLogMessage = "Loading additive MinimapModuleScope...";
                    await _pipeline.LoadAdditiveModuleAsync<MinimapModuleScope>(cancellationToken: CancellationToken.None);
                    _isMinimapLoaded = true;
                    _lastLogMessage = "Additive MinimapModuleScope loaded!";
                }
                else
                {
                    _lastLogMessage = "Unloading additive MinimapModuleScope...";
                    await _pipeline.UnloadAdditiveModuleAsync<MinimapModuleScope>(cancellationToken: CancellationToken.None);
                    _isMinimapLoaded = false;
                    _lastLogMessage = "Additive MinimapModuleScope unloaded!";
                }
            }
            catch (Exception ex)
            {
                _lastLogMessage = $"Minimap module error: {ex.Message}";
                Debug.LogError(ex);
            }
            finally
            {
                _isBusy = false;
            }
        }

        private void OnGUI()
        {
            var boxRect = new Rect(16, 16, 420, 540);
            GUI.Box(boxRect, "RuntimeFlow Live Demo Controls");

            var statusText = _pipeline != null
                ? $"State: {_pipeline.GetRuntimeStatus().State} | Phase: {_pipeline.GetExecutionContext().Phase}"
                : "State: Not Started";

            GUI.Label(new Rect(32, 44, 390, 20), $"Status: <b>{statusText}</b>");
            GUI.Label(new Rect(32, 64, 390, 20), $"Chaos Mode: <color=yellow><b>{DemoChaosController.CurrentMode}</b></color>");
            GUI.Label(new Rect(32, 84, 390, 40), $"Log: {_lastLogMessage}");

            GUI.enabled = !_isBusy;

            if (GUI.Button(new Rect(32, 130, 388, 34), "▶ Start / Re-run Full Bootstrap Flow"))
            {
                _ = StartBootstrapFlowAsync();
            }

            if (GUI.Button(new Rect(32, 170, 388, 34), "🔄 Restart Session (Clean Teardown & Replay)"))
            {
                _ = RestartSessionAsync();
            }

            var invBtnText = _isInventoryLoaded ? "❌ Unload Additive Inventory Module" : "📦 Load Additive Inventory Module";
            if (GUI.Button(new Rect(32, 210, 388, 34), invBtnText))
            {
                _ = ToggleInventoryModuleAsync();
            }

            var miniBtnText = _isMinimapLoaded ? "❌ Unload Additive Minimap Module" : "🗺️ Load Additive Minimap Module";
            if (GUI.Button(new Rect(32, 250, 388, 34), miniBtnText))
            {
                _ = ToggleMinimapModuleAsync();
            }

            GUI.Box(new Rect(32, 292, 388, 140), "Chaos & Anomaly Injection Testing");

            if (GUI.Button(new Rect(42, 316, 368, 32), "💥 Inject Exception on UserProfileService"))
            {
                DemoChaosController.CurrentMode = DemoChaosMode.ThrowExceptionOnProfile;
                _lastLogMessage = "Chaos Mode: Exception configured on UserProfile. Now click Re-run.";
            }

            if (GUI.Button(new Rect(42, 354, 368, 32), "⏳ Inject Stall / Watchdog Timeout on WorldGen"))
            {
                DemoChaosController.CurrentMode = DemoChaosMode.StallOnWorldGeneration;
                _lastLogMessage = "Chaos Mode: Stall configured on WorldGen. Now click Re-run.";
            }

            if (GUI.Button(new Rect(42, 392, 368, 32), "✅ Reset Chaos Mode to Normal"))
            {
                DemoChaosController.CurrentMode = DemoChaosMode.Normal;
                _lastLogMessage = "Chaos Mode: Normal mode restored.";
            }

            GUI.enabled = true;

            GUI.Label(new Rect(32, 440, 388, 90),
                "<b>Interactive Tip:</b>\n" +
                "Open <i>Window → RuntimeFlow → Dashboard & Graph</i> in Unity\n" +
                "to inspect the live Scope Hierarchy, Initialization Timeline,\n" +
                "DAG dependencies, and <b>Flow Failure Diagnostic Cards</b>!");
        }

        private sealed class DemoGameStartupFlow : IRuntimeFlowScenario
        {
            public async Task ExecuteAsync(IRuntimeFlowContext context, CancellationToken cancellationToken)
            {
                Debug.Log("[Flow] Step 1: Initializing Global & Session Scopes...");
                await context.InitializeAsync(cancellationToken).ConfigureAwait(false);

                Debug.Log("[Flow] Step 2: Loading GameplaySceneScope...");
                await context.LoadScopeSceneAsync<GameplaySceneScope>(cancellationToken).ConfigureAwait(false);

                Debug.Log("[Flow] Step 3: Loading HudModuleScope...");
                await context.LoadScopeModuleAsync<HudModuleScope>(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
