using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using Xunit;

namespace RuntimeFlow.Tests;

public sealed class FlowGuardWiringTests
{
    [Fact]
    public async Task LoadScene_GuardBlocks_ThrowsGuardFailedException()
    {
        var guard = new DenyAtStageGuard(
            RuntimeFlowGuardStage.BeforeSceneLoad, "scene_blocked", "Scene loading is blocked.");

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope<TestSessionScope>();
            builder.DefineSceneScope<TestSceneScope>();
        });
        pipeline.ConfigureGuards(guard);

        await pipeline.InitializeAsync();

        var ex = await Assert.ThrowsAsync<RuntimeFlowGuardFailedException>(
            () => pipeline.LoadSceneAsync<TestSceneScope>());

        Assert.Equal(RuntimeFlowGuardStage.BeforeSceneLoad, ex.Stage);
        Assert.Equal("scene_blocked", ex.ReasonCode);
    }

    [Fact]
    public async Task LoadScene_GuardAllows_OperationExecutes()
    {
        var guard = new DenyAtStageGuard(
            RuntimeFlowGuardStage.BeforeSessionRestart, "restart_blocked", "Not this stage.");

        var sceneService = new AttemptControlledSceneService((_, _) => Task.CompletedTask);

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope<TestSessionScope>();
            builder.DefineSceneScope<TestSceneScope>();
            builder.For<TestSceneScope>().RegisterInstance<ITestSceneService>(sceneService);
        });
        pipeline.ConfigureGuards(guard);

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();

        Assert.Equal(1, sceneService.Attempts);
    }

    [Fact]
    public async Task MultipleGuards_AllEvaluated_FirstDenyWins()
    {
        var allowGuard = new AllowGuard();
        var denyGuard = new DenyAtStageGuard(
            RuntimeFlowGuardStage.BeforeModuleLoad, "module_blocked", "Blocked by second guard.");
        var neverReachedGuard = new TrackingGuard(RuntimeFlowGuardStage.BeforeModuleLoad);

        var sceneService = new AttemptControlledSceneService((_, _) => Task.CompletedTask);
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope<TestSessionScope>();
            builder.DefineSceneScope<TestSceneScope>();
            builder.DefineModuleScope<TestModuleScope>();
            builder.For<TestSceneScope>().RegisterInstance<ITestSceneService>(sceneService);
        });
        pipeline.ConfigureGuards(allowGuard, denyGuard, neverReachedGuard);

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();

        var ex = await Assert.ThrowsAsync<RuntimeFlowGuardFailedException>(
            () => pipeline.LoadModuleAsync<TestModuleScope>());

        Assert.Equal("module_blocked", ex.ReasonCode);
        Assert.False(neverReachedGuard.WasEvaluated, "Third guard should not have been evaluated.");
    }

    [Fact]
    public async Task GoToAsync_GuardCheckedBeforeNavigation()
    {
        var guard = new DenyAtStageGuard(
            RuntimeFlowGuardStage.BeforeNavigation, "nav_blocked", "Navigation blocked.");

        var flow = new DelegateRuntimeFlowScenario(async (runner, ct) =>
        {
            await runner.InitializeAsync(ct);
            var route = SceneRoute.ToScene<TestSceneScope>("TestScene");
            await runner.GoToAsync(route, ct);
        });

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope<TestSessionScope>();
            builder.DefineSceneScope<TestSceneScope>();
        });
        pipeline.ConfigureFlow(flow);
        pipeline.ConfigureGuards(guard);

        var ex = await Assert.ThrowsAsync<RuntimeFlowGuardFailedException>(
            () => pipeline.RunAsync(NoopSceneLoader.Instance));

        Assert.Equal(RuntimeFlowGuardStage.BeforeNavigation, ex.Stage);
        Assert.Equal("nav_blocked", ex.ReasonCode);
    }

    [Fact]
    public async Task NoGuards_OperationsWorkNormally()
    {
        var sceneService = new AttemptControlledSceneService((_, _) => Task.CompletedTask);
        var moduleService = new AttemptControlledModuleService((_, _) => Task.CompletedTask);

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope<TestSessionScope>();
            builder.DefineSceneScope<TestSceneScope>();
            builder.DefineModuleScope<TestModuleScope>();
            builder.For<TestSceneScope>().RegisterInstance<ITestSceneService>(sceneService);
            builder.For<TestModuleScope>().RegisterInstance<ITestModuleService>(moduleService);
        });
        // No ConfigureGuards call

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadModuleAsync<TestModuleScope>();

        Assert.Equal(1, sceneService.Attempts);
        Assert.Equal(1, moduleService.Attempts);
    }

    private sealed class AllowGuard : IRuntimeFlowGuard
    {
        public Task<RuntimeFlowGuardResult> EvaluateAsync(
            RuntimeFlowGuardContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(RuntimeFlowGuardResult.Allow());
        }
    }

    private sealed class TrackingGuard : IRuntimeFlowGuard
    {
        private readonly RuntimeFlowGuardStage _trackStage;
        public bool WasEvaluated { get; private set; }

        public TrackingGuard(RuntimeFlowGuardStage trackStage)
        {
            _trackStage = trackStage;
        }

        public Task<RuntimeFlowGuardResult> EvaluateAsync(
            RuntimeFlowGuardContext context, CancellationToken cancellationToken = default)
        {
            if (context.Stage == _trackStage)
                WasEvaluated = true;
            return Task.FromResult(RuntimeFlowGuardResult.Allow());
        }
    }
}
