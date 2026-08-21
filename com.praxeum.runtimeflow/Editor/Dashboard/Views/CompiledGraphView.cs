#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using RuntimeFlow.Contexts;
using RuntimeFlow.Editor.Dashboard.Adapters;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace RuntimeFlow.Editor.Dashboard.Views
{
    public sealed class CompiledGraphView : VisualElement
    {
        private readonly ListView _nodeListView;
        private readonly ScrollView _detailScrollView;
        private readonly Label _ruleVersionLabel;
        private readonly Label _nodeCountLabel;
        private readonly List<RuntimeFlowCompiledInitializationGraph.Node> _filteredNodes = new();
        private IReadOnlyList<RuntimeFlowCompiledInitializationGraph.Node> _allNodes = Array.Empty<RuntimeFlowCompiledInitializationGraph.Node>();
        private string _searchFilter = string.Empty;
        private GameContextType? _scopeFilter;

        public CompiledGraphView()
        {
            style.flexGrow = 1;

            var splitView = new TwoPaneSplitView(0, 260, TwoPaneSplitViewOrientation.Horizontal);
            splitView.AddToClassList("rf-split-view");

            // Left Pane (List)
            var leftPane = new VisualElement();
            leftPane.AddToClassList("rf-pane-left");

            var summaryCard = new VisualElement();
            summaryCard.AddToClassList("rf-card");
            _ruleVersionLabel = new Label("Rule Version: Loading...");
            _ruleVersionLabel.style.fontSize = 11;
            _ruleVersionLabel.style.color = new Color(0.6f, 0.8f, 1f);
            _nodeCountLabel = new Label("Total Nodes: 0");
            _nodeCountLabel.style.fontSize = 11;
            summaryCard.Add(_ruleVersionLabel);
            summaryCard.Add(_nodeCountLabel);
            leftPane.Add(summaryCard);

            _nodeListView = new ListView(_filteredNodes, 36, MakeListItem, BindListItem);
            _nodeListView.style.flexGrow = 1;
            _nodeListView.onSelectionChange += OnNodeSelected;
            leftPane.Add(_nodeListView);

            // Right Pane (Details)
            var rightPane = new VisualElement();
            rightPane.AddToClassList("rf-pane-right");
            _detailScrollView = new ScrollView();
            _detailScrollView.style.flexGrow = 1;
            rightPane.Add(_detailScrollView);

            splitView.Add(leftPane);
            splitView.Add(rightPane);
            Add(splitView);

            Refresh();
        }

        public void SetFilter(string search, GameContextType? scope)
        {
            _searchFilter = search ?? string.Empty;
            _scopeFilter = scope;
            ApplyFilter();
        }

        public void Refresh()
        {
            if (RuntimePipelineEditorBridge.TryGetStaticGraph(out var ruleVersion, out var nodes))
            {
                _allNodes = nodes;
                _ruleVersionLabel.text = $"Rules: {ruleVersion}";
                _nodeCountLabel.text = $"Compiled Nodes: {_allNodes.Count}";
            }
            else
            {
                _allNodes = Array.Empty<RuntimeFlowCompiledInitializationGraph.Node>();
                _ruleVersionLabel.text = "Rules: No compiled graph found";
                _nodeCountLabel.text = "Nodes: 0 (Add [assembly: GenerateRuntimeFlowInitializationGraph])";
            }

            ApplyFilter();
        }

        private void ApplyFilter()
        {
            _filteredNodes.Clear();
            foreach (var node in _allNodes)
            {
                if (_scopeFilter.HasValue && node.Scope != _scopeFilter.Value)
                    continue;

                if (!string.IsNullOrEmpty(_searchFilter))
                {
                    var matchService = node.ServiceType?.Name.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                    var matchImpl = node.ImplementationType?.Name.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!matchService && !matchImpl)
                        continue;
                }

                _filteredNodes.Add(node);
            }

            _nodeListView.Rebuild();
            if (_filteredNodes.Count > 0)
            {
                _nodeListView.SetSelection(0);
            }
            else
            {
                ShowEmptyDetails();
            }
        }

        private static VisualElement MakeListItem()
        {
            var item = new VisualElement();
            item.AddToClassList("rf-list-item");

            var nameLabel = new Label();
            nameLabel.name = "node-name";
            nameLabel.AddToClassList("rf-item-name");

            var scopeBadge = new Label();
            scopeBadge.name = "node-scope";
            scopeBadge.AddToClassList("rf-badge");

            item.Add(nameLabel);
            item.Add(scopeBadge);
            return item;
        }

        private void BindListItem(VisualElement element, int index)
        {
            if (index < 0 || index >= _filteredNodes.Count) return;
            var node = _filteredNodes[index];

            var nameLabel = element.Q<Label>("node-name");
            var scopeBadge = element.Q<Label>("node-scope");

            nameLabel.text = node.ServiceType?.Name ?? "<Unknown>";
            scopeBadge.text = node.Scope.ToString();
            scopeBadge.ClearClassList();
            scopeBadge.AddToClassList("rf-badge");
            scopeBadge.AddToClassList(GetScopeBadgeClass(node.Scope));
        }

        private void OnNodeSelected(IEnumerable<object> selectedItems)
        {
            var node = selectedItems.FirstOrDefault() as RuntimeFlowCompiledInitializationGraph.Node;
            if (node == null)
            {
                ShowEmptyDetails();
                return;
            }

            _detailScrollView.Clear();

            // Header Card
            var headerCard = new VisualElement();
            headerCard.AddToClassList("rf-card");

            var title = new Label(node.ServiceType?.Name ?? "Unknown Node");
            title.AddToClassList("rf-card-title");
            headerCard.Add(title);

            AddPropertyRow(headerCard, "Full Service Type", node.ServiceType?.FullName ?? "null");
            AddPropertyRow(headerCard, "Implementation Type", node.ImplementationType?.FullName ?? "null");
            AddPropertyRow(headerCard, "Scope", node.Scope.ToString());
            AddPropertyRow(headerCard, "Assembly", node.ImplementationType?.Assembly.GetName().Name ?? "null");
            _detailScrollView.Add(headerCard);

            // Dependencies Card
            var depsCard = new VisualElement();
            depsCard.AddToClassList("rf-card");
            var depsTitle = new Label($"Dependencies ({node.Dependencies.Length})");
            depsTitle.AddToClassList("rf-card-title");
            depsCard.Add(depsTitle);

            if (node.Dependencies.Length == 0)
            {
                var noDeps = new Label("• No initialization dependencies declared");
                noDeps.style.color = new Color(0.6f, 0.6f, 0.6f);
                depsCard.Add(noDeps);
            }
            else
            {
                for (var i = 0; i < node.Dependencies.Length; i++)
                {
                    var depType = node.Dependencies[i];
                    var depRow = new VisualElement();
                    depRow.style.flexDirection = FlexDirection.Row;
                    depRow.style.marginBottom = 4;

                    var bullet = new Label("► ");
                    bullet.style.color = new Color(0.3f, 0.7f, 1f);
                    var depLabel = new Label(depType.Name);
                    depLabel.style.fontSize = 12;
                    depLabel.style.unityFontStyleAndWeight = FontStyle.Bold;

                    var depFull = new Label($" ({depType.Namespace})");
                    depFull.style.fontSize = 10;
                    depFull.style.color = new Color(0.5f, 0.5f, 0.5f);

                    depRow.Add(bullet);
                    depRow.Add(depLabel);
                    depRow.Add(depFull);
                    depsCard.Add(depRow);
                }
            }
            _detailScrollView.Add(depsCard);
        }

        private void ShowEmptyDetails()
        {
            _detailScrollView.Clear();
            var empty = new Label("Select a node from the list to view DAG dependencies and scope details.");
            empty.style.color = new Color(0.5f, 0.5f, 0.5f);
            empty.style.marginTop = 20;
            empty.style.alignSelf = Align.Center;
            _detailScrollView.Add(empty);
        }

        private static void AddPropertyRow(VisualElement container, string label, string value)
        {
            var row = new VisualElement();
            row.AddToClassList("rf-property-row");
            var lbl = new Label(label);
            lbl.AddToClassList("rf-property-label");
            var val = new Label(value);
            val.AddToClassList("rf-property-value");
            row.Add(lbl);
            row.Add(val);
            container.Add(row);
        }

        private static string GetScopeBadgeClass(GameContextType scope) => scope switch
        {
            GameContextType.Global => "rf-badge--blue",
            GameContextType.Session => "rf-badge--green",
            GameContextType.Scene => "rf-badge--yellow",
            GameContextType.Module => "rf-badge--gray",
            _ => "rf-badge--gray"
        };
    }
}
#endif
