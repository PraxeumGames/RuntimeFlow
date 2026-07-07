using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{

public sealed class RestartCancellationPropagationTests
{
    [Test]
    public async Task RestartCoordinator_Dispatch_ForwardsCallerCancellationTokenToReadinessBeforeRestartAndLifecycle()
    {
        var readinessGate = new ProbeReadinessGate();
        var lifecycleManager = new ProbeRestartLifecycleManager();
        CancellationToken? onBeforeRestartToken = null;

        var coordinator = new RuntimeRestartCoordinator(
            readinessGateFactory: () => readinessGate,
            restartLifecycleManagerProvider: () => lifecycleManager);

        using var cancellationSource = new CancellationTokenSource();
        var dispatch = coordinator.Dispatch(
            new RuntimeRestartCoordinatorRequest(
                restartRequest: new RuntimeRestartRequest(reasonCode: "restart.test"),
                readinessTimeout: TimeSpan.FromSeconds(1),
                readinessPollInterval: TimeSpan.FromMilliseconds(5),
                onBeforeRestartAsync: token =>
                {
                    onBeforeRestartToken = token;
                    return Task.CompletedTask;
                },
                cancellationToken: cancellationSource.Token));

        var result = await dispatch.ExecutionTask;

        Assert.That(result.Outcome, Is.EqualTo(RuntimeRestartExecutionOutcome.Completed));
        Assert.That(readinessGate.CapturedCancellationToken, Is.EqualTo(cancellationSource.Token));
        Assert.That(onBeforeRestartToken.HasValue, Is.True);
        Assert.That(onBeforeRestartToken.Value, Is.EqualTo(cancellationSource.Token));
        Assert.That(lifecycleManager.CapturedCancellationToken, Is.EqualTo(cancellationSource.Token));
    }

    [Test]
    public async Task RestartCoordinator_Dispatch_WhenCallerCancellationHappensDuringReadiness_ReturnsFailed()
    {
        var readinessGate = new ProbeReadinessGate(
            waitUntilReadyAsync: static async (_, _, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new RuntimeRestartReadiness(
                    isReady: true,
                    updatedAtUtc: DateTimeOffset.UtcNow);
            });
        var lifecycleManager = new ProbeRestartLifecycleManager();

        var coordinator = new RuntimeRestartCoordinator(
            readinessGateFactory: () => readinessGate,
            restartLifecycleManagerProvider: () => lifecycleManager);

        using var cancellationSource = new CancellationTokenSource();
        var dispatch = coordinator.Dispatch(
            new RuntimeRestartCoordinatorRequest(
                restartRequest: new RuntimeRestartRequest(reasonCode: "restart.test"),
                readinessTimeout: TimeSpan.FromSeconds(3),
                cancellationToken: cancellationSource.Token));

        cancellationSource.Cancel();
        var result = await dispatch.ExecutionTask;

        Assert.That(result.Outcome, Is.EqualTo(RuntimeRestartExecutionOutcome.Failed));
        Assert.That(result.Exception, Is.InstanceOf<OperationCanceledException>());
        Assert.That(lifecycleManager.RestartCallCount, Is.EqualTo(0));
    }

    [Test]
    public async Task RestartLifecycleManager_RestartAsync_ForwardsCallerTokenToGuardRestartAndReplay()
    {
        var guard = new ProbeRestartGuard();
        CancellationToken restartOperationToken = default;
        CancellationToken replayOperationToken = default;
        var lifecycleManager = new RuntimeRestartLifecycleManager(
            restartOperation: (_, token) =>
            {
                restartOperationToken = token;
                return Task.CompletedTask;
            },
            replayOperation: (_, token) =>
            {
                replayOperationToken = token;
                return Task.CompletedTask;
            },
            readinessGate: new ProbeReadinessGate(),
            guard: guard);

        using var cancellationSource = new CancellationTokenSource();
        await lifecycleManager.RestartAsync(
            new RuntimeRestartRequest(reasonCode: "restart.test"),
            cancellationSource.Token);

        Assert.That(guard.CapturedCancellationToken, Is.EqualTo(cancellationSource.Token));
        Assert.That(restartOperationToken, Is.EqualTo(cancellationSource.Token));
        Assert.That(replayOperationToken, Is.EqualTo(cancellationSource.Token));
    }

    private sealed class ProbeRestartLifecycleManager : IRuntimeRestartLifecycleManager
    {
        public RuntimeRestartLifecycleSnapshot Snapshot { get; private set; } =
            new(RuntimeRestartLifecycleStage.Idle, DateTimeOffset.UtcNow);

        public CancellationToken CapturedCancellationToken { get; private set; }
        public int RestartCallCount { get; private set; }

        public RuntimeRestartReadiness GetRestartReadiness()
        {
            return new RuntimeRestartReadiness(
                isReady: true,
                updatedAtUtc: DateTimeOffset.UtcNow);
        }

        public Task RestartAsync(RuntimeRestartRequest request, CancellationToken cancellationToken = default)
        {
            RestartCallCount++;
            CapturedCancellationToken = cancellationToken;
            Snapshot = new RuntimeRestartLifecycleSnapshot(
                RuntimeRestartLifecycleStage.Completed,
                DateTimeOffset.UtcNow,
                reasonCode: request.ReasonCode);
            return Task.CompletedTask;
        }
    }

    private sealed class ProbeRestartGuard : IRuntimeRestartGuard
    {
        public CancellationToken CapturedCancellationToken { get; private set; }

        public Task<RuntimeFlowGuardResult> EvaluateAsync(
            RuntimeRestartGuardContext context,
            CancellationToken cancellationToken = default)
        {
            CapturedCancellationToken = cancellationToken;
            return Task.FromResult(RuntimeFlowGuardResult.Allow());
        }
    }

    private sealed class ProbeReadinessGate : IRuntimeReadinessGate
    {
        private readonly Func<TimeSpan, TimeSpan?, CancellationToken, Task<RuntimeRestartReadiness>> _waitUntilReadyAsync;

        public ProbeReadinessGate(
            Func<TimeSpan, TimeSpan?, CancellationToken, Task<RuntimeRestartReadiness>>? waitUntilReadyAsync = null)
        {
            _waitUntilReadyAsync = waitUntilReadyAsync
                                   ?? ((_, _, _) => Task.FromResult(
                                       new RuntimeRestartReadiness(
                                           isReady: true,
                                           updatedAtUtc: DateTimeOffset.UtcNow)));
        }

        public CancellationToken CapturedCancellationToken { get; private set; }

        public RuntimeRestartReadiness GetRestartReadiness()
        {
            return new RuntimeRestartReadiness(
                isReady: true,
                updatedAtUtc: DateTimeOffset.UtcNow);
        }

        public IDisposable Block(string reasonCode, string? reason = null)
        {
            return NullDisposable.Instance;
        }

        public Task<RuntimeRestartReadiness> WaitUntilReadyAsync(
            TimeSpan timeout,
            TimeSpan? pollInterval = null,
            CancellationToken cancellationToken = default)
        {
            CapturedCancellationToken = cancellationToken;
            return _waitUntilReadyAsync(timeout, pollInterval, cancellationToken);
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
}
