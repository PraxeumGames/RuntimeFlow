#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using RuntimeFlow.Contexts;
using RuntimeFlow.Editor.Dashboard.Adapters;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using RuntimeFlow.Pipeline;

namespace RuntimeFlow.Editor.Dashboard.Views
{
    public sealed class ScopeHierarchyView : VisualElement
    {
        private readonly ScrollView _contentScrollView;
        private readonly Label _liveStatusLabel;

        public ScopeHierarchyView()
        {
            style.flexGrow = 1;

            _contentScrollView = new ScrollView();
            _contentScrollView.style.flexGrow = 1;
            _contentScrollView.style.paddingLeft = 12;
            _contentScrollView.style.paddingRight = 12;
            _contentScrollView.style.paddingTop = 12;

            _liveStatusLabel = new Label();
            _liveStatusLabel.style.fontSize = 11;
            _liveStatusLabel.style.marginBottom = 10;
            _contentScrollView.Add(_liveStatusLabel);

            Add(_contentScrollView);
            Refresh();
        }

        public void Refresh()
        {
            _contentScrollView.Clear();

            if (!Application.isPlaying)
            {
                var editCard = new VisualElement();
                editCard.AddToClassList("rf-card");
                var title = new Label("Static Scope Hierarchy Structure");
                title.AddToClassList("rf-card-title");
                editCard.Add(title);

                var desc = new Label(
                    "Hierarchy Definition:\n" +
                    "  ► Global Scope (Persistent Application Lifetime)\n" +
                    "    └── Session Scope (Player, Account & Session Lifecycle)\n" +
                    "          ├── Active Scene Scope (Active Gameplay/UI Scene)\n" +
                    "          │     ├── Active Module Scope (Sub-feature)\n" +
                    "          │     └── Additive Module Scopes (HUD, Chat, Inventory...)\n" +
                    "          └── Standalone Additive Modules");
                desc.style.fontSize = 12;
                desc.style.color = new Color(0.8f, 0.8f, 0.8f);
                editCard.Add(desc);
                _contentScrollView.Add(editCard);

                var hint = new Label("Enter PlayMode to inspect live GameContext instances, instance counts, and active services.");
                hint.style.marginTop = 12;
                hint.style.color = new Color(0.6f, 0.8f, 1f);
                hint.style.alignSelf = Align.Center;
                _contentScrollView.Add(hint);
                return;
            }

            if (!RuntimePipelineEditorBridge.TryGetActivePipeline(out var pipeline) || pipeline == null)
            {
                var waiting = new Label("PlayMode Active: Waiting for RuntimePipeline initialization...");
                waiting.style.color = new Color(1f, 0.8f, 0.2f);
                waiting.style.fontSize = 12;
                _contentScrollView.Add(waiting);
                return;
            }

            var builder = pipeline.Builder;

            // Global Scope Card
            DrawContextCard("Global Scope (Application Lifetime)", builder.GlobalContext as GameContext, "Persistent across session restarts", GameContextType.Global);

            // Session Scope Card
            DrawContextCard("Session Scope (Player & Game Session)", builder.SessionContext, "Restartable without dropping Global state", GameContextType.Session);

            // Active Scene Scope Card
            var sceneKey = builder.ActiveSceneScopeKey;
            var sceneTitle = sceneKey != null ? $"Active Scene: {sceneKey.Name}" : "Active Scene Scope (None)";
            DrawContextCard(sceneTitle, builder.SceneContext, "Tied to currently loaded Unity scene", GameContextType.Scene);

            // Active Module Scope Card
            var moduleKey = builder.ActiveModuleScopeKey;
            var moduleTitle = moduleKey != null ? $"Active Module: {moduleKey.Name}" : "Active Module Scope (None)";
            DrawContextCard(moduleTitle, builder.ModuleContext, "Tied to active sub-feature module", GameContextType.Module);

            // Additive Modules Card
            var additiveContexts = builder.AdditiveModuleContexts;
            if (additiveContexts.Count > 0)
            {
                var additiveCard = new VisualElement();
                additiveCard.AddToClassList("rf-card");
                var addTitle = new Label($"Additive Modules ({additiveContexts.Count})");
                addTitle.AddToClassList("rf-card-title");
                additiveCard.Add(addTitle);

                foreach (var kvp in additiveContexts)
                {
                    DrawSubContextRow(additiveCard, kvp.Key.Name, kvp.Value);
                }
                _contentScrollView.Add(additiveCard);
            }

            // Preloaded Scopes Card
            var preloaded = builder.PreloadedContexts;
            if (preloaded.Count > 0)
            {
                var preloadedCard = new VisualElement();
                preloadedCard.AddToClassList("rf-card");
                var preTitle = new Label($"Preloaded Scopes ({preloaded.Count})");
                preTitle.AddToClassList("rf-card-title");
                preloadedCard.Add(preTitle);

                foreach (var kvp in preloaded)
                {
                    DrawSubContextRow(preloadedCard, kvp.Key.Name, kvp.Value);
                }
                _contentScrollView.Add(preloadedCard);
            }
        }

        private void DrawContextCard(string titleText, GameContext? context, string subtitle, GameContextType scopeType)
        {
            var card = new VisualElement();
            card.AddToClassList("rf-card");

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.alignItems = Align.Center;

            var title = new Label(titleText);
            title.AddToClassList("rf-card-title");
            title.style.marginBottom = 0;
            header.Add(title);

            var badge = new Label(context?.IsInitialized == true ? "Active" : (context != null ? "Created" : "Not Loaded"));
            badge.AddToClassList("rf-badge");
            badge.AddToClassList(context?.IsInitialized == true ? "rf-badge--green" : "rf-badge--gray");
            header.Add(badge);
            card.Add(header);

            if (!string.IsNullOrEmpty(subtitle))
            {
                var sub = new Label(subtitle);
                sub.style.fontSize = 10;
                sub.style.color = new Color(0.6f, 0.6f, 0.6f);
                sub.style.marginBottom = 6;
                card.Add(sub);
            }

            if (context != null)
            {
                AddPropertyRow(card, "Registered Services", context.RegisteredServiceTypes.Count.ToString());
                AddPropertyRow(card, "Initialized Services", context.InitializationOrder.Count.ToString());
                AddPropertyRow(card, "Is Initialized", context.IsInitialized.ToString());
                AddPropertyRow(card, "Is Disposed", context.IsDisposed.ToString());
            }
            else
            {
                var none = new Label("• Scope context is currently null / unloaded");
                none.style.fontSize = 11;
                none.style.color = new Color(0.5f, 0.5f, 0.5f);
                card.Add(none);
            }

            _contentScrollView.Add(card);
        }

        private static void DrawSubContextRow(VisualElement card, string scopeName, GameContext context)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 4;

            var name = new Label($"► {scopeName}");
            name.style.fontSize = 11;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;

            var stats = new Label($"Registered: {context.RegisteredServiceTypes.Count} | Initialized: {context.InitializationOrder.Count}");
            stats.style.fontSize = 10;
            stats.style.color = new Color(0.7f, 0.7f, 0.7f);

            row.Add(name);
            row.Add(stats);
            card.Add(row);
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
    }
}
#endif
