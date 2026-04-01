using System;
using System.Collections.Generic;

namespace RuntimeFlow.Contexts
{
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

    /// <summary>
    /// Thread-safe generic stage state store for pipeline-like orchestrations.
    /// </summary>
    public sealed class RuntimePipelineStageStateStore<TStage>
        : IRuntimePipelineStageStateProvider<TStage, RuntimePipelineStageSnapshot<TStage>>
    {
        private readonly object _sync = new object();
        private readonly Func<DateTimeOffset> _timestampProvider;
        private RuntimePipelineStageSnapshot<TStage> _snapshot;
        private bool _isStopped;

        public RuntimePipelineStageStateStore(
            TStage initialStage,
            Func<DateTimeOffset> timestampProvider = null)
        {
            if (ReferenceEquals(initialStage, null))
                throw new ArgumentNullException(nameof(initialStage));

            _timestampProvider = timestampProvider ?? (() => DateTimeOffset.UtcNow);
            _snapshot = new RuntimePipelineStageSnapshot<TStage>(
                initialStage,
                RuntimePipelineStageState.NotStarted,
                _timestampProvider());
        }

        public bool IsStopped
        {
            get
            {
                lock (_sync)
                {
                    return _isStopped;
                }
            }
        }

        public RuntimePipelineStageSnapshot<TStage> Snapshot
        {
            get
            {
                lock (_sync)
                {
                    return _snapshot;
                }
            }
        }

        public bool IsStageActive(TStage stage)
        {
            if (ReferenceEquals(stage, null))
                return false;

            lock (_sync)
            {
                return _snapshot.State == RuntimePipelineStageState.Running
                       && EqualityComparer<TStage>.Default.Equals(_snapshot.Stage, stage);
            }
        }

        public void StartStage(TStage stage, string reasonCode = null, string diagnostic = null)
        {
            if (ReferenceEquals(stage, null))
                throw new ArgumentNullException(nameof(stage));

            lock (_sync)
            {
                if (_isStopped)
                    return;

                _snapshot = CreateSnapshot(
                    stage,
                    RuntimePipelineStageState.Running,
                    reasonCode,
                    diagnostic,
                    exception: null);
            }
        }

        public void CompleteStage(TStage stage, string reasonCode = null, string diagnostic = null)
        {
            if (ReferenceEquals(stage, null))
                throw new ArgumentNullException(nameof(stage));

            lock (_sync)
            {
                if (_isStopped)
                    return;

                _snapshot = CreateSnapshot(
                    stage,
                    RuntimePipelineStageState.Completed,
                    reasonCode,
                    diagnostic,
                    exception: null);
            }
        }

        public void FailStage(TStage stage, string reasonCode, Exception exception = null, string diagnostic = null)
        {
            if (ReferenceEquals(stage, null))
                throw new ArgumentNullException(nameof(stage));
            if (string.IsNullOrWhiteSpace(reasonCode))
                throw new ArgumentException("Reason code is required.", nameof(reasonCode));

            lock (_sync)
            {
                if (_isStopped)
                    return;

                _isStopped = true;
                _snapshot = CreateSnapshot(
                    stage,
                    RuntimePipelineStageState.Failed,
                    reasonCode,
                    diagnostic,
                    exception);
            }
        }

        public void Report(string diagnostic)
        {
            if (string.IsNullOrWhiteSpace(diagnostic))
                return;

            lock (_sync)
            {
                _snapshot = CreateSnapshot(
                    _snapshot.Stage,
                    _snapshot.State,
                    _snapshot.ReasonCode,
                    diagnostic,
                    exception: null,
                    preserveError: true);
            }
        }

        public void Stop(string reasonCode, string diagnostic = null, Exception exception = null)
        {
            if (string.IsNullOrWhiteSpace(reasonCode))
                throw new ArgumentException("Reason code is required.", nameof(reasonCode));

            lock (_sync)
            {
                if (_isStopped)
                    return;

                _isStopped = true;
                _snapshot = CreateSnapshot(
                    _snapshot.Stage,
                    RuntimePipelineStageState.Stopped,
                    reasonCode,
                    diagnostic,
                    exception);
            }
        }

        private RuntimePipelineStageSnapshot<TStage> CreateSnapshot(
            TStage stage,
            RuntimePipelineStageState state,
            string reasonCode,
            string diagnostic,
            Exception exception,
            bool preserveError = false)
        {
            return new RuntimePipelineStageSnapshot<TStage>(
                stage,
                state,
                _timestampProvider(),
                reasonCode,
                diagnostic,
                preserveError ? _snapshot.ErrorType : exception?.GetType().Name,
                preserveError ? _snapshot.ErrorMessage : exception?.Message);
        }
    }
}
