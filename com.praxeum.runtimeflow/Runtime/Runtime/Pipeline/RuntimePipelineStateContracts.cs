using System;
using System.Collections.Generic;
using System.Linq;

namespace RuntimeFlow.Contexts
{
    public static class RuntimePipelineStateReasonCodes
    {
        public const string PipelineStopped = "runtime.pipeline.stopped";
    }

    public interface IRuntimePipelineStateQuery
    {
        RuntimeStatus GetRuntimeStatus();
        RuntimeReadinessStatus GetReadinessStatus();
    }

    public interface IRuntimePipelineStateProvider : IRuntimePipelineStateQuery
    {
    }

    public enum RuntimePipelineStageState
    {
        NotStarted = 0,
        Running = 1,
        Completed = 2,
        Failed = 3,
        Stopped = 4
    }

    public sealed class RuntimePipelineStageSnapshot<TStage>
    {
        public RuntimePipelineStageSnapshot(
            TStage stage,
            RuntimePipelineStageState state,
            DateTimeOffset updatedAtUtc,
            string reasonCode = null,
            string diagnostic = null,
            string errorType = null,
            string errorMessage = null)
        {
            if (ReferenceEquals(stage, null))
                throw new ArgumentNullException(nameof(stage));

            Stage = stage;
            State = state;
            UpdatedAtUtc = updatedAtUtc;
            ReasonCode = Normalize(reasonCode);
            Diagnostic = Normalize(diagnostic);
            ErrorType = Normalize(errorType);
            ErrorMessage = Normalize(errorMessage);
        }

        public TStage Stage { get; }
        public RuntimePipelineStageState State { get; }
        public DateTimeOffset UpdatedAtUtc { get; }
        public string ReasonCode { get; }
        public string Diagnostic { get; }
        public string ErrorType { get; }
        public string ErrorMessage { get; }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public interface IRuntimePipelineStageSnapshotObserver<TStage>
    {
        void OnSnapshot(RuntimePipelineStageSnapshot<TStage> snapshot);
    }

    internal sealed class NullRuntimePipelineStageSnapshotObserver<TStage> : IRuntimePipelineStageSnapshotObserver<TStage>
    {
        public static readonly IRuntimePipelineStageSnapshotObserver<TStage> Instance = new NullRuntimePipelineStageSnapshotObserver<TStage>();

        private NullRuntimePipelineStageSnapshotObserver()
        {
        }

        public void OnSnapshot(RuntimePipelineStageSnapshot<TStage> snapshot)
        {
        }
    }

    public interface IRuntimePipelineStageStateQuery<in TStage, out TSnapshot>
    {
        bool IsStopped { get; }
        TSnapshot Snapshot { get; }
        bool IsStageActive(TStage stage);
    }

    public interface IRuntimePipelineStageStateProvider<in TStage, out TSnapshot>
        : IRuntimePipelineStageStateQuery<TStage, TSnapshot>
    {
        void StartStage(TStage stage, string reasonCode = null, string diagnostic = null);
        void CompleteStage(TStage stage, string reasonCode = null, string diagnostic = null);
        void FailStage(TStage stage, string reasonCode, Exception exception = null, string diagnostic = null);
        void Report(string diagnostic);
        void Stop(string reasonCode, string diagnostic = null, Exception exception = null);
    }

}
