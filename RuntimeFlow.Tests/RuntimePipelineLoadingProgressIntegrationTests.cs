using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using VContainer;

namespace RuntimeFlow.Tests;

public sealed class RuntimePipelineLoadingProgressIntegrationTests
{
    [Fact]
    public async Task RunFlow_ModuleLoadAndReload_EmitsMonotonicProgressSnapshots()
    {
        var observer = new CollectingRuntimeLoadingProgressObserver();
        var moduleService = new FlowModuleService();

        var pipeline = RuntimePipeline.Create(
                builder =>
                {
                    builder.DefineSceneScope<FlowSceneScope>();
                    builder.DefineModuleScope<FlowModuleScope>();
                    builder.For<FlowSceneScope>()
                        .Register<FlowSceneService>(Lifetime.Singleton)
                        .As<IFlowSceneService>()
                        .AsSelf();
                    builder.For<FlowModuleScope>().RegisterInstance<IFlowModuleService>(moduleService);
                },
                options => options.LoadingProgressObserver = observer)
            .ConfigureFlow(new DelegateRuntimeFlowScenario(async (context, token) =>
            {
                await context.InitializeAsync(token).ConfigureAwait(false);
                await context.LoadScopeSceneAsync<FlowSceneScope>(token).ConfigureAwait(false);
                await context.LoadScopeModuleAsync<FlowModuleScope>(token).ConfigureAwait(false);
                await context.ReloadScopeModuleAsync<FlowModuleScope>(token).ConfigureAwait(false);
            }));

        await pipeline.RunAsync(NoopSceneLoader.Instance);

        Assert.Equal("run_flow", pipeline.GetRuntimeStatus().CurrentOperationCode);
        Assert.Equal(2, moduleService.Attempts);

        var moduleSnapshots = observer.Snapshots
            .Where(snapshot => snapshot.OperationKind is RuntimeLoadingOperationKind.LoadModule or RuntimeLoadingOperationKind.ReloadModule)
            .ToArray();

        Assert.NotEmpty(moduleSnapshots);

        var groupedByOperation = moduleSnapshots
            .GroupBy(snapshot => snapshot.OperationId)
            .ToArray();

        Assert.Equal(2, groupedByOperation.Length);

        var loadModuleSnapshots = groupedByOperation
            .Single(group => group.All(snapshot => snapshot.OperationKind == RuntimeLoadingOperationKind.LoadModule))
            .ToArray();
        var reloadModuleSnapshots = groupedByOperation
            .Single(group => group.All(snapshot => snapshot.OperationKind == RuntimeLoadingOperationKind.ReloadModule))
            .ToArray();

        Assert.StartsWith("load_module-", loadModuleSnapshots[0].OperationId, StringComparison.Ordinal);
        Assert.StartsWith("reload_module-", reloadModuleSnapshots[0].OperationId, StringComparison.Ordinal);

        AssertProgressProgression(loadModuleSnapshots);
        AssertProgressProgression(reloadModuleSnapshots);
    }

    [Fact]
    public async Task LoadAndReloadModule_HeavyBindings_EmitMonotonicProgressPerOperation()
    {
        var observer = new CollectingRuntimeLoadingProgressObserver();
        var heavyService = new HeavyFlowModuleService();

        var pipeline = RuntimePipeline.Create(
                builder =>
                {
                    builder.DefineSceneScope<FlowSceneScope>();
                    builder.DefineModuleScope<FlowModuleScope>();
                    builder.For<FlowSceneScope>()
                        .Register<FlowSceneService>(Lifetime.Singleton)
                        .As<IFlowSceneService>()
                        .AsSelf();
                    builder.For<FlowModuleScope>()
                        .RegisterInstance<IHeavyFlowModuleStageA>(heavyService)
                        .As<IHeavyFlowModuleStageB>()
                        .As<IHeavyFlowModuleStageC>();
                },
                options => options.LoadingProgressObserver = observer);

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<FlowSceneScope>();
        await pipeline.LoadModuleAsync<FlowModuleScope>();
        await pipeline.ReloadModuleAsync<FlowModuleScope>();

        Assert.Equal(2, heavyService.Attempts);

        var groupedByOperation = observer.Snapshots
            .Where(snapshot => snapshot.OperationKind is RuntimeLoadingOperationKind.LoadModule or RuntimeLoadingOperationKind.ReloadModule)
            .GroupBy(snapshot => snapshot.OperationId)
            .ToArray();

        Assert.Equal(2, groupedByOperation.Length);

        foreach (var snapshots in groupedByOperation.Select(group => group.ToArray()))
        {
            Assert.True(snapshots.Length >= 6);
            AssertProgressProgression(snapshots);
        }
    }

