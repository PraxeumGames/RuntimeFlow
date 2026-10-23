using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{
    /// <summary>
    /// A restart requested while an earlier one is in progress. When the earlier one already rebuilds the
    /// session, the rebuild can be waiting on the very initializer that asks for the new restart (it fetched
    /// fresh data and needs the session rebuilt on it): the new request replaces the rebuild, and the earlier
    /// request ends with it. When the earlier one still waits for readiness or its guard, the request is
    /// absorbed by it, so the readiness gate keeps holding (no rebuild in the middle of a fight).
    /// </summary>
    public sealed class RuntimeRestartLifecycleSupersedeTests
    {
        [Test]
        public async Task SupersedingRestart_WhileTheFirstRebuildsTheSession_RebuildsAgain_AndBothCompleteWithIt()
        {
            var rebuilds = new List<TaskCompletionSource<bool>>();
            var manager = new RuntimeRestartLifecycleManager(restartOperation: (_, _) =>
            {
                // The builder abandons the active rebuild when a new one starts
                foreach (var previous in rebuilds)
                    previous.TrySetCanceled();
                var rebuild = new TaskCompletionSource<bool>();
                rebuilds.Add(rebuild);
                return rebuild.Task;
            });

            var first = manager.RestartAsync(new RuntimeRestartRequest("restart.first", allowBeforeReady: true));
            var second = manager.RestartAsync(new RuntimeRestartRequest("restart.second", supersedesInFlight: true));

            Assert.That(rebuilds, Has.Count.EqualTo(2), "The superseding restart rebuilds the session again");
            Assert.That(first.IsCompleted, Is.False, "The first request ends with the rebuild that replaced its own");

            rebuilds[1].SetResult(true);
            await second;
            await first;

            Assert.That(manager.Snapshot.Stage, Is.EqualTo(RuntimeRestartLifecycleStage.Completed));
        }

        [Test]
        public void SupersedingRequest_WhileTheFirstWaitsForItsGuard_IsAbsorbedByIt()
        {
            var guard = new BlockingGuard();
            var rebuilds = 0;
            var manager = new RuntimeRestartLifecycleManager(
                restartOperation: (_, _) =>
                {
                    rebuilds++;
                    return Task.CompletedTask;
                },
                guard: guard);

            var first = manager.RestartAsync(new RuntimeRestartRequest("restart.first", allowBeforeReady: true));
            var second = manager.RestartAsync(new RuntimeRestartRequest("restart.second", supersedesInFlight: true));

            Assert.That(rebuilds, Is.Zero, "Nothing rebuilds the session before the guard of the first restart lets it");
            guard.Allow();

            Assert.That(rebuilds, Is.EqualTo(1), "The two requests are one restart");
            Assert.That(first.IsCompleted && second.IsCompleted, Is.True);
        }

        [Test]
        public void SupersededRestart_WhoseRebuildEndsAnyway_StopsThere()
        {
            var rebuilds = new List<TaskCompletionSource<bool>>();
            var replays = new List<string?>();
            var manager = new RuntimeRestartLifecycleManager(
                restartOperation: (_, _) =>
                {
                    var rebuild = new TaskCompletionSource<bool>();
                    rebuilds.Add(rebuild);
                    return rebuild.Task;
                },
                replayOperation: (request, _) =>
                {
                    replays.Add(request.ReasonCode);
                    return Task.CompletedTask;
                });

            var first = manager.RestartAsync(new RuntimeRestartRequest("restart.first", allowBeforeReady: true));
            var second = manager.RestartAsync(new RuntimeRestartRequest("restart.second", supersedesInFlight: true));
            rebuilds[0].SetResult(true);

            Assert.That(replays, Is.Empty, "The superseded restart must not replay over the rebuild that replaced it");
            Assert.That(manager.Snapshot.Stage, Is.EqualTo(RuntimeRestartLifecycleStage.Restarting),
                "The stage belongs to the restart that replaced it");
            Assert.That(first.IsCompleted, Is.False);

            rebuilds[1].SetResult(true);

            Assert.That(replays, Is.EqualTo(new[] { "restart.second" }));
            Assert.That(first.IsCompleted && second.IsCompleted, Is.True);
        }

        [Test]
        public void RestartWithoutSupersede_IsStillDeduplicatedAgainstTheOneInFlight()
        {
            var rebuilds = 0;
            var rebuild = new TaskCompletionSource<bool>();
            var manager = new RuntimeRestartLifecycleManager(restartOperation: (_, _) =>
            {
                rebuilds++;
                return rebuild.Task;
            });

            manager.RestartAsync(new RuntimeRestartRequest("restart.first", allowBeforeReady: true));
            manager.RestartAsync(new RuntimeRestartRequest("restart.second", allowBeforeReady: true));

            Assert.That(rebuilds, Is.EqualTo(1));
        }

        [Test]
        public void Coordinator_StartsASupersedingRequest_WhileTheFirstRebuildsTheSession()
        {
            var lifecycle = new RuntimeRestartLifecycleManager(restartOperation: (_, _) => new TaskCompletionSource<bool>().Task);
            var coordinator = new RuntimeRestartCoordinator(
                readinessGateFactory: () => new RuntimeReadinessGate(),
                restartLifecycleManagerProvider: () => lifecycle);

            var first = coordinator.Dispatch(Request(supersedes: true));
            var second = coordinator.Dispatch(Request(supersedes: true));

            Assert.That(first.IsAccepted, Is.True);
            Assert.That(second.IsAccepted, Is.True, "A request during the rebuild is not deduplicated");
        }

        [Test]
        public void Coordinator_AbsorbsASupersedingRequest_WhileTheFirstWaitsForReadiness()
        {
            var rebuilds = 0;
            var lifecycle = new RuntimeRestartLifecycleManager(restartOperation: (_, _) =>
            {
                rebuilds++;
                return Task.CompletedTask;
            });
            var readiness = new RuntimeReadinessGate();
            var fight = readiness.Block("fight", "A fight is in progress");
            var coordinator = new RuntimeRestartCoordinator(
                readinessGateFactory: () => readiness,
                restartLifecycleManagerProvider: () => lifecycle);

            var first = coordinator.Dispatch(Request(supersedes: true, allowBeforeReady: false));
            var second = coordinator.Dispatch(Request(supersedes: true, allowBeforeReady: false));

            Assert.That(first.IsAccepted, Is.True);
            Assert.That(second.IsAccepted, Is.False, "A request before the rebuild joins the restart waiting for readiness");
            Assert.That(rebuilds, Is.Zero, "The readiness gate holds for both requests");

            fight.Dispose();
            Assert.That(SpinWait.SpinUntil(() => first.ExecutionTask.IsCompleted, TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(rebuilds, Is.EqualTo(1));
        }

        private static RuntimeRestartCoordinatorRequest Request(bool supersedes, bool allowBeforeReady = true) =>
            new RuntimeRestartCoordinatorRequest(
                restartRequest: new RuntimeRestartRequest(
                    "restart.test",
                    allowBeforeReady: allowBeforeReady,
                    supersedesInFlight: supersedes),
                readinessTimeout: TimeSpan.FromSeconds(5),
                readinessPollInterval: TimeSpan.FromMilliseconds(5));

        private sealed class BlockingGuard : IRuntimeRestartGuard
        {
            private readonly TaskCompletionSource<RuntimeFlowGuardResult> _result =
                new TaskCompletionSource<RuntimeFlowGuardResult>();

            public Task<RuntimeFlowGuardResult> EvaluateAsync(
                RuntimeRestartGuardContext context,
                CancellationToken cancellationToken = default) => _result.Task;

            public void Allow() => _result.TrySetResult(RuntimeFlowGuardResult.Allow());
        }
    }
}
