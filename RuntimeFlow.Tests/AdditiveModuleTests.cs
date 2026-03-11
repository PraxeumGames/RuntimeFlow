using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

public sealed class AdditiveModuleTests
{
    [Fact]
    public async Task LoadAdditiveModule_CreatesSecondModule()
    {
        var primaryService = new TrackingModuleService();
        var additiveService = new TrackingModuleService();

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope<TestSessionScope>();
            builder.DefineSceneScope<TestSceneScope>();
            builder.DefineModuleScope<PrimaryModule>();
            builder.DefineModuleScope<AdditiveModuleA>();
            builder.For<TestSceneScope>().RegisterInstance<ISimpleSceneService>(new SimpleSceneService());
            builder.For<PrimaryModule>().RegisterInstance<IPrimaryModuleService>(primaryService);
            builder.For<AdditiveModuleA>().RegisterInstance<IAdditiveModuleServiceA>(additiveService);
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadModuleAsync<PrimaryModule>();
        await pipeline.LoadAdditiveModuleAsync<AdditiveModuleA>();

        Assert.Equal(ScopeLifecycleState.Active, pipeline.GetScopeState<PrimaryModule>());
        Assert.Equal(ScopeLifecycleState.Active, pipeline.GetScopeState<AdditiveModuleA>());
        Assert.True(primaryService.Initialized);
        Assert.True(additiveService.Initialized);
    }

    [Fact]
    public async Task UnloadAdditiveModule_DisposesCorrectly()
    {
        var additiveService = new DisposableModuleService();

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope<TestSessionScope>();
            builder.DefineSceneScope<TestSceneScope>();
            builder.DefineModuleScope<AdditiveModuleA>();
            builder.For<TestSceneScope>().RegisterInstance<ISimpleSceneService>(new SimpleSceneService());
            builder.For<AdditiveModuleA>().RegisterInstance<IDisposableAdditiveService>(additiveService);
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadAdditiveModuleAsync<AdditiveModuleA>();

        Assert.True(additiveService.Initialized);
        Assert.False(additiveService.Disposed);

        await pipeline.UnloadAdditiveModuleAsync<AdditiveModuleA>();

        Assert.True(additiveService.Disposed);
        Assert.Equal(ScopeLifecycleState.Disposed, pipeline.GetScopeState<AdditiveModuleA>());
    }

    [Fact]
    public async Task LoadScene_CleansUpAdditiveModules()
    {
        var additiveService = new DisposableModuleService();

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope<TestSessionScope>();
            builder.DefineSceneScope<TestSceneScope>();
            builder.DefineSceneScope<AlternateScene>();
            builder.DefineModuleScope<AdditiveModuleA>();
            builder.For<TestSceneScope>().RegisterInstance<ISimpleSceneService>(new SimpleSceneService());
            builder.For<AlternateScene>().RegisterInstance<ISimpleSceneService>(new SimpleSceneService());
            builder.For<AdditiveModuleA>().RegisterInstance<IDisposableAdditiveService>(additiveService);
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadAdditiveModuleAsync<AdditiveModuleA>();

        Assert.False(additiveService.Disposed);

        await pipeline.LoadSceneAsync<AlternateScene>();

        Assert.True(additiveService.Disposed);
    }

    [Fact]
    public async Task LoadDuplicateAdditiveModule_Throws()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope<TestSessionScope>();
            builder.DefineSceneScope<TestSceneScope>();
            builder.DefineModuleScope<AdditiveModuleA>();
            builder.For<TestSceneScope>().RegisterInstance<ISimpleSceneService>(new SimpleSceneService());
            builder.For<AdditiveModuleA>().RegisterInstance<IAdditiveModuleServiceA>(new TrackingModuleService());
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadAdditiveModuleAsync<AdditiveModuleA>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.LoadAdditiveModuleAsync<AdditiveModuleA>());

        Assert.Contains("already loaded", ex.Message);
    }

    [Fact]
    public async Task RestartSession_CleansUpAdditiveModules()
    {
        var additiveService = new DisposableModuleService();

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope<TestSessionScope>();
            builder.DefineSceneScope<TestSceneScope>();
            builder.DefineModuleScope<AdditiveModuleA>();
            builder.For<TestSceneScope>().RegisterInstance<ISimpleSceneService>(new SimpleSceneService());
            builder.For<AdditiveModuleA>().RegisterInstance<IDisposableAdditiveService>(additiveService);
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadAdditiveModuleAsync<AdditiveModuleA>();

        Assert.False(additiveService.Disposed);

        await pipeline.RestartSessionAsync();

        Assert.True(additiveService.Disposed);
    }

    // Scope markers
    private sealed class PrimaryModule;
    private sealed class AdditiveModuleA;
    private sealed class AlternateScene;

    // Service contracts
    private interface IPrimaryModuleService : IModuleInitializableService;
    private interface IAdditiveModuleServiceA : IModuleInitializableService;
    private interface IDisposableAdditiveService : IModuleInitializableService, IModuleDisposableService;
    private interface ISimpleSceneService : ISceneInitializableService;

    // Service implementations
    private sealed class TrackingModuleService : IPrimaryModuleService, IAdditiveModuleServiceA, IAsyncInitializableService
    {
        public bool Initialized { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Initialized = true;
            return Task.CompletedTask;
        }
    }

    private sealed class DisposableModuleService : IDisposableAdditiveService, IAsyncInitializableService
    {
        public bool Initialized { get; private set; }
        public bool Disposed { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Initialized = true;
            return Task.CompletedTask;
        }

        public Task DisposeAsync(CancellationToken cancellationToken)
        {
            Disposed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class SimpleSceneService : ISimpleSceneService, IAsyncInitializableService
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
