using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RuntimeFlow.Contexts;
using SFS.Core.GameLoading;

namespace RuntimeFlow.Tests;

public sealed class RuntimeFlowGameRestartHandlerLoggerTests
{
    [Fact]
    public void Restart_WhenLoggerInjected_WritesRestartRequestLog()
    {
        var logger = new CapturingLogger();
        var handler = CreateHandler(logger);

        handler.Restart("logger-test", forceSave: false);

        Assert.Contains(
            logger.Messages,
            message => message.Contains("Restart: create request. Reason: logger-test", StringComparison.Ordinal));
    }

    [Fact]
    public void Restart_WhenLoggerNotProvided_UsesNullLoggerFallbackWithoutThrowing()
    {
        var handler = CreateHandler(logger: null);

        var exception = Record.Exception(() => handler.Restart("fallback-test", forceSave: false));

        Assert.Null(exception);
    }

    private static RuntimeFlowGameRestartHandler CreateHandler(ILogger? logger)
    {
        return logger == null
            ? new RuntimeFlowGameRestartHandler(
                new NoopStateSaver(),
                new NoopGameDataCleaner(),
                new FakePipelineProvider(),
                new NoopStageStateProvider())
            : new RuntimeFlowGameRestartHandler(
                new NoopStateSaver(),
                new NoopGameDataCleaner(),
                new FakePipelineProvider(),
                new NoopStageStateProvider(),
                logger);
    }

    private sealed class NoopStateSaver : IGameRestartStateSaver
    {
        public void SaveAppState()
        {
        }
    }

    private sealed class NoopGameDataCleaner : IGameDataCleaner
    {
        public void ClearSecondaryUserData()
        {
        }

        public void ClearAllUserData()
        {
        }
    }

    private sealed class FakePipelineProvider : IRuntimeFlowPipelineProvider
    {
        private readonly IRuntimeRestartCoordinator _coordinator = new DeduplicatingRestartCoordinator();

        public bool HasCurrent => false;

        public RuntimePipeline Current => throw new InvalidOperationException("No runtime pipeline.");

        public bool TryGetCurrent(out RuntimePipeline pipeline)
        {
            pipeline = null!;
            return false;
        }

        public void SetCurrent(RuntimePipeline pipeline, IGameSceneLoader sceneLoader)
        {
        }

        public void ClearCurrent(RuntimePipeline pipeline)
        {
        }

        public bool IsReplayInProgress() => false;

        public bool IsReplayReady() => false;

        public string DescribeCurrentStatus() => "pipeline-missing";

        public RuntimeReadinessStatus GetRuntimeReadinessStatus()
        {
            return new RuntimeReadinessStatus(
                isReady: false,
                updatedAtUtc: DateTimeOffset.UtcNow,
                blockingReasonCode: "runtime.pipeline.missing",
                blockingReason: "RuntimeFlow pipeline is not available.");
        }

        public IRuntimeRestartCoordinator CreateRestartCoordinator(
            Func<RuntimeReadinessStatus>? runtimeReadinessProvider = null,
            Func<DateTimeOffset>? timestampProvider = null)
        {
            return _coordinator;
        }

        public Task ReplayCurrentFlowAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class DeduplicatingRestartCoordinator : IRuntimeRestartCoordinator
    {
        public RuntimeRestartDispatch Dispatch(RuntimeRestartCoordinatorRequest request)
        {
            return RuntimeRestartDispatch.Deduplicated(
                RuntimeRestartDuplicateReason.InFlight,
                Task.FromResult(
                    RuntimeRestartExecutionResult.Deduplicated(
                        request.RestartRequest,
                        RuntimeRestartDuplicateReason.InFlight)));
        }
    }

    private sealed class NoopStageStateProvider : IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>
    {
        private RuntimePipelineStageSnapshot<string> _snapshot = new(
            stage: "init",
            state: RuntimePipelineStageState.NotStarted,
            updatedAtUtc: DateTimeOffset.UtcNow);

        public bool IsStopped { get; private set; }

        public RuntimePipelineStageSnapshot<string> Snapshot => _snapshot;

        public bool IsStageActive(string stage)
        {
            return !IsStopped
                   && _snapshot.State == RuntimePipelineStageState.Running
                   && string.Equals(_snapshot.Stage, stage, StringComparison.Ordinal);
        }

        public void StartStage(string stage, string reasonCode = null, string diagnostic = null)
        {
            if (IsStopped) return;
            _snapshot = new RuntimePipelineStageSnapshot<string>(
                stage,
                RuntimePipelineStageState.Running,
                DateTimeOffset.UtcNow,
                reasonCode,
                diagnostic);
        }

        public void CompleteStage(string stage, string reasonCode = null, string diagnostic = null)
        {
            if (IsStopped) return;
            _snapshot = new RuntimePipelineStageSnapshot<string>(
                stage,
                RuntimePipelineStageState.Completed,
                DateTimeOffset.UtcNow,
                reasonCode,
                diagnostic);
        }

        public void FailStage(string stage, string reasonCode, Exception exception = null, string diagnostic = null)
        {
            if (IsStopped) return;
            _snapshot = new RuntimePipelineStageSnapshot<string>(
                stage,
                RuntimePipelineStageState.Failed,
                DateTimeOffset.UtcNow,
                reasonCode,
                diagnostic,
                exception?.GetType().Name,
                exception?.Message);
        }

        public void Report(string diagnostic)
        {
        }

        public void Stop(string reasonCode, string diagnostic = null, Exception exception = null)
        {
            IsStopped = true;
            _snapshot = new RuntimePipelineStageSnapshot<string>(
                _snapshot.Stage,
                RuntimePipelineStageState.Stopped,
                DateTimeOffset.UtcNow,
                reasonCode,
                diagnostic,
                exception?.GetType().Name,
                exception?.Message);
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullDisposable.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _messages.Enqueue(formatter(state, exception));
        }
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
