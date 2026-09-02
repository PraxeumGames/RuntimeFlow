using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using RuntimeFlow.Internal;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace RuntimeFlow.Editor
{
    /// <summary>
    /// Live view of every <see cref="RuntimeFlowHost"/> that is alive: the initialization graph with
    /// states, elapsed times and failures, the scope tree, and the snapshot of the last run once Play
    /// Mode is over. Polls four times a second while playing and never keeps a host reference.
    /// </summary>
    public sealed class RuntimeFlowDashboardWindow : EditorWindow
    {
        private const double RefreshInterval = 0.25;
        private const string UssPath = "Packages/com.praxeum.runtimeflow/Editor/Dashboard/RuntimeFlowDashboardWindow.uss";

        private readonly List<string> _hostLabels = new List<string>();

        private DropdownField? _hostDropdown;
        private Button? _restartButton;
        private VisualElement? _tabBar;
        private VisualElement? _viewContainer;
        private Label? _statusLabel;
        private Button[] _tabButtons = Array.Empty<Button>();

        private GraphView? _graphView;
        private ScopesView? _scopesView;
        private LastRunView? _lastRunView;

        private DashboardSnapshot? _snapshot;
        private int _selectedHost;
        private int _selectedTab;
        private double _lastRefresh;
        private bool _ready;

        /// <summary>Opens the dashboard window.</summary>
        [MenuItem("Window/RuntimeFlow/Dashboard")]
        public static void Open()
        {
            var window = GetWindow<RuntimeFlowDashboardWindow>("RuntimeFlow");
            window.minSize = new Vector2(720, 420);
            window.Show();
        }

        private void OnEnable()
        {
            FlowRegistry.Changed += OnRegistryChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private void OnDisable()
        {
            FlowRegistry.Changed -= OnRegistryChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.AddToClassList("runtimeflow-dashboard-root");

            var style = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (style != null) root.styleSheets.Add(style);

            BuildToolbar(root);

            _viewContainer = new VisualElement();
            _viewContainer.style.flexGrow = 1;
            root.Add(_viewContainer);

            _graphView = new GraphView();
            _scopesView = new ScopesView();
            _scopesView.ScopeSelected += scope =>
            {
                _graphView.SetScopeFilter(scope);
                SelectTab(0);
            };
            _lastRunView = new LastRunView();

            BuildStatusBar(root);

            _ready = true;
            SelectTab(_selectedTab);
            Refresh();
        }

        private void Update()
        {
            if (!_ready || !EditorApplication.isPlaying) return;
            var now = EditorApplication.timeSinceStartup;
            if (now - _lastRefresh < RefreshInterval) return;
            _lastRefresh = now;
            Refresh();
        }

        private void BuildToolbar(VisualElement root)
        {
            var toolbar = new VisualElement();
            toolbar.AddToClassList("rf-toolbar");

            var title = new Label("RuntimeFlow");
            title.AddToClassList("rf-toolbar-title");
            toolbar.Add(title);

            _hostDropdown = new DropdownField();
            _hostDropdown.AddToClassList("rf-host-dropdown");
            _hostDropdown.style.display = DisplayStyle.None;
            _hostDropdown.RegisterValueChangedCallback(evt =>
            {
                var index = _hostLabels.IndexOf(evt.newValue);
                if (index < 0) return;
                _selectedHost = index;
                Refresh();
            });
            toolbar.Add(_hostDropdown);

            _restartButton = ToolbarButton(toolbar, "Restart session", RestartSelectedHost, "rf-action-button--primary");
            ToolbarButton(toolbar, "Copy diagnostics JSON", CopyDiagnostics, null);
            ToolbarButton(toolbar, "Copy Describe()", CopyDescribe, null);
            ToolbarButton(toolbar, "Refresh", Refresh, null);

            _tabBar = new VisualElement();
            _tabBar.AddToClassList("rf-tab-bar");
            _tabButtons = new[]
            {
                Tab(_tabBar, "Graph", 0),
                Tab(_tabBar, "Scopes", 1),
                Tab(_tabBar, "Last run", 2)
            };
            toolbar.Add(_tabBar);

            root.Add(toolbar);
        }

        private void BuildStatusBar(VisualElement root)
        {
            var bar = new VisualElement();
            bar.AddToClassList("rf-status-bar");

            _statusLabel = new Label("No host is running.");
            bar.Add(_statusLabel);

            // The package version cannot change while the window is open, so it is written once here
            // rather than on every one of the four refreshes a second.
            var version = new Label("RuntimeFlow " + DashboardData.PackageVersion);
            version.AddToClassList("rf-version-label");
            bar.Add(version);

            root.Add(bar);
        }

        private static Button ToolbarButton(VisualElement parent, string text, System.Action clicked, string? modifier)
        {
            var button = new Button(clicked) { text = text };
            button.AddToClassList("rf-action-button");
            if (modifier != null) button.AddToClassList(modifier);
            parent.Add(button);
            return button;
        }

        private Button Tab(VisualElement parent, string text, int index)
        {
            var button = new Button(() => SelectTab(index)) { text = text };
            button.AddToClassList("rf-tab-button");
            parent.Add(button);
            return button;
        }

        private void SelectTab(int index)
        {
            _selectedTab = index;
            if (!_ready || _viewContainer == null) return;

            for (var i = 0; i < _tabButtons.Length; i++)
            {
                _tabButtons[i].EnableInClassList("rf-tab-button--active", i == index);
            }

            _viewContainer.Clear();
            switch (index)
            {
                case 1:
                    _viewContainer.Add(_scopesView);
                    break;
                case 2:
                    _viewContainer.Add(_lastRunView);
                    break;
                default:
                    _viewContainer.Add(_graphView);
                    break;
            }

            RenderCurrentTab();
        }

        private void OnRegistryChanged()
        {
            if (!_ready) return;
            Refresh();
        }

        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode)
            {
                var snapshot = CaptureSelected() ?? _snapshot;
                if (snapshot != null) DashboardData.SaveLastRun(snapshot);
            }

            if (!_ready) return;
            Refresh();
        }

        private void Refresh()
        {
            if (!_ready) return;

            UpdateHostDropdown();

            // A host that was disposed when Play Mode ended can linger in the weak registry until the
            // next collection; its snapshot has no scopes, so outside Play Mode it counts as "gone".
            var captured = CaptureSelected();
            if (captured != null && captured.Scopes.Count == 0 && !EditorApplication.isPlaying) captured = null;
            _snapshot = EditorApplication.isPlaying ? captured ?? _snapshot : captured;

            if (_restartButton != null) _restartButton.SetEnabled(_snapshot is { CanRestart: true });

            RenderCurrentTab();
            UpdateStatusBar();
        }

        private void RenderCurrentTab()
        {
            switch (_selectedTab)
            {
                case 1:
                    _scopesView?.Refresh(_snapshot);
                    break;
                case 2:
                    var live = _snapshot != null && _snapshot.Live && _snapshot.ServiceCount > 0 ? _snapshot : null;
                    _lastRunView?.Refresh(live ?? DashboardData.LoadLastRun(), DashboardData.LiveHosts.Count > 0);
                    break;
                default:
                    _graphView?.Refresh(_snapshot);
                    break;
            }
        }

        private void UpdateHostDropdown()
        {
            if (_hostDropdown == null) return;

            var hosts = DashboardData.LiveHosts;
            var labels = new List<string>(hosts.Count);
            for (var i = 0; i < hosts.Count; i++) labels.Add(DashboardData.Label(i, hosts[i]));

            if (_selectedHost >= labels.Count) _selectedHost = Math.Max(0, labels.Count - 1);

            if (!SameLabels(labels))
            {
                _hostLabels.Clear();
                _hostLabels.AddRange(labels);
                _hostDropdown.choices = _hostLabels;
                _hostDropdown.SetValueWithoutNotify(
                    _selectedHost < _hostLabels.Count ? _hostLabels[_selectedHost] : string.Empty);
            }

            _hostDropdown.style.display = labels.Count > 1 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private bool SameLabels(List<string> labels)
        {
            if (labels.Count != _hostLabels.Count) return false;
            for (var i = 0; i < labels.Count; i++)
            {
                if (!string.Equals(labels[i], _hostLabels[i], StringComparison.Ordinal)) return false;
            }
            return true;
        }

        /// <summary>
        /// Captures the selected host. The registry is asked again on every refresh and the host is
        /// released as soon as the snapshot is built, because the registry holds weak references.
        /// </summary>
        private DashboardSnapshot? CaptureSelected()
        {
            var hosts = DashboardData.LiveHosts;
            if (hosts.Count == 0) return null;
            var index = Math.Max(0, Math.Min(_selectedHost, hosts.Count - 1));
            try
            {
                return DashboardData.Capture(hosts[index], index);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[RuntimeFlow] the dashboard could not read the host: " + exception.Message);
                return null;
            }
        }

        private void UpdateStatusBar()
        {
            if (_statusLabel == null) return;

            if (_snapshot == null)
            {
                _statusLabel.text = EditorApplication.isPlaying
                    ? "Play Mode: no RuntimeFlowHost has been created yet."
                    : "Edit Mode: showing the last stored snapshot on the Last run tab.";
                return;
            }

            _statusLabel.text = string.Format(
                CultureInfo.InvariantCulture,
                "{0} · {1} · {2} · {3} · {4} service(s) · {5} restart(s)",
                _snapshot.HostLabel, _snapshot.State, ViewHelpers.Percent(_snapshot.Percent),
                ViewHelpers.Seconds(_snapshot.ElapsedMs), _snapshot.ServiceCount, _snapshot.RestartCount);
        }

        private void CopyDiagnostics()
        {
            var snapshot = CaptureSelected() ?? _snapshot ?? DashboardData.LoadLastRun();
            if (snapshot == null)
            {
                Debug.LogWarning("[RuntimeFlow] nothing to copy: no host is running and no snapshot was stored.");
                return;
            }
            DiagnosticsJson.CopyToClipboard(snapshot);
        }

        private void CopyDescribe()
        {
            var snapshot = CaptureSelected() ?? _snapshot ?? DashboardData.LoadLastRun();
            if (snapshot == null || snapshot.Describe.Length == 0)
            {
                Debug.LogWarning("[RuntimeFlow] nothing to copy: no graph has been described yet.");
                return;
            }
            DiagnosticsJson.CopyText("Describe()", snapshot.Describe);
        }

        private void RestartSelectedHost()
        {
            var hosts = DashboardData.LiveHosts;
            if (hosts.Count == 0)
            {
                Debug.LogWarning("[RuntimeFlow] no host to restart.");
                return;
            }

            var index = Math.Max(0, Math.Min(_selectedHost, hosts.Count - 1));
            _ = RestartAsync(hosts[index]);
        }

        private static async Task RestartAsync(RuntimeFlowHost host)
        {
            try
            {
                var result = await host.RestartAsync("dashboard");
                Debug.Log(string.Format(
                    CultureInfo.InvariantCulture,
                    "[RuntimeFlow] dashboard restart finished: {0} after {1:0.00}s.",
                    result.Outcome, result.Elapsed.TotalSeconds));
            }
            catch (Exception exception)
            {
                Debug.LogError("[RuntimeFlow] dashboard restart failed: " + exception);
            }
        }
    }
}
