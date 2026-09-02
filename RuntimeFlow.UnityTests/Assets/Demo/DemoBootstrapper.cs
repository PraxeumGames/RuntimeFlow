using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using VContainer;

namespace RuntimeFlow.Demo
{
    /// <summary>
    /// Drives <see cref="DemoGame"/> from a scene: it owns the backend, the toggles and the host, and
    /// draws an IMGUI panel with the run's status plus the buttons that reproduce every failure mode.
    /// IMGUI is deliberate here — this is a runtime demo, not editor tooling, and it keeps the whole
    /// panel in one readable file. Open Window &gt; RuntimeFlow &gt; Dashboard to watch the same run
    /// service by service.
    /// </summary>
    [AddComponentMenu("RuntimeFlow/Demo Bootstrapper")]
    public sealed class DemoBootstrapper : MonoBehaviour
    {
        [Header("Startup")]
        [Tooltip("Start the game as soon as the scene plays; otherwise press Start in the panel.")]
        [SerializeField] private bool _startOnPlay = true;

        [Header("Chaos toggles")]
        [Tooltip("PlayerProfileService throws: a required service failure.")]
        [SerializeField] private bool _throwInProfile;

        [Tooltip("QuestWarmupService never finishes: a stall warning, then whatever deadline you set.")]
        [SerializeField] private bool _hangInQuestWarmup;

        [Tooltip("/catalog answers after 10 s, so CatalogService hits its own 2 s timeout.")]
        [SerializeField] private bool _timeoutInCatalog;

        [Tooltip("MaintenanceGateService halts the run without an exception.")]
        [SerializeField] private bool _maintenanceHalt;

        [Tooltip("GdprConsentService completes without waiting for the Accept button.")]
        [SerializeField] private bool _gdprAlreadyAccepted;

        private readonly FakeBackend _backend = new FakeBackend();
        private readonly ChaosToggles _chaos = new ChaosToggles();

        private RuntimeFlowHost? _host;
        private string _message = "Not started.";

        private void Start()
        {
            PushToggles();
            if (_startOnPlay) StartGame();
        }

        private void OnDestroy()
        {
            var host = _host;
            _host = null;
            if (host != null) _ = DisposeAsync(host);
        }

        private void OnGUI()
        {
            const float width = 560f;
            GUILayout.BeginArea(new Rect(12f, 12f, width, 320f), GUI.skin.box);

            GUILayout.Label("RuntimeFlow demo — Window > RuntimeFlow > Dashboard shows the same run.");
            GUILayout.Label(StatusLine());
            GUILayout.Space(4f);
            GUILayout.Label(_message);
            GUILayout.Space(6f);

            GUILayout.BeginHorizontal();
            GUI.enabled = _host == null;
            if (GUILayout.Button("Start game")) StartGame();

            // Restart and Accept stay live while a run is in flight, on purpose: accepting the consent
            // dialog is only useful mid-run, and hammering Restart is how the coalescing is watched —
            // the host folds every request that arrives before the rebuild starts into one rebuild.
            GUI.enabled = _host != null;
            if (GUILayout.Button("Restart session")) RestartSession();
            if (GUILayout.Button("Accept GDPR")) AcceptGdpr();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.Label("Chaos toggles (apply to the next start or restart):");
            _throwInProfile = GUILayout.Toggle(_throwInProfile, "Throw in PlayerProfileService");
            _hangInQuestWarmup = GUILayout.Toggle(_hangInQuestWarmup, "Hang in QuestWarmupService");
            _timeoutInCatalog = GUILayout.Toggle(_timeoutInCatalog, "Timeout in CatalogService");
            _maintenanceHalt = GUILayout.Toggle(_maintenanceHalt, "Halt in MaintenanceGateService");
            _gdprAlreadyAccepted = GUILayout.Toggle(_gdprAlreadyAccepted, "GDPR already accepted");
            PushToggles();

            GUILayout.EndArea();
        }

        private string StatusLine()
        {
            if (_host == null) return "host: not created";

            var status = _host.GetStatus();
            var text = new StringBuilder();
            text.Append(status.State).Append("  ").Append(status.Percent.ToString("0")).Append('%')
                .Append("  ").Append(status.CompletedCount).Append('/').Append(status.TotalCount)
                .Append("  restarts ").Append(status.RestartCount);

            if (status.Running.Count > 0)
            {
                text.Append("\nrunning: ");
                for (var i = 0; i < status.Running.Count; i++)
                {
                    var service = status.Running[i];
                    if (i > 0) text.Append(", ");
                    text.Append(service.Name).Append(" (").Append(service.Elapsed.TotalSeconds.ToString("0.0")).Append("s")
                        .Append(service.AwaitingPlayer ? ", awaiting player)" : ")");
                }
            }

            if (status.HaltReason != null) text.Append("\nhalted: ").Append(status.HaltReason);
            if (status.Error != null) text.Append("\nerror: ").Append(status.Error.Message);
            return text.ToString();
        }

        private void PushToggles()
        {
            _chaos.ThrowInProfile = _throwInProfile;
            _chaos.HangInQuestWarmup = _hangInQuestWarmup;
            _chaos.TimeoutInCatalog = _timeoutInCatalog;
            _chaos.MaintenanceHalt = _maintenanceHalt;
            _chaos.GdprAlreadyAccepted = _gdprAlreadyAccepted;
        }

        private void StartGame()
        {
            if (_host != null) return;
            _host = DemoGame.CreateHost(_backend, _chaos);
            _ = RunAsync(host => host.StartAsync(), "startup");
        }

        /// <summary>
        /// Asks for a restart every time the button is pressed, with no local guard. Double-clicking is
        /// the point: the two requests coalesce inside the host into a single rebuild, and both awaiters
        /// see the same final result.
        /// </summary>
        private void RestartSession()
        {
            if (_host == null) return;
            _ = RunAsync(host => host.RestartAsync("demo-panel"), "restart");
        }

        private void AcceptGdpr()
        {
            var host = _host;
            if (host == null) return;

            try
            {
                host.Session.Resolve<GdprConsentService>().Accept();
                _message = "GDPR accepted.";
            }
            catch (Exception error)
            {
                _message = $"Accept failed: {error.GetType().Name}: {error.Message}";
            }
        }

        private async Task RunAsync(Func<RuntimeFlowHost, Task<StartupResult>> operation, string what)
        {
            var host = _host;
            if (host == null) return;

            _message = $"{what} running…";
            try
            {
                var result = await operation(host);
                _message = result.Outcome == StartupOutcome.Halted
                    ? $"{what}: halted by {result.HaltedBy} — '{result.HaltReason}'"
                    : $"{what}: completed in {result.Elapsed.TotalSeconds:0.0}s" +
                      (result.Degraded.Count > 0 ? $" (degraded: {string.Join(", ", result.Degraded)})" : string.Empty);
            }
            catch (Exception error)
            {
                _message = $"{what} failed — {error.GetType().Name}: {error.Message}";
            }
        }

        private static async Task DisposeAsync(RuntimeFlowHost host)
        {
            try
            {
                await host.DisposeAsync();
            }
            catch (Exception error)
            {
                Debug.LogException(error);
            }
        }
    }
}
