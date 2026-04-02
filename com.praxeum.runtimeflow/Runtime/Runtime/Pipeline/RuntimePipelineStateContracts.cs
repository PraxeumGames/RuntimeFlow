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

    public sealed class RuntimePipelineStringStageStateProviderOptions
    {
        public string InitialStage { get; set; } = "unknown";

        public IReadOnlyCollection<string>? ResettableStages { get; set; }

        public IRuntimePipelineStageSnapshotObserver<string>? SnapshotObserver { get; set; }
    }

    /// <summary>
    /// Thread-safe string-stage provider with optional stopped-state reset gates.
    /// </summary>
    public sealed class RuntimePipelineStringStageStateProvider
        : IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>
    {
        private readonly object _sync = new object();
        private readonly string _initialStage;
        private readonly HashSet<string> _resettableStages;
        private readonly IRuntimePipelineStageSnapshotObserver<string> _snapshotObserver;
        private RuntimePipelineStageStateStore<string> _stateStore;

        public RuntimePipelineStringStageStateProvider(
            RuntimePipelineStringStageStateProviderOptions? options = null,
            IRuntimePipelineStageSnapshotObserver<string>? snapshotObserver = null)
        {
            options ??= new RuntimePipelineStringStageStateProviderOptions();
            _initialStage = NormalizeStage(options.InitialStage) ?? "unknown";
            _snapshotObserver = snapshotObserver
                               ?? options.SnapshotObserver
                               ?? NullRuntimePipelineStageSnapshotObserver<string>.Instance;
            _resettableStages = new HashSet<string>(
                (options.ResettableStages ?? Array.Empty<string>())
                .Select(NormalizeStage)
                .Where(stage => stage != null),
                StringComparer.Ordinal);
            _stateStore = CreateStore();
        }

        public bool IsStopped
        {
            get
            {
                lock (_sync)
                {
                    return _stateStore.IsStopped;
                }
            }
        }

        public RuntimePipelineStageSnapshot<string> Snapshot
        {
            get
            {
                lock (_sync)
                {
                    return _stateStore.Snapshot;
                }
            }
        }

        public void StartStage(string stage, string reasonCode = null, string diagnostic = null)
        {
            var normalizedStage = NormalizeStage(stage);
            if (normalizedStage == null)
            {
                throw new ArgumentException("Stage is required.", nameof(stage));
            }

            lock (_sync)
            {
                if (_stateStore.IsStopped)
                {
                    if (!_resettableStages.Contains(normalizedStage))
                    {
                        return;
                    }

                    _stateStore = CreateStore();
                }

                _stateStore.StartStage(
                    normalizedStage,
                    Normalize(reasonCode),
                    Normalize(diagnostic));
            }
        }

        public void CompleteStage(string stage, string reasonCode = null, string diagnostic = null)
        {
            var normalizedStage = NormalizeStage(stage);
            if (normalizedStage == null)
            {
                throw new ArgumentException("Stage is required.", nameof(stage));
            }

            lock (_sync)
            {
                if (_stateStore.IsStopped)
                {
                    return;
                }

                _stateStore.CompleteStage(
                    normalizedStage,
                    Normalize(reasonCode),
                    Normalize(diagnostic));
            }
        }

        public void FailStage(string stage, string reasonCode, Exception exception = null, string diagnostic = null)
        {
            var normalizedStage = NormalizeStage(stage);
            if (normalizedStage == null)
            {
                throw new ArgumentException("Stage is required.", nameof(stage));
            }

            lock (_sync)
            {
                if (_stateStore.IsStopped)
                {
                    return;
                }

                _stateStore.FailStage(
                    normalizedStage,
                    Normalize(reasonCode) ?? RuntimePipelineStateReasonCodes.PipelineStopped,
                    exception,
                    Normalize(diagnostic) ?? exception?.Message);
            }
        }

        public void Report(string diagnostic)
        {
            var normalizedDiagnostic = Normalize(diagnostic);
            if (normalizedDiagnostic == null)
            {
                return;
            }

            lock (_sync)
            {
                _stateStore.Report(normalizedDiagnostic);
            }
        }

        public void Stop(string reasonCode, string diagnostic = null, Exception exception = null)
        {
            lock (_sync)
            {
                if (_stateStore.IsStopped)
                {
                    return;
                }

                _stateStore.Stop(
                    Normalize(reasonCode) ?? RuntimePipelineStateReasonCodes.PipelineStopped,
                    Normalize(diagnostic),
                    exception);
            }
        }

        public bool IsStageActive(string stage)
        {
            var normalizedStage = NormalizeStage(stage);
            if (normalizedStage == null)
            {
                return false;
            }

            lock (_sync)
            {
                return _stateStore.IsStageActive(normalizedStage);
            }
        }

        private RuntimePipelineStageStateStore<string> CreateStore()
        {
            return new RuntimePipelineStageStateStore<string>(
                _initialStage,
                snapshotObserver: _snapshotObserver);
        }

        private static string NormalizeStage(string value)
        {
            var normalized = Normalize(value);
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
