using System;

namespace RuntimeFlow.Contexts
{
    public sealed class RestartAwareSceneBootstrapScenarioOptions
    {
        public string? SceneName { get; set; }
        public IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>? LoadingState { get; set; }
        public Type? ReplayReloadScopeType { get; set; }

        public string RunStageName { get; set; } = "run";

        public string RunStartReasonCode { get; set; } = "runtime-flow.run.started";
        public string ReplayRunStartReasonCode { get; set; } = "runtime-flow.replay.started";
        public string RunCompleteReasonCode { get; set; } = "runtime-flow.run.completed";
        public string RunFailReasonCode { get; set; } = "runtime-flow.run.failed";

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
    }
}
