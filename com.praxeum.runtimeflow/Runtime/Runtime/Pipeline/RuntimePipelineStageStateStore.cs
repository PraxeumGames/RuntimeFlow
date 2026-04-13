using System;
using System.Collections.Generic;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Thread-safe generic stage state store for pipeline-like orchestrations.
    /// </summary>
    public sealed class RuntimePipelineStageStateStore<TStage>
        : IRuntimePipelineStageStateProvider<TStage, RuntimePipelineStageSnapshot<TStage>>
    {
        private readonly object _sync = new object();
        private readonly Func<DateTimeOffset> _timestampProvider;
        private readonly IRuntimePipelineStageSnapshotObserver<TStage> _snapshotObserver;
        private RuntimePipelineStageSnapshot<TStage> _snapshot;
        private bool _isStopped;

        public RuntimePipelineStageStateStore(
            TStage initialStage,
            Func<DateTimeOffset> timestampProvider = null,
            IRuntimePipelineStageSnapshotObserver<TStage> snapshotObserver = null)
        {
            if (ReferenceEquals(initialStage, null))
                throw new ArgumentNullException(nameof(initialStage));

            _timestampProvider = timestampProvider ?? (() => DateTimeOffset.UtcNow);
            _snapshotObserver = snapshotObserver ?? NullRuntimePipelineStageSnapshotObserver<TStage>.Instance;
            _snapshot = new RuntimePipelineStageSnapshot<TStage>(
                initialStage,
                RuntimePipelineStageState.NotStarted,
                _timestampProvider());
            _snapshotObserver.OnSnapshot(_snapshot);
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

            RuntimePipelineStageSnapshot<TStage> snapshot;
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
                snapshot = _snapshot;
            }

            _snapshotObserver.OnSnapshot(snapshot);
        }

        public void CompleteStage(TStage stage, string reasonCode = null, string diagnostic = null)
        {
            if (ReferenceEquals(stage, null))
                throw new ArgumentNullException(nameof(stage));

            RuntimePipelineStageSnapshot<TStage> snapshot;
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
                snapshot = _snapshot;
            }

            _snapshotObserver.OnSnapshot(snapshot);
        }

        public void FailStage(TStage stage, string reasonCode, Exception exception = null, string diagnostic = null)
        {
            if (ReferenceEquals(stage, null))
                throw new ArgumentNullException(nameof(stage));
            if (string.IsNullOrWhiteSpace(reasonCode))
                throw new ArgumentException("Reason code is required.", nameof(reasonCode));

            RuntimePipelineStageSnapshot<TStage> snapshot;
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
                snapshot = _snapshot;
            }

            _snapshotObserver.OnSnapshot(snapshot);
        }

        public void Report(string diagnostic)
        {
            if (string.IsNullOrWhiteSpace(diagnostic))
                return;

            RuntimePipelineStageSnapshot<TStage> snapshot;
            lock (_sync)
            {
                _snapshot = CreateSnapshot(
                    _snapshot.Stage,
                    _snapshot.State,
                    _snapshot.ReasonCode,
                    diagnostic,
                    exception: null,
                    preserveError: true);
                snapshot = _snapshot;
            }

            _snapshotObserver.OnSnapshot(snapshot);
        }

        public void Stop(string reasonCode, string diagnostic = null, Exception exception = null)
        {
            if (string.IsNullOrWhiteSpace(reasonCode))
                throw new ArgumentException("Reason code is required.", nameof(reasonCode));

            RuntimePipelineStageSnapshot<TStage> snapshot;
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
                snapshot = _snapshot;
            }

            _snapshotObserver.OnSnapshot(snapshot);
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
