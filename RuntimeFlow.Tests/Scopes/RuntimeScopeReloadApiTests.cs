using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

public sealed partial class RuntimeScopeReloadApiTests
{
    private static async Task AssertCanceledProgressForSessionReloadAsync()
    {
        var observer = new CollectingRuntimeLoadingProgressObserver();
        var reloadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionService = new AttemptControlledSessionService(async (attempt, cancellationToken) =>
        {
            if (attempt == 1)
                return;

            reloadStarted.TrySetResult(true);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        });

        var pipeline = RuntimePipeline.Create(
            builder =>
            {
                builder.DefineSessionScope();
                builder.Session().RegisterInstance<ITestSessionService>(sessionService);
            },
            options => options.LoadingProgressObserver = observer);

        await pipeline.InitializeAsync();

        using var cancellationSource = new CancellationTokenSource();
        var reload = pipeline.ReloadScopeAsync<SessionScope>(cancellationToken: cancellationSource.Token);
        await reloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reload);

        AssertCanceledOperationProgress(
            observer.Snapshots,
            RuntimeLoadingOperationKind.RestartSession,
            expectedOperationIdPrefix: "restart_session-");
    }

    private static async Task AssertCanceledProgressForSceneReloadAsync()
    {
        var observer = new CollectingRuntimeLoadingProgressObserver();
        var reloadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sceneService = new AttemptControlledSceneService(async (_, cancellationToken) =>
        {
            reloadStarted.TrySetResult(true);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        });

        var pipeline = RuntimePipeline.Create(
            builder =>
            {
                builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(sceneService)));
            },
            options => options.LoadingProgressObserver = observer);

        await pipeline.InitializeAsync();

        using var cancellationSource = new CancellationTokenSource();
        var reload = pipeline.ReloadScopeAsync<TestSceneScope>(cancellationToken: cancellationSource.Token);
        await reloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reload);

        AssertCanceledOperationProgress(
            observer.Snapshots,
            RuntimeLoadingOperationKind.ReloadScene,
            expectedOperationIdPrefix: "reload_scene-");
    }

    private static async Task AssertCanceledProgressForModuleReloadAsync()
    {
        var observer = new CollectingRuntimeLoadingProgressObserver();
        var reloadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sceneService = new AttemptControlledSceneService((_, _) => Task.CompletedTask);
        var moduleService = new AttemptControlledModuleService(async (_, cancellationToken) =>
        {
            reloadStarted.TrySetResult(true);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        });

        var pipeline = RuntimePipeline.Create(
            builder =>
            {
                builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(sceneService)));
                builder.Module(new TestModuleScope(m => m.RegisterInstance<ITestModuleService>(moduleService)));
            },
            options => options.LoadingProgressObserver = observer);

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();

        using var cancellationSource = new CancellationTokenSource();
        var reload = pipeline.ReloadScopeAsync<TestModuleScope>(cancellationToken: cancellationSource.Token);
        await reloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reload);

        AssertCanceledOperationProgress(
            observer.Snapshots,
            RuntimeLoadingOperationKind.ReloadModule,
            expectedOperationIdPrefix: "reload_module-");
    }

    private static void AssertCanceledOperationProgress(
        IReadOnlyList<RuntimeLoadingOperationSnapshot> snapshots,
        RuntimeLoadingOperationKind operationKind,
        string expectedOperationIdPrefix)
    {
        var canceledSnapshot = snapshots.LastOrDefault(snapshot =>
            snapshot.OperationKind == operationKind
            && snapshot.Stage == RuntimeLoadingOperationStage.Canceled
            && snapshot.State == RuntimeLoadingOperationState.Canceled);

        Assert.NotNull(canceledSnapshot);
        Assert.StartsWith(expectedOperationIdPrefix, canceledSnapshot!.OperationId, StringComparison.Ordinal);

        var operationSnapshots = snapshots
            .Where(snapshot => snapshot.OperationId == canceledSnapshot.OperationId)
            .ToArray();

        Assert.NotEmpty(operationSnapshots);
        Assert.Equal(RuntimeLoadingOperationStage.Preparing, operationSnapshots[0].Stage);
        Assert.Equal(RuntimeLoadingOperationStage.Canceled, operationSnapshots[operationSnapshots.Length - 1].Stage);
        Assert.Equal(RuntimeLoadingOperationState.Canceled, operationSnapshots[operationSnapshots.Length - 1].State);
    }

    private static void AssertScopeReloadProgressContract(IReadOnlyList<RuntimeLoadingOperationSnapshot> snapshots)
    {
        Assert.NotEmpty(snapshots);
        Assert.Contains(snapshots, snapshot => snapshot.OperationKind == RuntimeLoadingOperationKind.RestartSession);
        Assert.Contains(snapshots, snapshot => snapshot.OperationKind == RuntimeLoadingOperationKind.ReloadScene);
        Assert.Contains(snapshots, snapshot => snapshot.OperationKind == RuntimeLoadingOperationKind.ReloadModule);

        foreach (var operationSnapshots in snapshots.GroupBy(snapshot => snapshot.OperationId).Select(group => group.ToArray()))
        {
            var operationKind = operationSnapshots[0].OperationKind;
            Assert.All(operationSnapshots, snapshot => Assert.Equal(operationKind, snapshot.OperationKind));
            RuntimeLoadingProgressAssertions.AssertOperationIdPrefix(operationKind, operationSnapshots[0].OperationId);

            RuntimeLoadingProgressAssertions.AssertProgression(operationSnapshots);
        }
    }

    private sealed class ConcurrentSessionActivationService : ITestSessionService, ISessionScopeActivationService
    {
        private int _attempts;
        private int _enterCalls;
        private int _exitCalls;
        private int _canceledExitCalls;

        public int Attempts => Volatile.Read(ref _attempts);
        public int EnterCalls => Volatile.Read(ref _enterCalls);
        public int ExitCalls => Volatile.Read(ref _exitCalls);
        public int CanceledExitCalls => Volatile.Read(ref _canceledExitCalls);
        public TaskCompletionSource<bool> FirstExitStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ResetForConcurrentOperation()
        {
            Interlocked.Exchange(ref _enterCalls, 0);
            Interlocked.Exchange(ref _exitCalls, 0);
            Interlocked.Exchange(ref _canceledExitCalls, 0);
            FirstExitStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            return Task.CompletedTask;
        }

        public Task OnScopeActivatedAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _enterCalls);
            return Task.CompletedTask;
        }

        public async Task OnScopeDeactivatingAsync(CancellationToken cancellationToken)
        {
            var exitCall = Interlocked.Increment(ref _exitCalls);
            if (exitCall != 1)
                return;

            FirstExitStarted.TrySetResult(true);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _canceledExitCalls);
                throw;
            }
        }
    }

    private sealed class ConcurrentModuleActivationService : ITestModuleService, IModuleScopeActivationService
    {
        private int _attempts;
        private int _enterCalls;
        private int _exitCalls;
        private int _canceledExitCalls;

        public int Attempts => Volatile.Read(ref _attempts);
        public int EnterCalls => Volatile.Read(ref _enterCalls);
        public int ExitCalls => Volatile.Read(ref _exitCalls);
        public int CanceledExitCalls => Volatile.Read(ref _canceledExitCalls);
        public TaskCompletionSource<bool> FirstExitStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ResetForConcurrentOperation()
        {
            Interlocked.Exchange(ref _enterCalls, 0);
            Interlocked.Exchange(ref _exitCalls, 0);
            Interlocked.Exchange(ref _canceledExitCalls, 0);
            FirstExitStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            return Task.CompletedTask;
        }

        public Task OnScopeActivatedAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _enterCalls);
            return Task.CompletedTask;
        }

        public async Task OnScopeDeactivatingAsync(CancellationToken cancellationToken)
        {
            var exitCall = Interlocked.Increment(ref _exitCalls);
            if (exitCall != 1)
                return;

            FirstExitStarted.TrySetResult(true);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _canceledExitCalls);
                throw;
            }
        }
    }

    private sealed class UndeclaredScope { }
}
