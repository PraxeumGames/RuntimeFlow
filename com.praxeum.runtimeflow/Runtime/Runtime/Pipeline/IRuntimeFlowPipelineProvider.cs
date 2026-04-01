using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    public interface IRuntimeFlowPipelineProvider
    {
        bool HasCurrent { get; }
        RuntimePipeline Current { get; }
        bool TryGetCurrent(out RuntimePipeline pipeline);
        void SetCurrent(RuntimePipeline pipeline, IGameSceneLoader sceneLoader);
        void ClearCurrent(RuntimePipeline pipeline);
        bool IsReplayInProgress();
        bool IsReplayReady();
        string DescribeCurrentStatus();
        RuntimeReadinessStatus GetRuntimeReadinessStatus();
        IRuntimeRestartCoordinator CreateRestartCoordinator(
            Func<RuntimeReadinessStatus> runtimeReadinessProvider = null,
            Func<DateTimeOffset> timestampProvider = null);
        Task ReplayCurrentFlowAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Thread-safe storage for the current runtime pipeline and scene loader.
    /// Provides replay-aware status projection and flow replay orchestration.
    /// </summary>
    public sealed class RuntimeFlowPipelineProvider : IRuntimeFlowPipelineProvider
    {
        private readonly object _sync = new object();
        private RuntimePipeline _current;
        private IGameSceneLoader _sceneLoader;

        public bool HasCurrent
        {
            get
            {
                lock (_sync)
                {
                    return _current != null;
                }
            }
        }

        public RuntimePipeline Current
        {
            get
            {
                lock (_sync)
                {
                    if (_current == null)
                        throw new InvalidOperationException("RuntimeFlow pipeline is not initialized yet.");

                    return _current;
                }
            }
        }

        public bool TryGetCurrent(out RuntimePipeline pipeline)
        {
            lock (_sync)
            {
                pipeline = _current;
                return pipeline != null;
            }
        }

        public void SetCurrent(RuntimePipeline pipeline, IGameSceneLoader sceneLoader)
        {
            if (pipeline == null) throw new ArgumentNullException(nameof(pipeline));
            if (sceneLoader == null) throw new ArgumentNullException(nameof(sceneLoader));

            lock (_sync)
            {
                _current = pipeline;
                _sceneLoader = sceneLoader;
            }
        }

        public void ClearCurrent(RuntimePipeline pipeline)
        {
            if (pipeline == null)
                return;

            lock (_sync)
            {
                if (!ReferenceEquals(_current, pipeline))
                    return;

                _current = null;
                _sceneLoader = null;
            }
        }

        public bool IsReplayInProgress()
        {
            lock (_sync)
            {
                if (_current == null)
                    return false;

                var executionContext = _current.GetExecutionContext();
                if (executionContext != null)
                {
                    return executionContext.IsReplay
                           && executionContext.State == RuntimeExecutionState.Recovering;
                }

                return _current.GetRuntimeStatus().State == RuntimeExecutionState.Recovering;
            }
        }

        public bool IsReplayReady()
        {
            lock (_sync)
            {
                if (_current == null)
                    return false;

                var status = _current.GetRuntimeStatus();
                if (!status.IsReady || status.State == RuntimeExecutionState.Recovering)
                    return false;

                var executionContext = _current.GetExecutionContext();
                if (executionContext != null
                    && executionContext.IsReplay
                    && executionContext.State == RuntimeExecutionState.Recovering)
                {
                    return false;
                }

                return true;
            }
        }

        public string DescribeCurrentStatus()
        {
            lock (_sync)
            {
                if (_current == null)
                    return "pipeline-missing";

                var status = _current.GetRuntimeStatus();
                var executionContext = _current.GetExecutionContext();
                if (executionContext != null)
                {
                    return
                        $"{status.State}/{status.CurrentOperationCode ?? "<none>"}/{executionContext.Phase}/{executionContext.IsReplay}";
                }

                return $"{status.State}/{status.CurrentOperationCode ?? "<none>"}/{status.Message ?? "<none>"}";
            }
        }

        public RuntimeReadinessStatus GetRuntimeReadinessStatus()
        {
            if (!TryGetCurrent(out var pipeline))
            {
                return new RuntimeReadinessStatus(
                    isReady: false,
                    updatedAtUtc: DateTimeOffset.UtcNow,
                    blockingReasonCode: "runtime.pipeline.missing",
                    blockingReason: "RuntimeFlow pipeline is not available.");
            }

            if (pipeline is IRuntimePipelineStateQuery pipelineStateQuery)
            {
                return pipelineStateQuery.GetReadinessStatus();
            }

            var runtimeStatus = pipeline.GetRuntimeStatus();
            return new RuntimeReadinessStatus(
                isReady: runtimeStatus.IsReady,
                updatedAtUtc: runtimeStatus.UpdatedAtUtc,
                currentOperationCode: runtimeStatus.CurrentOperationCode,
                blockingReasonCode: runtimeStatus.BlockingReasonCode,
                blockingReason: runtimeStatus.Message);
        }

        public IRuntimeRestartCoordinator CreateRestartCoordinator(
            Func<RuntimeReadinessStatus> runtimeReadinessProvider = null,
            Func<DateTimeOffset> timestampProvider = null)
        {
            var safeTimestampProvider = timestampProvider ?? (() => DateTimeOffset.UtcNow);
            return new RuntimeRestartCoordinator(
                readinessGateFactory: () => new RuntimeReadinessGate(
                    runtimeReadinessProvider: runtimeReadinessProvider ?? GetRuntimeReadinessStatus,
                    executionContextProvider: BuildExecutionContext,
                    restartLifecycleSnapshotProvider: BuildRestartLifecycleSnapshot,
                    timestampProvider: safeTimestampProvider),
                restartLifecycleManagerProvider: ResolveRestartLifecycleManager);
        }

        public async Task ReplayCurrentFlowAsync(CancellationToken cancellationToken = default)
        {
            RuntimePipeline pipeline;
            IGameSceneLoader sceneLoader;
            lock (_sync)
            {
                pipeline = _current;
                sceneLoader = _sceneLoader;
            }

            if (pipeline == null)
                throw new InvalidOperationException("RuntimeFlow pipeline replay is not initialized yet.");
            if (sceneLoader == null)
                throw new InvalidOperationException("RuntimeFlow scene loader is not initialized yet.");

            using (RuntimeFlowReplayScope.Enter())
            {
                await pipeline.RunAsync(sceneLoader, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        private IRuntimeExecutionContext BuildExecutionContext()
        {
            if (!TryGetCurrent(out var pipeline))
            {
                return null;
            }

            return (pipeline as IRuntimeExecutionContextProvider)?.GetExecutionContext();
        }

        private RuntimeRestartLifecycleSnapshot BuildRestartLifecycleSnapshot()
        {
            var lifecycleManager = ResolveRestartLifecycleManager();
            return lifecycleManager?.Snapshot;
        }

        private IRuntimeRestartLifecycleManager ResolveRestartLifecycleManager()
        {
            if (!TryGetCurrent(out var pipeline))
            {
                return null;
            }

            return pipeline as IRuntimeRestartLifecycleManager;
        }
    }
}
