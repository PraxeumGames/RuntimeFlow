#if UNITY_EDITOR
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using UnityEngine;
using RuntimeFlow.Pipeline;

namespace RuntimeFlow.Editor.Dashboard.Adapters
{
    public static class RuntimePipelineEditorBridge
    {
        public static bool TryGetActivePipeline(out RuntimePipeline? pipeline)
        {
            pipeline = RuntimePipeline.ActivePipeline;
            return pipeline != null;
        }

        public static bool TryGetStaticGraph(out string ruleVersion, out IReadOnlyList<RuntimeFlowCompiledInitializationGraph.Node> nodes)
        {
            try
            {
                ruleVersion = RuntimeFlowCompiledInitializationGraph.RuleVersion;
                nodes = RuntimeFlowCompiledInitializationGraph.Nodes;
                return nodes != null && nodes.Count > 0;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RuntimeFlow] Dashboard could not read the compiled initialization graph: {ex.Message}");
                ruleVersion = string.Empty;
                nodes = Array.Empty<RuntimeFlowCompiledInitializationGraph.Node>();
                return false;
            }
        }

        public static IReadOnlyList<Type> GetExplicitCatalogTypes()
        {
            try
            {
                return ExplicitTypeCatalogProvider.GetExplicitDependencyTypes();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RuntimeFlow] Dashboard could not read the explicit dependency catalog: {ex.Message}");
                return Array.Empty<Type>();
            }
        }

        public static async Task TriggerRestartSessionAsync()
        {
            if (RuntimePipeline.ActivePipeline == null)
            {
                Debug.LogWarning("[RuntimeFlow] No active RuntimePipeline in PlayMode to restart.");
                return;
            }

            try
            {
                await RuntimePipeline.ActivePipeline.RestartSessionAsync(
                    new RuntimeRestartRequest("editor.dashboard.restart", "Triggered from RuntimeFlow Dashboard"),
                    CancellationToken.None);
                Debug.Log("[RuntimeFlow] Session restart completed successfully.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RuntimeFlow] Session restart failed: {ex.Message}");
            }
        }

        public static async Task TriggerReloadModuleAsync(Type moduleScopeKey)
        {
            if (RuntimePipeline.ActivePipeline == null)
            {
                Debug.LogWarning("[RuntimeFlow] No active RuntimePipeline in PlayMode.");
                return;
            }

            try
            {
                await RuntimePipeline.ActivePipeline.ReloadModuleAsync(moduleScopeKey, cancellationToken: CancellationToken.None);
                Debug.Log($"[RuntimeFlow] Module '{moduleScopeKey.Name}' reloaded successfully.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RuntimeFlow] Module reload failed: {ex.Message}");
            }
        }

        public static string GenerateDiagnosticsDumpJson()
        {
            var isPlaying = Application.isPlaying;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"timestampUtc\": \"{DateTimeOffset.UtcNow:O}\",");
            sb.AppendLine($"  \"isPlaying\": {isPlaying.ToString().ToLowerInvariant()},");

            if (TryGetStaticGraph(out var ruleVersion, out var nodes))
            {
                sb.AppendLine($"  \"graphRuleVersion\": \"{JsonEscape(ruleVersion)}\",");
                sb.AppendLine($"  \"staticNodeCount\": {nodes.Count},");
            }

            if (TryGetActivePipeline(out var pipeline) && pipeline != null)
            {
                var status = pipeline.GetRuntimeStatus();
                var exec = pipeline.GetExecutionContext();
                sb.AppendLine("  \"pipeline\": {");
                sb.AppendLine($"    \"state\": \"{status.State}\",");
                sb.AppendLine($"    \"phase\": \"{exec.Phase}\",");
                sb.AppendLine($"    \"operationCode\": \"{JsonEscape(status.CurrentOperationCode)}\",");
                sb.AppendLine($"    \"message\": \"{JsonEscape(status.Message)}\",");
                sb.AppendLine($"    \"isReady\": {status.IsReady.ToString().ToLowerInvariant()}");
                sb.AppendLine("  },");

                var global = pipeline.Builder.GlobalContext as GameContext;
                var session = pipeline.Builder.SessionContext;
                var scene = pipeline.Builder.SceneContext;
                var module = pipeline.Builder.ModuleContext;

                sb.AppendLine("  \"scopes\": {");
                sb.AppendLine($"    \"globalInitialized\": {(global?.IsInitialized == true).ToString().ToLowerInvariant()},");
                sb.AppendLine($"    \"sessionInitialized\": {(session?.IsInitialized == true).ToString().ToLowerInvariant()},");
                sb.AppendLine($"    \"activeScene\": \"{JsonEscape(pipeline.Builder.ActiveSceneScopeKey?.Name ?? "None")}\",");
                sb.AppendLine($"    \"activeModule\": \"{JsonEscape(pipeline.Builder.ActiveModuleScopeKey?.Name ?? "None")}\",");
                sb.AppendLine($"    \"preloadedCount\": {pipeline.Builder.PreloadedContexts.Count},");
                sb.AppendLine($"    \"additiveModulesCount\": {pipeline.Builder.AdditiveModuleContexts.Count}");
                sb.AppendLine("  }");
            }
            else
            {
                sb.AppendLine("  \"pipeline\": null");
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string JsonEscape(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new System.Text.StringBuilder(value.Length + 8);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
#endif
