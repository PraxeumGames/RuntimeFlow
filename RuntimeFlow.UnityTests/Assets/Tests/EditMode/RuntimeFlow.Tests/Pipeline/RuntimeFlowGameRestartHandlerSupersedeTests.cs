using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using SFS.Core.GameLoading;

namespace RuntimeFlow.Tests
{
    /// <summary>
    /// Which restart request may supersede the restart in flight. The handler lives in the session scope and a
    /// restart rebuilds the session with a new handler: a request from the session being rebuilt comes to that
    /// new handler and may replace the rebuild (it can be waiting on the very initializer that asks), while a
    /// repeated request from the session that started the restart joins it. Whether the restart in flight is
    /// already rebuilding is decided by the coordinator (RuntimeRestartLifecycleSupersedeTests).
    /// </summary>
    public sealed class RuntimeFlowGameRestartHandlerSupersedeTests
    {
        [Test]
        public void RepeatedRequest_FromTheSessionThatStartedTheRestart_JoinsIt()
        {
            var coordinator = new ScriptedCoordinator();
            var handler = CreateHandler(coordinator);

            handler.Restart("first", forceSave: false);
            handler.Restart("second", forceSave: false);

            Assert.That(coordinator.Started, Has.Count.EqualTo(1), "A double request is one restart");
        }

        [Test]
        public void RequestFromTheReplayedSession_MaySupersedeTheRestartOfThePreviousSession()
        {
            var coordinator = new ScriptedCoordinator();
            var provider = new FakePipelineProvider(coordinator);
            var previousSession = CreateHandler(provider);
            var replayedSession = CreateHandler(provider);

            previousSession.Restart("first", forceSave: false);
            replayedSession.Restart("second", forceSave: false);

            Assert.That(coordinator.Started, Has.Count.EqualTo(2), "The request from the rebuilt session must run");
            Assert.That(coordinator.Started[1].RestartRequest.SupersedesInFlight, Is.True);
            Assert.That(replayedSession.IsApplicationRestarting, Is.True);
        }

        [Test]
        public void HandlerWhoseRestartCompleted_MayRequestANewOne()
        {
            var coordinator = new ScriptedCoordinator();
            var handler = CreateHandler(coordinator);

            handler.Restart("first", forceSave: false);
            coordinator.CompleteAll();
            handler.Restart("second", forceSave: false);

            Assert.That(coordinator.Started, Has.Count.EqualTo(2));
        }

        private static RuntimeFlowGameRestartHandler CreateHandler(ScriptedCoordinator coordinator) =>
            CreateHandler(new FakePipelineProvider(coordinator));

        private static RuntimeFlowGameRestartHandler CreateHandler(IRuntimeFlowPipelineProvider provider)
        {
            return new RuntimeFlowGameRestartHandler(
                new NoopStateSaver(),
                new NoopGameDataCleaner(),
                provider,
                new NoopStageStateProvider());
        }

        /// <summary>
        /// Starts each request like the real coordinator: a request is deduplicated against the one in
        /// flight unless it supersedes it.
        /// </summary>
        private sealed class ScriptedCoordinator : IRuntimeRestartCoordinator
        {
            private readonly List<(RuntimeRestartCoordinatorRequest Request, TaskCompletionSource<RuntimeRestartExecutionResult> Completion)> _inFlight =
                new List<(RuntimeRestartCoordinatorRequest, TaskCompletionSource<RuntimeRestartExecutionResult>)>();

            public List<RuntimeRestartCoordinatorRequest> Started { get; } = new List<RuntimeRestartCoordinatorRequest>();

            public RuntimeRestartDispatch Dispatch(RuntimeRestartCoordinatorRequest request)
            {
                if (_inFlight.Count > 0 && !request.RestartRequest.SupersedesInFlight)
                    return RuntimeRestartDispatch.Deduplicated(RuntimeRestartDuplicateReason.InFlight, _inFlight[0].Completion.Task);

                var completion = new TaskCompletionSource<RuntimeRestartExecutionResult>();
                _inFlight.Add((request, completion));
                Started.Add(request);
                request.OnBeforeRestartAsync?.Invoke(request.CancellationToken);
                return RuntimeRestartDispatch.Started(completion.Task);
            }

            public void CompleteAll()
            {
                foreach (var (request, completion) in _inFlight)
                    completion.TrySetResult(RuntimeRestartExecutionResult.Completed(request.RestartRequest));
                _inFlight.Clear();
            }
        }

        private sealed class FakePipelineProvider : IRuntimeFlowPipelineProvider
        {
            private readonly IRuntimeRestartCoordinator _coordinator;

            public FakePipelineProvider(IRuntimeRestartCoordinator coordinator) => _coordinator = coordinator;

            public bool HasCurrent => false;
            public RuntimePipeline Current => throw new InvalidOperationException("No runtime pipeline.");

            public bool TryGetCurrent(out RuntimePipeline? pipeline)
            {
                pipeline = null;
                return false;
            }

            public void SetCurrent(RuntimePipeline pipeline, IGameSceneLoader sceneLoader) { }
            public void ClearCurrent(RuntimePipeline pipeline) { }
            public bool IsReplayInProgress() => false;
            public bool IsReplayReady() => false;
            public string DescribeCurrentStatus() => "pipeline-missing";

            public RuntimeReadinessStatus GetRuntimeReadinessStatus() =>
                new RuntimeReadinessStatus(isReady: true, updatedAtUtc: DateTimeOffset.UtcNow);

            public IRuntimeRestartCoordinator CreateRestartCoordinator(
                Func<RuntimeReadinessStatus>? runtimeReadinessProvider = null,
                Func<DateTimeOffset>? timestampProvider = null) => _coordinator;

            public Task ReplayCurrentFlowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        private sealed class NoopStateSaver : IGameRestartStateSaver
        {
            public void SaveAppState() { }
        }

        private sealed class NoopGameDataCleaner : IGameDataCleaner
        {
            public void ClearSecondaryUserData() { }
            public void ClearAllUserData() { }
        }

        private sealed class NoopStageStateProvider : IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>
        {
            public bool IsStopped => false;

            public RuntimePipelineStageSnapshot<string> Snapshot { get; private set; } =
                new RuntimePipelineStageSnapshot<string>("init", RuntimePipelineStageState.NotStarted, DateTimeOffset.UtcNow);

            public bool IsStageActive(string stage) => false;
            public void StartStage(string stage, string? reasonCode = null, string? diagnostic = null) { }
            public void CompleteStage(string stage, string? reasonCode = null, string? diagnostic = null) { }
            public void FailStage(string stage, string? reasonCode, Exception? exception = null, string? diagnostic = null) { }
            public void Report(string diagnostic) { }
            public void Stop(string? reasonCode, string? diagnostic = null, Exception? exception = null) { }
        }
    }
}
