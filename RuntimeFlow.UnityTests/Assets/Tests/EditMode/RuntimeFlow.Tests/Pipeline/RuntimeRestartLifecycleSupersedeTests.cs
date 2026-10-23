using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{
    /// <summary>
    /// A superseding restart is run while the previous one is still rebuilding the session. Rebuilding the
    /// session again abandons the previous rebuild (the builder cancels the active load), so the previous
    /// request ends with the newer one instead of failing.
    /// </summary>
    public sealed class RuntimeRestartLifecycleSupersedeTests
    {
        [Test]
        public async Task SupersedingRestart_RunsWhileTheFirstIsInFlight_AndBothCompleteWithIt()
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
            Assert.That(manager.Snapshot.IsInProgress, Is.False);
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
        public void Coordinator_DispatchesASupersedingRequestDuringAnInFlightRestart()
        {
            var lifecycle = new RuntimeRestartLifecycleManager(restartOperation: (_, _) => new TaskCompletionSource<bool>().Task);
            var coordinator = new RuntimeRestartCoordinator(
                readinessGateFactory: () => new RuntimeReadinessGate(),
                restartLifecycleManagerProvider: () => lifecycle);

            var first = coordinator.Dispatch(Request(supersedes: false));
            var second = coordinator.Dispatch(Request(supersedes: true));

            Assert.That(first.IsStarted, Is.True);
            Assert.That(second.IsStarted, Is.True, "A superseding request is not deduplicated");
        }

        private static RuntimeRestartCoordinatorRequest Request(bool supersedes) =>
            new RuntimeRestartCoordinatorRequest(
                restartRequest: new RuntimeRestartRequest("restart.test", allowBeforeReady: true, supersedesInFlight: supersedes),
                readinessTimeout: TimeSpan.FromSeconds(1),
                readinessPollInterval: TimeSpan.FromMilliseconds(5));
    }
}
