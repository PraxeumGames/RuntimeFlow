using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

public sealed class RuntimeScopeReloadApiTests
{
    [Fact]
    public async Task ReloadScopeAsync_GenericDispatch_ReloadsSessionSceneAndModule()
    {
        var sessionService = new AttemptControlledSessionService((_, _) => Task.CompletedTask);
        var sceneService = new AttemptControlledSceneService((_, _) => Task.CompletedTask);
        var moduleService = new AttemptControlledModuleService((_, _) => Task.CompletedTask);

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<ITestSessionService>(sessionService);
            builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(sceneService)));
            builder.Module(new TestModuleScope(m => m.RegisterInstance<ITestModuleService>(moduleService)));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadModuleAsync<TestModuleScope>();
        await pipeline.ReloadScopeAsync<SessionScope>();
        await pipeline.ReloadScopeAsync<TestSceneScope>();
        await pipeline.ReloadScopeAsync<TestModuleScope>();

        Assert.Equal(2, sessionService.Attempts);
        Assert.Equal(3, sceneService.Attempts);
        Assert.Equal(3, moduleService.Attempts);
    }

    [Fact]
    public async Task ReloadScopeAsync_GlobalScope_ThrowsDeterministicException()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineGlobalScope();
        });

        await pipeline.InitializeAsync();

        var exception = await Assert.ThrowsAsync<ScopeNotRestartableException>(() =>
            pipeline.ReloadScopeAsync<GlobalScope>());

        Assert.Equal(typeof(GlobalScope), exception.ScopeType);
    }

    [Fact]
    public async Task ReloadScopeAsync_UndeclaredScope_ThrowsDeterministicDiagnostic()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
        });

        await pipeline.InitializeAsync();

        var exception = await Assert.ThrowsAsync<ScopeNotDeclaredException>(() =>
            pipeline.ReloadScopeAsync<UndeclaredScope>());

        Assert.Equal(typeof(UndeclaredScope), exception.ScopeType);
    }

    [Fact]
    public async Task ReloadScopeAsync_ModuleScopeWithoutSceneContext_ThrowsDeterministicPrecondition()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.Module(new TestModuleScope(m => m.RegisterInstance<ITestModuleService>(
                new AttemptControlledModuleService((_, _) => Task.CompletedTask))));
        });

        await pipeline.InitializeAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.ReloadScopeAsync<TestModuleScope>());

        Assert.Equal("Scene context is not initialized. Call LoadSceneAsync first.", exception.Message);
        Assert.Equal("reload_module", pipeline.GetRuntimeStatus().CurrentOperationCode);
    }

    [Fact]
    public async Task ReloadScopeAsync_DispatchPublishesRestartAndReloadOperationKinds()
    {
        var observer = new CollectingRuntimeLoadingProgressObserver();
        var sessionService = new AttemptControlledSessionService((_, _) => Task.CompletedTask);
        var sceneService = new AttemptControlledSceneService((_, _) => Task.CompletedTask);
        var moduleService = new AttemptControlledModuleService((_, _) => Task.CompletedTask);

        var pipeline = RuntimePipeline.Create(
            builder =>
            {
                builder.DefineSessionScope();
                builder.Session().RegisterInstance<ITestSessionService>(sessionService);
                builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(sceneService)));
                builder.Module(new TestModuleScope(m => m.RegisterInstance<ITestModuleService>(moduleService)));
            },
            options => options.LoadingProgressObserver = observer);

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadModuleAsync<TestModuleScope>();
        await pipeline.ReloadScopeAsync<SessionScope>();
        await pipeline.ReloadScopeAsync<TestSceneScope>();
        await pipeline.ReloadScopeAsync<TestModuleScope>();

        var scopeReloadSnapshots = observer.Snapshots
            .Where(snapshot => snapshot.OperationKind is RuntimeLoadingOperationKind.RestartSession
                or RuntimeLoadingOperationKind.ReloadScene
                or RuntimeLoadingOperationKind.ReloadModule)
            .ToArray();

        AssertScopeReloadProgressContract(scopeReloadSnapshots);
    }

    [Fact]
    public async Task FlowContextReloadScopeAsync_GenericDispatch_ReloadsSessionSceneAndModule()
    {
        var observer = new CollectingRuntimeLoadingProgressObserver();
        var sessionService = new AttemptControlledSessionService((_, _) => Task.CompletedTask);
        var sceneService = new AttemptControlledSceneService((_, _) => Task.CompletedTask);
        var moduleService = new AttemptControlledModuleService((_, _) => Task.CompletedTask);

        var pipeline = RuntimePipeline.Create(
                builder =>
                {
                    builder.DefineSessionScope();
                    builder.Session().RegisterInstance<ITestSessionService>(sessionService);
                    builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(sceneService)));
                    builder.Module(new TestModuleScope(m => m.RegisterInstance<ITestModuleService>(moduleService)));
                },
                options => options.LoadingProgressObserver = observer)
            .ConfigureFlow(new DelegateRuntimeFlowScenario(async (context, cancellationToken) =>
            {
                await context.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await context.LoadScopeSceneAsync<TestSceneScope>(cancellationToken).ConfigureAwait(false);
                await context.LoadScopeModuleAsync<TestModuleScope>(cancellationToken).ConfigureAwait(false);
                await context.ReloadScopeAsync<SessionScope>(cancellationToken).ConfigureAwait(false);
                await context.ReloadScopeAsync<TestSceneScope>(cancellationToken).ConfigureAwait(false);
                await context.ReloadScopeAsync<TestModuleScope>(cancellationToken).ConfigureAwait(false);
            }));

        await pipeline.RunAsync(NoopSceneLoader.Instance);

        Assert.Equal(2, sessionService.Attempts);
        Assert.Equal(3, sceneService.Attempts);
        Assert.Equal(3, moduleService.Attempts);

        var scopeReloadSnapshots = observer.Snapshots
            .Where(snapshot => snapshot.OperationKind is RuntimeLoadingOperationKind.RestartSession
                or RuntimeLoadingOperationKind.ReloadScene
                or RuntimeLoadingOperationKind.ReloadModule)
            .ToArray();

        AssertScopeReloadProgressContract(scopeReloadSnapshots);
    }

    [Fact]
    public async Task FlowContextReloadScopeAsync_GlobalScope_ThrowsDeterministicException()
    {
        var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineGlobalScope();
            })
            .ConfigureFlow(new DelegateRuntimeFlowScenario(async (context, cancellationToken) =>
            {
                await context.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await context.ReloadScopeAsync<GlobalScope>(cancellationToken).ConfigureAwait(false);
            }));

        var exception = await Assert.ThrowsAsync<ScopeNotRestartableException>(() =>
            pipeline.RunAsync(NoopSceneLoader.Instance));

        Assert.Equal(typeof(GlobalScope), exception.ScopeType);
    }

    [Fact]
    public async Task FlowContextReloadScopeAsync_UndeclaredScope_ThrowsDeterministicDiagnostic()
    {
        var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
            })
            .ConfigureFlow(new DelegateRuntimeFlowScenario(async (context, cancellationToken) =>
            {
                await context.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await context.ReloadScopeAsync<UndeclaredScope>(cancellationToken).ConfigureAwait(false);
            }));

        var exception = await Assert.ThrowsAsync<ScopeNotDeclaredException>(() =>
            pipeline.RunAsync(NoopSceneLoader.Instance));

        Assert.Equal(typeof(UndeclaredScope), exception.ScopeType);
    }

    [Fact]
    public async Task ReloadScopeAsync_CallerCancellation_PublishesCanceledProgressPerOperationKind()
    {
        await AssertCanceledProgressForSessionReloadAsync();
        await AssertCanceledProgressForSceneReloadAsync();
        await AssertCanceledProgressForModuleReloadAsync();
    }

    [Fact]
    public async Task ReloadScopeAsync_SessionConcurrentRequests_CancelStaleOperation()
    {
        var firstReloadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstReloadCanceled = 0;
        var sessionService = new AttemptControlledSessionService(async (attempt, cancellationToken) =>
        {
            if (attempt != 2)
                return;

            firstReloadStarted.TrySetResult(true);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref firstReloadCanceled, 1);
                throw;
            }
        });

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<ITestSessionService>(sessionService);
        });

        await pipeline.InitializeAsync();

        var firstReload = pipeline.ReloadScopeAsync<SessionScope>();
        await firstReloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var secondReload = pipeline.ReloadScopeAsync<SessionScope>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstReload);
        await secondReload;

        Assert.Equal(3, sessionService.Attempts);
        Assert.Equal(1, Volatile.Read(ref firstReloadCanceled));
        Assert.Equal(RuntimeExecutionState.Ready, pipeline.GetRuntimeStatus().State);
    }

    [Fact]
    public async Task ReloadScopeAsync_ModuleConcurrentWithInFlightLoad_CancelsStaleLoad()
    {
        var firstLoadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstLoadCanceled = 0;
        var sceneService = new AttemptControlledSceneService((_, _) => Task.CompletedTask);
        var moduleService = new AttemptControlledModuleService(async (attempt, cancellationToken) =>
        {
            if (attempt != 1)
                return;

            firstLoadStarted.TrySetResult(true);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref firstLoadCanceled, 1);
                throw;
            }
        });

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(sceneService)));
            builder.Module(new TestModuleScope(m => m.RegisterInstance<ITestModuleService>(moduleService)));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();

        var firstLoad = pipeline.LoadModuleAsync<TestModuleScope>();
        await firstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var reload = pipeline.ReloadScopeAsync<TestModuleScope>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstLoad);
        await reload;

        Assert.Equal(2, moduleService.Attempts);
        Assert.Equal(1, Volatile.Read(ref firstLoadCanceled));
        Assert.Equal(RuntimeExecutionState.Ready, pipeline.GetRuntimeStatus().State);
    }

    [Fact]
    public async Task ReloadScopeAsync_ModuleConcurrentRequests_WithActivationExit_CancelsStaleOperationDeterministically()
    {
        var sceneService = new AttemptControlledSceneService((_, _) => Task.CompletedTask);
        var moduleService = new ConcurrentModuleActivationService();

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(sceneService)));
            builder.Module(new TestModuleScope(m => m.RegisterInstance<ITestModuleService>(moduleService)));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadModuleAsync<TestModuleScope>();
        moduleService.ResetForConcurrentOperation();

        var firstReload = pipeline.ReloadScopeAsync<TestModuleScope>();
        await moduleService.FirstExitStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var secondReload = pipeline.ReloadScopeAsync<TestModuleScope>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstReload);
        await secondReload;

        Assert.Equal(2, moduleService.Attempts);
        Assert.Equal(1, moduleService.EnterCalls);
        Assert.Equal(2, moduleService.ExitCalls);
        Assert.Equal(1, moduleService.CanceledExitCalls);
        Assert.Equal(RuntimeExecutionState.Ready, pipeline.GetRuntimeStatus().State);
    }

    [Fact]
    public async Task ReloadScopeAsync_SessionConcurrentRequests_WithActivationExit_CancelsStaleOperationDeterministically()
    {
        var sessionService = new ConcurrentSessionActivationService();
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<ITestSessionService>(sessionService);
        });

        await pipeline.InitializeAsync();
        sessionService.ResetForConcurrentOperation();

        var firstReload = pipeline.ReloadScopeAsync<SessionScope>();
        await sessionService.FirstExitStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var secondReload = pipeline.ReloadScopeAsync<SessionScope>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstReload);
        await secondReload;

        Assert.Equal(2, sessionService.Attempts);
        Assert.Equal(1, sessionService.EnterCalls);
        Assert.Equal(2, sessionService.ExitCalls);
        Assert.Equal(1, sessionService.CanceledExitCalls);
        Assert.Equal(RuntimeExecutionState.Ready, pipeline.GetRuntimeStatus().State);
    }

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
            AssertOperationIdPrefix(operationKind, operationSnapshots[0].OperationId);

            Assert.Equal(RuntimeLoadingOperationStage.Preparing, operationSnapshots[0].Stage);

            var completedSnapshot = operationSnapshots[operationSnapshots.Length - 1];
            Assert.Equal(RuntimeLoadingOperationStage.Completed, completedSnapshot.Stage);
            Assert.Equal(RuntimeLoadingOperationState.Completed, completedSnapshot.State);
            Assert.Equal(100d, completedSnapshot.Percent);

            for (var index = 1; index < operationSnapshots.Length; index++)
            {
                Assert.True((int)operationSnapshots[index].Stage >= (int)operationSnapshots[index - 1].Stage);
                Assert.True(operationSnapshots[index].Percent >= operationSnapshots[index - 1].Percent);
            }
        }
    }

    private static void AssertOperationIdPrefix(RuntimeLoadingOperationKind operationKind, string operationId)
    {
        var expectedPrefix = operationKind switch
        {
            RuntimeLoadingOperationKind.RestartSession => "restart_session-",
            RuntimeLoadingOperationKind.ReloadScene => "reload_scene-",
            RuntimeLoadingOperationKind.ReloadModule => "reload_module-",
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, "Unsupported operation kind.")
        };

        Assert.StartsWith(expectedPrefix, operationId, StringComparison.Ordinal);
    }

    private sealed class CollectingRuntimeLoadingProgressObserver : IRuntimeLoadingProgressObserver
    {
        public List<RuntimeLoadingOperationSnapshot> Snapshots { get; } = new();

        public void OnLoadingProgress(RuntimeLoadingOperationSnapshot snapshot)
        {
            Snapshots.Add(snapshot);
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
