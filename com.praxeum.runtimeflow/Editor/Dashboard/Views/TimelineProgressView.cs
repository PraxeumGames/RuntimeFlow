#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using RuntimeFlow.Contexts;
using RuntimeFlow.Editor.Dashboard.Adapters;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace RuntimeFlow.Editor.Dashboard.Views
{
    public sealed class TimelineProgressView : VisualElement
    {
        private readonly ScrollView _scrollView;

        public TimelineProgressView()
        {
            style.flexGrow = 1;
            _scrollView = new ScrollView();
            _scrollView.style.flexGrow = 1;
            _scrollView.style.paddingLeft = 12;
            _scrollView.style.paddingRight = 12;
            _scrollView.style.paddingTop = 12;
            Add(_scrollView);
            Refresh();
        }

        public void Refresh()
        {
            _scrollView.Clear();

            if (!Application.isPlaying)
            {
                var card = new VisualElement();
                card.AddToClassList("rf-card");
                var title = new Label("Initialization Order & Reverse Teardown");
                title.AddToClassList("rf-card-title");
                card.Add(title);

                var info = new Label(
                    "Deterministic Lifecycle Guarantee:\n" +
                    "• Services are initialized in strict dependency order (DAG tiers).\n" +
                    "• Services with affinity (MainThread vs ThreadPool) are marshaled.\n" +
                    "• On scope teardown / dispose, services are disposed in exact reverse initialization order (N-1 down to 0).");
                info.style.fontSize = 11;
                info.style.color = new Color(0.8f, 0.8f, 0.8f);
                card.Add(info);
                _scrollView.Add(card);

                var hint = new Label("Enter PlayMode to inspect the live initialization sequence and reverse teardown order.");
                hint.style.marginTop = 12;
                hint.style.color = new Color(0.6f, 0.8f, 1f);
                hint.style.alignSelf = Align.Center;
                _scrollView.Add(hint);
                return;
            }

            if (!RuntimePipelineEditorBridge.TryGetActivePipeline(out var pipeline) || pipeline == null)
            {
                var waiting = new Label("PlayMode Active: Waiting for pipeline initialization...");
                waiting.style.color = new Color(1f, 0.8f, 0.2f);
                waiting.style.fontSize = 12;
                _scrollView.Add(waiting);
                return;
            }

            var builder = pipeline.Builder;

            // Global Scope Timeline
            DrawScopeTimeline(builder.GlobalContext as GameContext, "Global Scope Initialization Sequence", "Global");

            // Session Scope Timeline
            DrawScopeTimeline(builder.SessionContext, "Session Scope Initialization Sequence", "Session");

            // Scene Scope Timeline
            DrawScopeTimeline(builder.SceneContext, "Scene Scope Initialization Sequence", "Scene");

            // Module Scope Timeline
            DrawScopeTimeline(builder.ModuleContext, "Module Scope Initialization Sequence", "Module");
        }

        private void DrawScopeTimeline(GameContext? context, string titleText, string scopeName)
        {
            if (context == null)
                return;

            var card = new VisualElement();
            card.AddToClassList("rf-card");

            var title = new Label($"{titleText} ({context.InitializationOrder.Count} services)");
            title.AddToClassList("rf-card-title");
            card.Add(title);

            var list = context.InitializationOrder;
            if (list.Count == 0)
            {
                var empty = new Label("• No async services initialized yet in this scope");
                empty.style.color = new Color(0.6f, 0.6f, 0.6f);
                empty.style.fontSize = 11;
                card.Add(empty);
            }
            else
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var binding = list[i];
                    var row = new VisualElement();
                    row.style.flexDirection = FlexDirection.Row;
                    row.style.justifyContent = Justify.SpaceBetween;
                    row.style.alignItems = Align.Center;
                    row.style.marginBottom = 3;
                    row.style.paddingLeft = 4;
                    row.style.paddingRight = 4;

                    var left = new VisualElement();
                    left.style.flexDirection = FlexDirection.Row;
                    left.style.alignItems = Align.Center;

                    var indexLabel = new Label($"#{i + 1:D2} ");
                    indexLabel.style.fontSize = 11;
                    indexLabel.style.color = new Color(0.5f, 0.8f, 1f);
                    indexLabel.style.unityFontStyleAndWeight = FontStyle.Bold;

                    var nameLabel = new Label(binding.ServiceType.Name);
                    nameLabel.style.fontSize = 11;
                    nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;

                    var implLabel = new Label($" ({binding.ImplementationType.Name})");
                    implLabel.style.fontSize = 10;
                    implLabel.style.color = new Color(0.6f, 0.6f, 0.6f);

                    left.Add(indexLabel);
                    left.Add(nameLabel);
                    left.Add(implLabel);

                    var teardownLabel = new Label($"Teardown #{list.Count - i:D2}");
                    teardownLabel.style.fontSize = 10;
                    teardownLabel.style.color = new Color(1f, 0.7f, 0.4f);

                    row.Add(left);
                    row.Add(teardownLabel);
                    card.Add(row);
                }
            }

            _scrollView.Add(card);
        }
    }
}
#endif
