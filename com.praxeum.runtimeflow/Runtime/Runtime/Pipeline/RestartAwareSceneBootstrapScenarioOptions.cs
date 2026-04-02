using System;

namespace RuntimeFlow.Contexts
{
    public sealed class RestartAwareSceneBootstrapScenarioOptions
    {
        private static readonly Func<PreBootstrapStageStatus, string, string> DefaultPreBootstrapReasonCodeResolver =
            ResolveDefaultPreBootstrapReasonCode;

        public string? SceneName { get; set; }
        public IPreBootstrapStageService? PreBootstrapStageService { get; set; }
        public IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>? LoadingState { get; set; }
        public Type? ReplayReloadScopeType { get; set; }

        public string RunStageName { get; set; } = "run";
        public string PreBootstrapStageName { get; set; } = "prebootstrap";

        public string RunStartReasonCode { get; set; } = "runtime-flow.run.started";
        public string ReplayRunStartReasonCode { get; set; } = "runtime-flow.replay.started";
        public string RunCompleteReasonCode { get; set; } = "runtime-flow.run.completed";
        public string RunFailReasonCode { get; set; } = "runtime-flow.run.failed";

        public Func<PreBootstrapStageStatus, string, string>? PreBootstrapReasonCodeResolver { get; set; } =
            DefaultPreBootstrapReasonCodeResolver;

        public string? PreBootstrapFailedReasonCodeFallback { get; set; } = "prebootstrap.failed";
        public string? PreBootstrapFailedDiagnosticFallback { get; set; } =
            "Prebootstrap stage reported failed status before runtime flow initialization.";

        internal void Validate()
        {
            SceneName = Require(SceneName, nameof(SceneName), "Session scene name is required.");
            if (ReplayReloadScopeType == null)
            {
                throw new ArgumentException(
                    "Replay reload scope type is required.",
                    nameof(ReplayReloadScopeType));
            }

            RunStageName = Require(RunStageName, nameof(RunStageName), "Run stage name is required.");
            PreBootstrapStageName = Require(PreBootstrapStageName, nameof(PreBootstrapStageName), "Prebootstrap stage name is required.");

            RunStartReasonCode = Require(RunStartReasonCode, nameof(RunStartReasonCode), "Run start reason code is required.");
            ReplayRunStartReasonCode = Require(
                ReplayRunStartReasonCode,
                nameof(ReplayRunStartReasonCode),
                "Replay run start reason code is required.");
            RunCompleteReasonCode = Require(
                RunCompleteReasonCode,
                nameof(RunCompleteReasonCode),
                "Run completion reason code is required.");
            RunFailReasonCode = Require(RunFailReasonCode, nameof(RunFailReasonCode), "Run fail reason code is required.");

            PreBootstrapFailedReasonCodeFallback = Normalize(PreBootstrapFailedReasonCodeFallback);
            PreBootstrapFailedDiagnosticFallback = Normalize(PreBootstrapFailedDiagnosticFallback);
            PreBootstrapReasonCodeResolver ??= DefaultPreBootstrapReasonCodeResolver;
        }

        private static string Require(string? value, string parameterName, string message)
        {
            var normalized = Normalize(value);
            if (normalized == null)
                throw new ArgumentException(message, parameterName);
            return normalized;
        }

        private static string? Normalize(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string ResolveDefaultPreBootstrapReasonCode(PreBootstrapStageStatus status, string reasonCode)
        {
            var normalizedReasonCode = Normalize(reasonCode);
            if (!string.IsNullOrEmpty(normalizedReasonCode))
            {
                return normalizedReasonCode switch
                {
                    "prebootstrap.failed.transient" => "prebootstrap.failed",
                    "prebootstrap.failed.permanent" => "prebootstrap.failed",
                    _ => normalizedReasonCode
                };
            }

            return status switch
            {
                PreBootstrapStageStatus.Running => "prebootstrap.started",
                PreBootstrapStageStatus.Succeeded => "prebootstrap.succeeded",
                PreBootstrapStageStatus.Failed => "prebootstrap.failed",
                _ => null
            };
        }
    }
}
