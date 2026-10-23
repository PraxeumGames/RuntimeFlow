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
    /// A restart requested while an earlier restart is still replaying the session. The replay can be
    /// waiting on the very initializer that asks for the new restart (it downloaded fresh data and
    /// needs the session rebuilt on it), so dropping the request as a duplicate leaves the replay
    /// hanging forever. The new request supersedes: the replay is cancelled and the new restart runs.
    /// </summary>
    public sealed class RuntimeFlowGameRestartHandlerSupersedeTests
    {
        [Test]
        public void RestartDuringRestart_CancelsTheReplay_AndRunsTheNewRestart()
        {
            var coordinator = new ScriptedCoordinator();
            var handler = CreateHandler(coordinator);

            handler.Restart("first", forceSave: false);
            handler.Restart("second", forceSave: false);

            Assert.That(coordinator.Started, Has.Count.EqualTo(2), "The second restart must run, not be skipped");
            Assert.That(coordinator.Started[0].CancellationToken.IsCancellationRequested, Is.True,
                "The superseded replay must be cancelled: it may be waiting on the requester forever");
            Assert.That(coordinator.Started[1].RestartRequest.Diagnostic, Is.EqualTo("second"));
        }

        [Test]
        public void SeveralRestartsDuringRestart_RunOnlyTheLatest()
        {
            var coordinator = new ScriptedCoordinator();
            var handler = CreateHandler(coordinator);

            handler.Restart("first", forceSave: false);
            handler.Restart("second", forceSave: false);
            handler.Restart("third", forceSave: false);

            Assert.That(coordinator.Started, Has.Count.EqualTo(3));
            Assert.That(coordinator.Started[1].CancellationToken.IsCancellationRequested, Is.True);
            Assert.That(coordinator.Started[2].RestartRequest.Diagnostic, Is.EqualTo("third"));
            Assert.That(handler.IsApplicationRestarting, Is.True);
        }

        [Test]
        public void CompletedRestart_LeavesNothingToSupersede()
        {
            var coordinator = new ScriptedCoordinator();
            var handler = CreateHandler(coordinator);

            handler.Restart("first", forceSave: false);
            coordinator.CompleteLast();
            handler.Restart("second", forceSave: false);

            Assert.That(coordinator.Started, Has.Count.EqualTo(2));
            Assert.That(coordinator.Started[0].CancellationToken.IsCancellationRequested, Is.False,
                "A finished restart is not cancelled by the next one");
        }

        private static RuntimeFlowGameRestartHandler CreateHandler(ScriptedCoordinator coordinator)
        {
            return new RuntimeFlowGameRestartHandler(
                new NoopStateSaver(),
                new NoopGameDataCleaner(),
                new FakePipelineProvider(coordinator),
                new NoopStageStateProvider());
        }

        /// <summary>
        /// Starts each request like the real coordinator, when nothing is in flight, and finishes it as
        /// cancelled when its token is cancelled, the way a replay ends when its token fires.
        /// </summary>
        private sealed class ScriptedCoordinator : IRuntimeRestartCoordinator
        {
            private TaskCompletionSource<RuntimeRestartExecutionResult>? _inFlight;
            private RuntimeRestartCoordinatorRequest? _inFlightRequest;

            public List<RuntimeRestartCoordinatorRequest> Started { get; } = new List<RuntimeRestartCoordinatorRequest>();

            public RuntimeRestartDispatch Dispatch(RuntimeRestartCoordinatorRequest request)
            {
                if (_inFlight != null && !_inFlight.Task.IsCompleted)
                    return RuntimeRestartDispatch.Deduplicated(RuntimeRestartDuplicateReason.InFlight, _inFlight.Task);

                var completion = new TaskCompletionSource<RuntimeRestartExecutionResult>();
                _inFlight = completion;
                _inFlightRequest = request;
                Started.Add(request);
                request.CancellationToken.Register(() => completion.TrySetResult(
                    RuntimeRestartExecutionResult.Failed(request.RestartRequest, new OperationCanceledException())));
                request.OnBeforeRestartAsync?.Invoke(request.CancellationToken);
                return RuntimeRestartDispatch.Started(completion.Task);
            }

            public void CompleteLast()
            {
                _inFlight!.TrySetResult(RuntimeRestartExecutionResult.Completed(_inFlightRequest!.RestartRequest));
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
