using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using VContainer;
using Xunit;

namespace RuntimeFlow.Tests;

public sealed class ChaosRecoveryMatrixTests
{
    [Fact]
    public async Task TimeoutOnSessionService_TriggersAutoRecovery_WhenLimitAllows()
    {
        var sessionService = new AttemptControlledSessionService(async (attempt, cancellationToken) =>
        {
            var delay = attempt == 1 ? TimeSpan.FromMilliseconds(220) : TimeSpan.FromMilliseconds(10);
            await Task.Delay(delay, cancellationToken);
        });
        var healthObserver = new CollectingHealthObserver();

        var pipeline = RuntimePipeline.Create(
                builder =>
                {
                    builder.DefineSessionScope();
                    builder.Session().RegisterInstance<ITestSessionService>(sessionService);
                },
                options =>
                {
                    options.Health.Enabled = true;
                    options.Health.MinimumExpectedServiceDuration = TimeSpan.FromMilliseconds(5);
                    options.Health.MinimumServiceTimeout = TimeSpan.FromMilliseconds(60);
                    options.Health.MaximumServiceTimeout = TimeSpan.FromMilliseconds(60);
                    options.Health.SlowServiceMultiplier = 1.0;
                    options.Health.MaxAutoSessionRestartsPerRun = 1;
                    options.HealthObserver = healthObserver;
                })
            .ConfigureFlow(new DelegateRuntimeFlowScenario((context, token) => context.InitializeAsync(token)));

        await pipeline.RunAsync(NoopSceneLoader.Instance);

        Assert.True(sessionService.Attempts >= 2);
        Assert.True(healthObserver.RecoveryTriggeredCount >= 1);
        Assert.Equal(RuntimeExecutionState.Ready, pipeline.GetRuntimeStatus().State);
    }

    [Fact]
    public async Task TimeoutAfterRecoveryLimit_EscalatesFailure()
    {
        var sessionService = new AttemptControlledSessionService((_, cancellationToken) =>
            Task.Delay(TimeSpan.FromMilliseconds(240), cancellationToken));

        var pipeline = RuntimePipeline.Create(
                builder =>
                {
                    builder.DefineSessionScope();
                    builder.Session().RegisterInstance<ITestSessionService>(sessionService);
                },
                options =>
                {
                    options.Health.Enabled = true;
                    options.Health.MinimumExpectedServiceDuration = TimeSpan.FromMilliseconds(5);
                    options.Health.MinimumServiceTimeout = TimeSpan.FromMilliseconds(60);
                    options.Health.MaximumServiceTimeout = TimeSpan.FromMilliseconds(60);
                    options.Health.SlowServiceMultiplier = 1.0;
                    options.Health.MaxAutoSessionRestartsPerRun = 1;
                })
            .ConfigureFlow(new DelegateRuntimeFlowScenario((context, token) => context.InitializeAsync(token)));

        await Assert.ThrowsAsync<RuntimeHealthCriticalException>(() => pipeline.RunAsync(NoopSceneLoader.Instance));
        Assert.Equal(RuntimeExecutionState.Failed, pipeline.GetRuntimeStatus().State);
    }

