using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

public sealed partial class RuntimeScopeReloadApiTests
{
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
}
