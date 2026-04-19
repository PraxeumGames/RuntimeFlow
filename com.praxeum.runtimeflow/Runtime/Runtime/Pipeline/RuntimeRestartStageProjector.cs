using System;

namespace RuntimeFlow.Contexts
{
    public sealed class RuntimeRestartStageProjectionOptions<TStage>
    {
        public RuntimeRestartStageProjectionOptions(
            TStage stage,
            string preparingReasonCode,
            string duplicateReasonCode,
            string completedReasonCode,
            string failedReasonCode,
            string timedOutReasonCode,
            string lifecycleManagerMissingReasonCode)
        {
            if (ReferenceEquals(stage, null))
                throw new ArgumentNullException(nameof(stage));
            if (string.IsNullOrWhiteSpace(failedReasonCode))
                throw new ArgumentException("Failed reason code is required.", nameof(failedReasonCode));
            if (string.IsNullOrWhiteSpace(timedOutReasonCode))
                throw new ArgumentException("Timed out reason code is required.", nameof(timedOutReasonCode));
            if (string.IsNullOrWhiteSpace(lifecycleManagerMissingReasonCode))
            {
                throw new ArgumentException(
                    "Lifecycle manager missing reason code is required.",
                    nameof(lifecycleManagerMissingReasonCode));
            }

            Stage = stage;
            PreparingReasonCode = Normalize(preparingReasonCode);
            DuplicateReasonCode = Normalize(duplicateReasonCode);
            CompletedReasonCode = Normalize(completedReasonCode);
            FailedReasonCode = Normalize(failedReasonCode);
            TimedOutReasonCode = Normalize(timedOutReasonCode);
            LifecycleManagerMissingReasonCode = Normalize(lifecycleManagerMissingReasonCode);
        }

        public TStage Stage { get; }
        public string? PreparingReasonCode { get; }
        public string? DuplicateReasonCode { get; }
        public string? CompletedReasonCode { get; }
        public string? FailedReasonCode { get; }
        public string? TimedOutReasonCode { get; }
        public string? LifecycleManagerMissingReasonCode { get; }

        private static string? Normalize(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public static class RuntimeRestartStageProjector
    {
        public static bool TryProjectDispatch<TStage, TSnapshot>(
            RuntimeRestartDispatch dispatch,
            IRuntimePipelineStageStateProvider<TStage, TSnapshot> stageStateProvider,
            RuntimeRestartStageProjectionOptions<TStage> options,
            string? diagnostic = null)
        {
            if (stageStateProvider == null)
                throw new ArgumentNullException(nameof(stageStateProvider));
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            if (!dispatch.IsAccepted)
            {
                stageStateProvider.StartStage(options.Stage, options.DuplicateReasonCode, diagnostic);
                return false;
            }

            stageStateProvider.StartStage(options.Stage, options.PreparingReasonCode, diagnostic);
            return true;
        }

        public static void ProjectOutcome<TStage, TSnapshot>(
            RuntimeRestartExecutionResult result,
            IRuntimePipelineStageStateProvider<TStage, TSnapshot> stageStateProvider,
            RuntimeRestartStageProjectionOptions<TStage> options,
            string? diagnostic = null,
            Func<RuntimeRestartExecutionResult, string>? timeoutDiagnosticResolver = null,
            Func<RuntimeRestartExecutionResult, string>? lifecycleMissingDiagnosticResolver = null,
            Func<RuntimeRestartExecutionResult, Exception>? failedExceptionResolver = null)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));
            if (stageStateProvider == null)
                throw new ArgumentNullException(nameof(stageStateProvider));
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            switch (result.Outcome)
            {
                case RuntimeRestartExecutionOutcome.Completed:
                    stageStateProvider.CompleteStage(options.Stage, options.CompletedReasonCode, diagnostic);
                    break;
                case RuntimeRestartExecutionOutcome.LifecycleManagerMissing:
                    stageStateProvider.FailStage(
                        options.Stage,
                        options.LifecycleManagerMissingReasonCode,
                        diagnostic: lifecycleMissingDiagnosticResolver?.Invoke(result) ?? diagnostic);
                    break;
                case RuntimeRestartExecutionOutcome.TimedOut:
                    stageStateProvider.Stop(
                        options.TimedOutReasonCode,
                        timeoutDiagnosticResolver?.Invoke(result) ?? diagnostic);
                    break;
                case RuntimeRestartExecutionOutcome.Failed:
                    stageStateProvider.FailStage(
                        options.Stage,
                        options.FailedReasonCode,
                        failedExceptionResolver?.Invoke(result) ?? result.Exception,
                        diagnostic);
                    break;
                case RuntimeRestartExecutionOutcome.Deduplicated:
                    stageStateProvider.StartStage(options.Stage, options.DuplicateReasonCode, diagnostic);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}