    [Fact]
    public async Task PartialFailureSceneStep_RetriesAndSucceeds()
    {
        var retryObserver = new CollectingRetryObserver();
        var sceneService = new AttemptControlledSceneService((attempt, _) =>
        {
            if (attempt == 1)
                throw new TimeoutException("Scene step transient timeout.");
            return Task.CompletedTask;
        });

        var pipeline = RuntimePipeline.Create(
                builder =>
                {
                    builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(sceneService)));
                },
                options =>
                {
                    options.RetryObserver = retryObserver;
                    options.RetryPolicy.Enabled = true;
                    options.RetryPolicy.MaxAttempts = 3;
                    options.RetryPolicy.InitialBackoff = TimeSpan.Zero;
                    options.RetryPolicy.MaxBackoff = TimeSpan.Zero;
                    options.RetryPolicy.UseJitter = false;
                })
            .ConfigureFlow(new DelegateRuntimeFlowScenario(async (context, token) =>
            {
                await context.InitializeAsync(token).ConfigureAwait(false);
                await context.LoadScopeSceneAsync<TestSceneScope>(token).ConfigureAwait(false);
            }));

        await pipeline.RunAsync(NoopSceneLoader.Instance);

        Assert.Equal(2, sceneService.Attempts);
        Assert.Contains(
            retryObserver.Decisions,
            decision => decision.OperationCode == RuntimeOperationCodes.LoadSceneScope && decision.WillRetry);
    }

    [Fact]
    public async Task ConcurrentModuleLoadAndReload_CancelsStaleLoadDeterministically()
    {
        var sceneService = new AttemptControlledSceneService((_, _) => Task.CompletedTask);
        var firstAttemptStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAttemptCanceled = 0;
        var moduleService = new AttemptControlledModuleService(async (attempt, cancellationToken) =>
        {
            if (attempt != 1)
                return;

            firstAttemptStarted.TrySetResult(true);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref firstAttemptCanceled, 1);
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
        await firstAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var reload = pipeline.ReloadModuleAsync<TestModuleScope>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstLoad);
        await reload;

        Assert.Equal(2, moduleService.Attempts);
        Assert.Equal(1, Volatile.Read(ref firstAttemptCanceled));
        Assert.Equal(RuntimeExecutionState.Ready, pipeline.GetRuntimeStatus().State);
    }

    [Fact]
    public async Task ModuleLoadTimeout_RecoversSessionAndSucceedsOnRetry()
    {
        var sceneService = new AttemptControlledSceneService((_, _) => Task.CompletedTask);
        var moduleService = new AttemptControlledModuleService(async (attempt, cancellationToken) =>
        {
            var delay = attempt == 1 ? TimeSpan.FromMilliseconds(220) : TimeSpan.FromMilliseconds(10);
            await Task.Delay(delay, cancellationToken);
        });
        var healthObserver = new CollectingHealthObserver();

        var pipeline = RuntimePipeline.Create(
                builder =>
                {
                    builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(sceneService)));
                    builder.Module(new TestModuleScope(m => m.RegisterInstance<ITestModuleService>(moduleService)));
                },
                options =>
                {
                    options.Health.Enabled = true;
                    options.Health.MinimumExpectedServiceDuration = TimeSpan.FromMilliseconds(5);
                    options.Health.MinimumServiceTimeout = TimeSpan.FromMilliseconds(60);
                    options.Health.MaximumServiceTimeout = TimeSpan.FromMilliseconds(60);
                    options.Health.SlowServiceMultiplier = 1.0;
                    options.Health.MaxAutoSessionRestartsPerRun = 1;
                    options.HealthObserver = healthObserver;

                    options.RetryPolicy.Enabled = true;
                    options.RetryPolicy.MaxAttempts = 2;
                    options.RetryPolicy.InitialBackoff = TimeSpan.Zero;
                    options.RetryPolicy.MaxBackoff = TimeSpan.Zero;
                    options.RetryPolicy.UseJitter = false;
                })
            .ConfigureFlow(new DelegateRuntimeFlowScenario(async (context, token) =>
            {
                await context.InitializeAsync(token).ConfigureAwait(false);
                await context.LoadScopeSceneAsync<TestSceneScope>(token).ConfigureAwait(false);
                await context.LoadScopeModuleAsync<TestModuleScope>(token).ConfigureAwait(false);
            }));

        await pipeline.RunAsync(NoopSceneLoader.Instance);

        Assert.True(moduleService.Attempts >= 2);
        Assert.True(healthObserver.RecoveryTriggeredCount >= 1);
        Assert.Equal(RuntimeExecutionState.Ready, pipeline.GetRuntimeStatus().State);
    }

    [Fact]
    public async Task RestartStormConcurrentRestarts_CancelsStaleGeneration()
    {
        var sessionService = new AttemptControlledSessionService((_, cancellationToken) =>
            Task.Delay(TimeSpan.FromMilliseconds(180), cancellationToken));

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<ITestSessionService>(sessionService);
        });

        await pipeline.InitializeAsync();

        var firstRestart = pipeline.RestartSessionAsync();
        await Task.Delay(30);
        await pipeline.RestartSessionAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstRestart);
        Assert.Equal(RuntimeExecutionState.Ready, pipeline.GetRuntimeStatus().State);
    }

    [Fact]
    public async Task MixedFailuresTransientThenSuccess_UsesBoundedRetry()
    {
        var retryObserver = new CollectingRetryObserver();
        var sessionService = new AttemptControlledSessionService((attempt, _) =>
        {
            if (attempt == 1)
                throw new TimeoutException("Network flap during initialization.");
            return Task.CompletedTask;
        });

        var pipeline = RuntimePipeline.Create(
                builder =>
                {
                    builder.DefineSessionScope();
                    builder.Session().RegisterInstance<ITestSessionService>(sessionService);
                },
                options =>
                {
                    options.RetryObserver = retryObserver;
                    options.RetryPolicy.Enabled = true;
                    options.RetryPolicy.MaxAttempts = 2;
                    options.RetryPolicy.InitialBackoff = TimeSpan.Zero;
                    options.RetryPolicy.MaxBackoff = TimeSpan.Zero;
                    options.RetryPolicy.UseJitter = false;
                })
            .ConfigureFlow(new DelegateRuntimeFlowScenario((context, token) => context.InitializeAsync(token)));

        await pipeline.RunAsync(NoopSceneLoader.Instance);

        Assert.Equal(2, sessionService.Attempts);
        Assert.Contains(
            retryObserver.Decisions,
            decision => decision.OperationCode == RuntimeOperationCodes.Initialize && decision.WillRetry);
    }

    [Fact]
    public async Task BackgroundResumeDuringInit_CancelThenResumeWithoutHang()
    {
        var sessionService = new AttemptControlledSessionService((_, cancellationToken) =>
            Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken));

        var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().RegisterInstance<ITestSessionService>(sessionService);
            })
            .ConfigureFlow(new DelegateRuntimeFlowScenario((context, token) => context.InitializeAsync(token)));

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(35));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.RunAsync(NoopSceneLoader.Instance, cancellationToken: cancellationSource.Token));
        Assert.Equal(RuntimeExecutionState.Degraded, pipeline.GetRuntimeStatus().State);

        await pipeline.RunAsync(NoopSceneLoader.Instance);
        Assert.Equal(RuntimeExecutionState.Ready, pipeline.GetRuntimeStatus().State);
    }

    [Fact]
    public async Task NonUnityHeadlessRestart_RunAndRestartRemainHealthy()
    {
        var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session()
                    .Register<HeadlessSessionBootstrapService>(Lifetime.Singleton)
                    .As<IHeadlessSessionBootstrapService>()
                    .AsSelf();
            })
            .ConfigureFlow(new DelegateRuntimeFlowScenario((context, token) => context.InitializeAsync(token)));

        await pipeline.RunAsync(NoopSceneLoader.Instance);
        await pipeline.RestartSessionAsync();
        await pipeline.RunAsync(NoopSceneLoader.Instance);

        Assert.Equal(RuntimeExecutionState.Ready, pipeline.GetRuntimeStatus().State);
    }

    private interface IHeadlessSessionBootstrapService : ISessionInitializableService;

    private sealed class HeadlessSessionBootstrapService : IHeadlessSessionBootstrapService
    {
        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(5, cancellationToken);
        }
    }
}
