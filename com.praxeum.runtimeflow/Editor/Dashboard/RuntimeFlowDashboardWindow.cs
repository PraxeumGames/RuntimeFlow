#if UNITY_EDITOR
using System;
using RuntimeFlow.Contexts;
using RuntimeFlow.Editor.Dashboard.Adapters;
using RuntimeFlow.Editor.Dashboard.Views;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace RuntimeFlow.Editor.Dashboard
{
    public sealed class RuntimeFlowDashboardWindow : EditorWindow
    {
        private VisualElement _root = null!;
        private Label _modeBadge = null!;
        private TextField _searchField = null!;
        private VisualElement _viewContainer = null!;
        private Label _statusTextLabel = null!;

        private CompiledGraphView _compiledGraphView = null!;
        private ScopeHierarchyView _scopeHierarchyView = null!;
        private PipelineStatusView _pipelineStatusView = null!;
        private TimelineProgressView _timelineProgressView = null!;

        private Button[] _tabButtons = Array.Empty<Button>();
        private int _selectedTabIndex;
        private double _lastUpdateTime;

        [MenuItem("Window/RuntimeFlow/Dashboard & Graph")]
        public static void Open()
        {
            var window = GetWindow<RuntimeFlowDashboardWindow>("RuntimeFlow Dashboard");
            window.minSize = new Vector2(680, 420);
            window.Show();
        }

        public void CreateGUI()
        {
            _root = rootVisualElement;
            _root.AddToClassList("runtimeflow-dashboard-root");

            // Load StyleSheet
            var stylesheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.praxeum.runtimeflow/Editor/Dashboard/RuntimeFlowDashboardWindow.uss");
            if (stylesheet == null)
            {
                // Fallback for direct assets folder development
                stylesheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                    "Assets/com.praxeum.runtimeflow/Editor/Dashboard/RuntimeFlowDashboardWindow.uss");
            }
            if (stylesheet != null)
                _root.styleSheets.Add(stylesheet);

            // Build UI
            BuildToolbar();

            _viewContainer = new VisualElement();
            _viewContainer.style.flexGrow = 1;
            _root.Add(_viewContainer);

            BuildViews();
            BuildStatusBar();

            SelectTab(0);
        }

        private void BuildToolbar()
        {
            var toolbar = new VisualElement();
            toolbar.AddToClassList("rf-toolbar");

            var title = new Label("RuntimeFlow");
            title.AddToClassList("rf-toolbar-title");
            toolbar.Add(title);

            _modeBadge = new Label(Application.isPlaying ? "PLAY" : "EDIT");
            _modeBadge.AddToClassList("rf-mode-badge");
            _modeBadge.AddToClassList(Application.isPlaying ? "rf-mode-badge--play" : "rf-mode-badge--edit");
            toolbar.Add(_modeBadge);

            var tab0 = new Button(() => SelectTab(0)) { text = "Initialization DAG" };
            var tab1 = new Button(() => SelectTab(1)) { text = "Scope Hierarchy" };
            var tab2 = new Button(() => SelectTab(2)) { text = "Pipeline Status" };
            var tab3 = new Button(() => SelectTab(3)) { text = "Timeline & Teardown" };

            _tabButtons = new[] { tab0, tab1, tab2, tab3 };
            for (var i = 0; i < _tabButtons.Length; i++)
            {
                _tabButtons[i].AddToClassList("rf-tab-button");
                toolbar.Add(_tabButtons[i]);
            }

            _searchField = new TextField("Filter:");
            _searchField.AddToClassList("rf-search-field");
            _searchField.RegisterValueChangedCallback(evt =>
            {
                _compiledGraphView.SetFilter(evt.newValue, null);
            });
            toolbar.Add(_searchField);

            var refreshBtn = new Button(RefreshAll) { text = "↻" };
            refreshBtn.AddToClassList("rf-action-button");
            refreshBtn.tooltip = "Refresh current view";
            toolbar.Add(refreshBtn);

            _root.Add(toolbar);
        }

        private void BuildViews()
        {
            _compiledGraphView = new CompiledGraphView();
            _scopeHierarchyView = new ScopeHierarchyView();
            _pipelineStatusView = new PipelineStatusView();
            _timelineProgressView = new TimelineProgressView();
        }

        private void BuildStatusBar()
        {
            var statusBar = new VisualElement();
            statusBar.AddToClassList("rf-status-bar");

            _statusTextLabel = new Label("RuntimeFlow Dashboard ready.");
            statusBar.Add(_statusTextLabel);

            var versionLabel = new Label("RuntimeFlow v0.5.0");
            versionLabel.style.fontSize = 10;
            versionLabel.style.color = new Color(0.9f, 0.9f, 0.9f);
            statusBar.Add(versionLabel);

            _root.Add(statusBar);
        }

        private void SelectTab(int index)
        {
            _selectedTabIndex = index;
            for (var i = 0; i < _tabButtons.Length; i++)
            {
                _tabButtons[i].RemoveFromClassList("rf-tab-button--active");
                if (i == index)
                    _tabButtons[i].AddToClassList("rf-tab-button--active");
            }

            _viewContainer.Clear();
            switch (index)
            {
                case 0:
                    _viewContainer.Add(_compiledGraphView);
                    _compiledGraphView.Refresh();
                    _statusTextLabel.text = "Viewing compiled initialization graph (DAG).";
                    break;
                case 1:
                    _viewContainer.Add(_scopeHierarchyView);
                    _scopeHierarchyView.Refresh();
                    _statusTextLabel.text = Application.isPlaying
                        ? "Inspecting active runtime scope hierarchy."
                        : "Scope hierarchy definition (enter PlayMode for live instances).";
                    break;
                case 2:
                    _viewContainer.Add(_pipelineStatusView);
                    _pipelineStatusView.Refresh();
                    _statusTextLabel.text = "Pipeline lifecycle, health supervisor & readiness.";
                    break;
                case 3:
                    _viewContainer.Add(_timelineProgressView);
                    _timelineProgressView.Refresh();
                    _statusTextLabel.text = "Live service initialization timeline and reverse teardown prediction.";
                    break;
            }
        }

        private void RefreshAll()
        {
            _modeBadge.text = Application.isPlaying ? "PLAY" : "EDIT";
            _modeBadge.ClearClassList();
            _modeBadge.AddToClassList("rf-mode-badge");
            _modeBadge.AddToClassList(Application.isPlaying ? "rf-mode-badge--play" : "rf-mode-badge--edit");

            SelectTab(_selectedTabIndex);
        }

        private void Update()
        {
            if (!Application.isPlaying)
                return;

            var time = EditorApplication.timeSinceStartup;
            if (time - _lastUpdateTime < 0.25) // Poll 4 times per second in PlayMode
                return;

            _lastUpdateTime = time;

            if (_selectedTabIndex == 1)
                _scopeHierarchyView.Refresh();
            else if (_selectedTabIndex == 2)
                _pipelineStatusView.Refresh();
            else if (_selectedTabIndex == 3)
                _timelineProgressView.Refresh();
        }
    }
}
#endif
