#if UNITY_EDITOR
using System;
using RuntimeFlow.Contexts;
using RuntimeFlow.Editor.Dashboard.Adapters;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace RuntimeFlow.Editor.Dashboard.Views
{
    public sealed class PipelineStatusView : VisualElement
    {
        private readonly ScrollView _scrollView;

        public PipelineStatusView()
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
                var title = new Label("Pipeline Lifecycle & Health Model");
                title.AddToClassList("rf-card-title");
                card.Add(title);

                var info = new Label(
                    "Runtime Pipeline Lifecycle:\n" +
                    "• ColdStart → Initializing → Ready\n" +
                    "• RunningFlow → Finalizing → Ready\n" +
                    "• Recovering (Auto-session restart after health anomaly) → Degraded / Ready\n" +
                    "• Disposed (Full reverse teardown)\n\n" +
                    "Health Supervisor:\n" +
                    "• Wave stall detection\n" +
                    "• Hardware profile baselines\n" +
                    "• Gated services (IUserInteractionGatedInitializableService)");
                info.style.fontSize = 11;
                info.style.color = new Color(0.8f, 0.8f, 0.8f);
                card.Add(info);
                _scrollView.Add(card);

                var hint = new Label("Enter PlayMode to observe live runtime status, execution phase, readiness gates, and health telemetry.");
                hint.style.marginTop = 12;
                hint.style.color = new Color(0.6f, 0.8f, 1f);
                hint.style.alignSelf = Align.Center;
                _scrollView.Add(hint);
                return;
            }

            if (!RuntimePipelineEditorBridge.TryGetActivePipeline(out var pipeline) || pipeline == null)
            {
                var waiting = new Label("PlayMode Active: Pipeline not yet created or running.");
                waiting.style.color = new Color(1f, 0.8f, 0.2f);
                waiting.style.fontSize = 12;
                _scrollView.Add(waiting);
                return;
            }

            var status = pipeline.GetRuntimeStatus();
            var exec = pipeline.GetExecutionContext();
            var readiness = pipeline.GetReadinessStatus();
            var restartReadiness = pipeline.GetRestartReadiness();

            // Status Card
            var statusCard = new VisualElement();
            statusCard.AddToClassList("rf-card");

            var statusHeader = new VisualElement();
            statusHeader.style.flexDirection = FlexDirection.Row;
            statusHeader.style.justifyContent = Justify.SpaceBetween;
            statusHeader.style.alignItems = Align.Center;

            var statusTitle = new Label("Active Pipeline Status");
            statusTitle.AddToClassList("rf-card-title");
            statusTitle.style.marginBottom = 0;
            statusHeader.Add(statusTitle);

            var stateBadge = new Label(status.State.ToString());
            stateBadge.AddToClassList("rf-badge");
            stateBadge.AddToClassList(GetStateBadgeClass(status.State));
            statusHeader.Add(stateBadge);
            statusCard.Add(statusHeader);

            AddPropertyRow(statusCard, "Execution State", status.State.ToString());
            AddPropertyRow(statusCard, "Execution Phase", exec.Phase.ToString());
            AddPropertyRow(statusCard, "Operation Code", status.CurrentOperationCode ?? "None");
            AddPropertyRow(statusCard, "Message", status.Message ?? string.Empty);
            AddPropertyRow(statusCard, "Is Ready", status.IsReady ? "YES" : "NO");
            AddPropertyRow(statusCard, "Is Replay", exec.IsReplay.ToString());
            AddPropertyRow(statusCard, "Updated (UTC)", status.UpdatedAtUtc.ToString("HH:mm:ss.fff"));
            _scrollView.Add(statusCard);

            // Failure / Anomaly Diagnostic Card
            if (status.State == RuntimeExecutionState.Failed || !string.IsNullOrEmpty(status.LastErrorType) || !string.IsNullOrEmpty(status.BlockingReasonCode))
            {
                var errorCard = new VisualElement();
                errorCard.AddToClassList("rf-card");
                errorCard.style.borderLeftWidth = 4;
                errorCard.style.borderLeftColor = new Color(0.9f, 0.2f, 0.2f);
                errorCard.style.backgroundColor = new Color(0.25f, 0.12f, 0.12f);

                var errorTitle = new Label("⚠ Flow Failure Diagnostic");
                errorTitle.AddToClassList("rf-card-title");
                errorTitle.style.color = new Color(1f, 0.4f, 0.4f);
                errorCard.Add(errorTitle);

                if (!string.IsNullOrEmpty(status.LastErrorType))
                    AddPropertyRow(errorCard, "Error Type", status.LastErrorType);
                if (!string.IsNullOrEmpty(status.LastErrorMessage))
                    AddPropertyRow(errorCard, "Error Detail", status.LastErrorMessage);
                if (!string.IsNullOrEmpty(status.BlockingReasonCode))
                    AddPropertyRow(errorCard, "Blocking Reason Code", status.BlockingReasonCode);
                if (!string.IsNullOrEmpty(status.Message))
                    AddPropertyRow(errorCard, "Failure Message", status.Message);

                var hint = new Label("• Pipeline execution halted. Inspect the failing service in the Initialization DAG or trigger a recovery restart.");
                hint.style.fontSize = 10;
                hint.style.color = new Color(1f, 0.7f, 0.7f);
                hint.style.marginTop = 6;
                errorCard.Add(hint);

                _scrollView.Add(errorCard);
            }

            // Health & Recovery Card
            var healthCard = new VisualElement();
            healthCard.AddToClassList("rf-card");
            var healthTitle = new Label("Health Supervisor & Readiness");
            healthTitle.AddToClassList("rf-card-title");
            healthCard.Add(healthTitle);

            AddPropertyRow(healthCard, "Health Supervisor Enabled", pipeline.HealthSupervisor.IsEnabled.ToString());
            AddPropertyRow(healthCard, "Restart Readiness", restartReadiness.IsReady ? "Ready" : $"Blocked ({restartReadiness.BlockingReasonCode})");
            if (!string.IsNullOrEmpty(restartReadiness.BlockingReason))
                AddPropertyRow(healthCard, "Restart Blocking Reason", restartReadiness.BlockingReason);
            AddPropertyRow(healthCard, "Readiness Gate", readiness.IsReady ? "Passed" : $"Blocked ({readiness.BlockingReasonCode})");
            _scrollView.Add(healthCard);

            // Interactive Controls Card
            var actionsCard = new VisualElement();
            actionsCard.AddToClassList("rf-card");
            var actionsTitle = new Label("Interactive Controls");
            actionsTitle.AddToClassList("rf-card-title");
            actionsCard.Add(actionsTitle);

            var btnRow = new VisualElement();
            btnRow.style.flexDirection = FlexDirection.Row;
            btnRow.style.marginTop = 6;

            var restartBtn = new Button(async () => await RuntimePipelineEditorBridge.TriggerRestartSessionAsync())
            {
                text = "Restart Session"
            };
            restartBtn.AddToClassList("rf-action-button");
            restartBtn.AddToClassList("rf-action-button--primary");
            btnRow.Add(restartBtn);

            var dumpBtn = new Button(() =>
            {
                var dump = RuntimePipelineEditorBridge.GenerateDiagnosticsDumpJson();
                Debug.Log($"[RuntimeFlow Diagnostics Dump]\n{dump}");
            })
            {
                text = "Log Diagnostics Dump"
            };
            dumpBtn.AddToClassList("rf-action-button");
            btnRow.Add(dumpBtn);

            actionsCard.Add(btnRow);
            _scrollView.Add(actionsCard);
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

        private static string GetStateBadgeClass(RuntimeExecutionState state) => state switch
        {
            RuntimeExecutionState.Ready => "rf-badge--green",
            RuntimeExecutionState.Initializing => "rf-badge--blue",
            RuntimeExecutionState.Recovering or RuntimeExecutionState.Degraded => "rf-badge--yellow",
            RuntimeExecutionState.Failed => "rf-badge--red",
            _ => "rf-badge--gray"
        };
    }
}
#endif
