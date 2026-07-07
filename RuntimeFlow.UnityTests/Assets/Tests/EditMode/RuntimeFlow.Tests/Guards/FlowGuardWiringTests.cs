using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{
    public sealed class FlowGuardWiringTests
    {
        [Test]
        public async Task LoadScene_GuardBlocks_ThrowsGuardFailedException()
        {
            var guard = new DenyAtStageGuard(
                RuntimeFlowGuardStage.BeforeSceneLoad, "scene_blocked", "Scene loading is blocked.");

            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
            });
            pipeline.ConfigureGuards(guard);

            await pipeline.InitializeAsync();

            var ex = await AsyncTestAssert.ThrowsAsync<RuntimeFlowGuardFailedException>(
                () => pipeline.LoadSceneAsync<TestSceneScope>());

            Assert.That(ex, Is.Not.Null);
            Assert.That(ex!.Stage, Is.EqualTo(RuntimeFlowGuardStage.BeforeSceneLoad));
            Assert.That(ex.ReasonCode, Is.EqualTo("scene_blocked"));
        }

        [Test]
        public async Task LoadScene_GuardAllows_OperationExecutes()
        {
            var guard = new DenyAtStageGuard(
                RuntimeFlowGuardStage.BeforeSessionRestart, "restart_blocked", "Not this stage.");

            var sceneService = new AttemptControlledSceneService((_, _) => Task.CompletedTask);

            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(sceneService)));
            });
            pipeline.ConfigureGuards(guard);

            await pipeline.InitializeAsync();
            await pipeline.LoadSceneAsync<TestSceneScope>();

            Assert.That(sceneService.Attempts, Is.EqualTo(1));
        }

        [Test]
        public async Task MultipleGuards_AllEvaluated_FirstDenyWins()
        {
            var allowGuard = new AllowGuard();
            var denyGuard = new DenyAtStageGuard(
                RuntimeFlowGuardStage.BeforeModuleLoad, "module_blocked", "Blocked by second guard.");
            var neverReachedGuard = new TrackingGuard(RuntimeFlowGuardStage.BeforeModuleLoad);

            var sceneService = new AttemptControlledSceneService((_, _) => Task.CompletedTask);
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(sceneService)));
            });
            pipeline.ConfigureGuards(allowGuard, denyGuard, neverReachedGuard);

            await pipeline.InitializeAsync();
            await pipeline.LoadSceneAsync<TestSceneScope>();

            var ex = await AsyncTestAssert.ThrowsAsync<RuntimeFlowGuardFailedException>(
                () => pipeline.LoadModuleAsync<TestModuleScope>());

            Assert.That(ex, Is.Not.Null);
            Assert.That(ex!.ReasonCode, Is.EqualTo("module_blocked"));
            Assert.That(neverReachedGuard.WasEvaluated, Is.False, "Third guard should not have been evaluated.");
        }

        [Test]
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
                builder.DefineSessionScope();
            });
            pipeline.ConfigureFlow(flow);
            pipeline.ConfigureGuards(guard);

            var ex = await AsyncTestAssert.ThrowsAsync<RuntimeFlowGuardFailedException>(
                () => pipeline.RunAsync(NoopSceneLoader.Instance));

            Assert.That(ex, Is.Not.Null);
            Assert.That(ex!.Stage, Is.EqualTo(RuntimeFlowGuardStage.BeforeNavigation));
            Assert.That(ex.ReasonCode, Is.EqualTo("nav_blocked"));
        }

        [Test]
        public async Task NoGuards_OperationsWorkNormally()
        {
            var sceneService = new AttemptControlledSceneService((_, _) => Task.CompletedTask);
            var moduleService = new AttemptControlledModuleService((_, _) => Task.CompletedTask);

            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(sceneService)));
                builder.Module(new TestModuleScope(m => m.RegisterInstance<ITestModuleService>(moduleService)));
            });
            // No ConfigureGuards call

            await pipeline.InitializeAsync();
            await pipeline.LoadSceneAsync<TestSceneScope>();
            await pipeline.LoadModuleAsync<TestModuleScope>();

            Assert.That(sceneService.Attempts, Is.EqualTo(1));
            Assert.That(moduleService.Attempts, Is.EqualTo(1));
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
}
