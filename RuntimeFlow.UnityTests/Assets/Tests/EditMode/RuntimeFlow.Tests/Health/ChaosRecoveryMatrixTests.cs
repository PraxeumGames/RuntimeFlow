using NUnit.Framework;
using System.Linq;
using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using VContainer;

namespace RuntimeFlow.Tests
{

public sealed class ChaosRecoveryMatrixTests
{
    [Test]
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

        Assert.That(sessionService.Attempts, Is.GreaterThanOrEqualTo(2));
        Assert.That(healthObserver.RecoveryTriggeredCount, Is.GreaterThanOrEqualTo(1));
        Assert.That(pipeline.GetRuntimeStatus().State, Is.EqualTo(RuntimeExecutionState.Ready));
    }

    [Test]
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

        await AsyncTestAssert.ThrowsAsync<RuntimeHealthCriticalException>(async () => await pipeline.RunAsync(NoopSceneLoader.Instance));
        Assert.That(pipeline.GetRuntimeStatus().State, Is.EqualTo(RuntimeExecutionState.Failed));
    }

    [Test]
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

        Assert.That(sceneService.Attempts, Is.EqualTo(2));
        Assert.That(
            retryObserver.Decisions.Any(decision => decision.OperationCode == RuntimeOperationCodes.LoadSceneScope && decision.WillRetry),
            Is.True);
    }

    [Test]
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

        await AsyncTestAssert.CatchAsync<OperationCanceledException>(async () => await firstLoad);
        await reload;

        Assert.That(moduleService.Attempts, Is.EqualTo(2));
        Assert.That(Volatile.Read(ref firstAttemptCanceled), Is.EqualTo(1));
        Assert.That(pipeline.GetRuntimeStatus().State, Is.EqualTo(RuntimeExecutionState.Ready));
    }

    [Test]
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

        Assert.That(moduleService.Attempts, Is.GreaterThanOrEqualTo(2));
        Assert.That(healthObserver.RecoveryTriggeredCount, Is.GreaterThanOrEqualTo(1));
        Assert.That(pipeline.GetRuntimeStatus().State, Is.EqualTo(RuntimeExecutionState.Ready));
    }

    [Test]
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

        await AsyncTestAssert.CatchAsync<OperationCanceledException>(async () => await firstRestart);
        Assert.That(pipeline.GetRuntimeStatus().State, Is.EqualTo(RuntimeExecutionState.Ready));
    }

    [Test]
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

        Assert.That(sessionService.Attempts, Is.EqualTo(2));
        Assert.That(
            retryObserver.Decisions.Any(decision => decision.OperationCode == RuntimeOperationCodes.Initialize && decision.WillRetry),
            Is.True);
    }

    [Test]
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
        await AsyncTestAssert.CatchAsync<OperationCanceledException>(async () =>
            await pipeline.RunAsync(NoopSceneLoader.Instance, cancellationToken: cancellationSource.Token));
        Assert.That(pipeline.GetRuntimeStatus().State, Is.EqualTo(RuntimeExecutionState.Degraded));

        await pipeline.RunAsync(NoopSceneLoader.Instance);
        Assert.That(pipeline.GetRuntimeStatus().State, Is.EqualTo(RuntimeExecutionState.Ready));
    }

    [Test]
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

        Assert.That(pipeline.GetRuntimeStatus().State, Is.EqualTo(RuntimeExecutionState.Ready));
    }

    private interface IHeadlessSessionBootstrapService : ISessionInitializableService
    {
    }

    private sealed class HeadlessSessionBootstrapService : IHeadlessSessionBootstrapService
    {
        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(5, cancellationToken);
        }
    }
}
}
