using System;
using System.Collections.Generic;
using System.Linq;

namespace RuntimeFlow.Contexts
{
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