    [Fact]
    public async Task ReloadModule_WithActivationHooks_EmitsActivationAndDeactivationSnapshots()
    {
        var observer = new CollectingRuntimeLoadingProgressObserver();
        var moduleService = new ActivatingFlowModuleService();

        var pipeline = RuntimePipeline.Create(
            builder =>
            {
                builder.DefineSceneScope<FlowSceneScope>();
                builder.DefineModuleScope<FlowModuleScope>();
                builder.For<FlowSceneScope>()
                    .Register<FlowSceneService>(Lifetime.Singleton)
                    .As<IFlowSceneService>()
                    .AsSelf();
                builder.For<FlowModuleScope>().RegisterInstance<IActivatingFlowModuleService>(moduleService);
            },
            options => options.LoadingProgressObserver = observer);

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<FlowSceneScope>();
        await pipeline.LoadModuleAsync<FlowModuleScope>();
        observer.Snapshots.Clear();

        await pipeline.ReloadModuleAsync<FlowModuleScope>();

        var reloadSnapshots = observer.Snapshots
            .Where(snapshot => snapshot.OperationKind == RuntimeLoadingOperationKind.ReloadModule)
            .ToArray();

        Assert.NotEmpty(reloadSnapshots);
        Assert.Contains(
            reloadSnapshots,
            snapshot => snapshot.Message?.Contains("deactivation", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(
            reloadSnapshots,
            snapshot => snapshot.Message?.Contains("activation", StringComparison.OrdinalIgnoreCase) == true);
        AssertProgressProgression(reloadSnapshots);
    }

    private static void AssertProgressProgression(IReadOnlyList<RuntimeLoadingOperationSnapshot> snapshots)
    {
        Assert.NotEmpty(snapshots);
        Assert.Equal(RuntimeLoadingOperationStage.Preparing, snapshots[0].Stage);
        Assert.Equal(RuntimeLoadingOperationStage.Completed, snapshots[snapshots.Count - 1].Stage);
        Assert.Equal(100d, snapshots[snapshots.Count - 1].Percent);

        for (var index = 1; index < snapshots.Count; index++)
        {
            Assert.True((int)snapshots[index].Stage >= (int)snapshots[index - 1].Stage);
            Assert.True(snapshots[index].Percent >= snapshots[index - 1].Percent);
        }
    }

    private sealed class CollectingRuntimeLoadingProgressObserver : IRuntimeLoadingProgressObserver
    {
        public List<RuntimeLoadingOperationSnapshot> Snapshots { get; } = new();

        public void OnLoadingProgress(RuntimeLoadingOperationSnapshot snapshot)
        {
            Snapshots.Add(snapshot);
        }
    }

    private interface IFlowSceneService : ISceneInitializableService;

    private sealed class FlowSceneService : IFlowSceneService
    {
        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private interface IFlowModuleService : IModuleInitializableService
    {
        int Attempts { get; }
    }

    private sealed class FlowModuleService : IFlowModuleService
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            return Task.CompletedTask;
        }
    }

    private interface IHeavyFlowModuleStageA : IModuleInitializableService;
    private interface IHeavyFlowModuleStageB : IModuleInitializableService;
    private interface IHeavyFlowModuleStageC : IModuleInitializableService;
    private interface IActivatingFlowModuleService : IModuleInitializableService, IModuleScopeActivationService;

    private sealed class HeavyFlowModuleService : IHeavyFlowModuleStageA, IHeavyFlowModuleStageB, IHeavyFlowModuleStageC
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            return Task.Delay(5, cancellationToken);
        }
    }

    private sealed class ActivatingFlowModuleService : IActivatingFlowModuleService
    {
        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task OnScopeActivatedAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task OnScopeDeactivatingAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FlowSceneScope { }
    private sealed class FlowModuleScope { }
}
