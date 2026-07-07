using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{

public sealed class AdditiveModuleTests
{
    [Test]
    public async Task LoadAdditiveModule_CreatesSecondModule()
    {
        var primaryService = new TrackingModuleService();
        var additiveService = new TrackingModuleService();

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new TestSceneScope(s => s.RegisterInstance<ISimpleSceneService>(new SimpleSceneService())));
            builder.Module(new PrimaryModule(m => m.RegisterInstance<IPrimaryModuleService>(primaryService)));
            builder.Module(new AdditiveModuleA(m => m.RegisterInstance<IAdditiveModuleServiceA>(additiveService)));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadModuleAsync<PrimaryModule>();
        await pipeline.LoadAdditiveModuleAsync<AdditiveModuleA>();

        Assert.AreEqual(ScopeLifecycleState.Active, pipeline.GetScopeState<PrimaryModule>());
        Assert.AreEqual(ScopeLifecycleState.Active, pipeline.GetScopeState<AdditiveModuleA>());
        Assert.IsTrue(primaryService.Initialized);
        Assert.IsTrue(additiveService.Initialized);
    }

    [Test]
    public async Task UnloadAdditiveModule_DisposesCorrectly()
    {
        var additiveService = new DisposableModuleService();

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new TestSceneScope(s => s.RegisterInstance<ISimpleSceneService>(new SimpleSceneService())));
            builder.Module(new AdditiveModuleA(m => m.RegisterInstance<IDisposableAdditiveService>(additiveService)));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadAdditiveModuleAsync<AdditiveModuleA>();

        Assert.IsTrue(additiveService.Initialized);
        Assert.IsFalse(additiveService.Disposed);

        await pipeline.UnloadAdditiveModuleAsync<AdditiveModuleA>();

        Assert.IsTrue(additiveService.Disposed);
        Assert.AreEqual(ScopeLifecycleState.Disposed, pipeline.GetScopeState<AdditiveModuleA>());
    }

    [Test]
    public async Task LoadScene_CleansUpAdditiveModules()
    {
        var additiveService = new DisposableModuleService();

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new TestSceneScope(s => s.RegisterInstance<ISimpleSceneService>(new SimpleSceneService())));
            builder.Scene(new AlternateScene(s => s.RegisterInstance<ISimpleSceneService>(new SimpleSceneService())));
            builder.Module(new AdditiveModuleA(m => m.RegisterInstance<IDisposableAdditiveService>(additiveService)));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadAdditiveModuleAsync<AdditiveModuleA>();

        Assert.IsFalse(additiveService.Disposed);

        await pipeline.LoadSceneAsync<AlternateScene>();

        Assert.IsTrue(additiveService.Disposed);
    }

    [Test]
    public async Task LoadDuplicateAdditiveModule_Throws()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new TestSceneScope(s => s.RegisterInstance<ISimpleSceneService>(new SimpleSceneService())));
            builder.Module(new AdditiveModuleA(m => m.RegisterInstance<IAdditiveModuleServiceA>(new TrackingModuleService())));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadAdditiveModuleAsync<AdditiveModuleA>();

        var ex = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.LoadAdditiveModuleAsync<AdditiveModuleA>());

        Assert.That(ex.Message, Does.Contain("already loaded"));
    }

    [Test]
    public async Task ConcurrentDuplicateAdditiveModuleLoad_SerializesAndThrowsWithoutSecondInitialization()
    {
        var additiveService = new BlockingAdditiveModuleService();

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new TestSceneScope(s => s.RegisterInstance<ISimpleSceneService>(new SimpleSceneService())));
            builder.Module(new AdditiveModuleA(m => m.RegisterInstance<IBlockingAdditiveService>(additiveService)));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();

        var firstLoad = pipeline.LoadAdditiveModuleAsync<AdditiveModuleA>();
        await additiveService.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var secondLoad = pipeline.LoadAdditiveModuleAsync<AdditiveModuleA>();

        try
        {
            await Task.Delay(50);
            Assert.IsFalse(secondLoad.IsCompleted, "Second additive load should wait for the first side operation.");

            additiveService.Release();
            await firstLoad;

            var ex = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(async () => await secondLoad);

            Assert.That(ex.Message, Does.Contain("already loaded"));
            Assert.AreEqual(1, additiveService.InitializeCount);
            Assert.AreEqual(ScopeLifecycleState.Active, pipeline.GetScopeState<AdditiveModuleA>());
        }
        finally
        {
            additiveService.Release();
        }
    }

    [Test]
    public async Task RestartSession_CleansUpAdditiveModules()
    {
        var additiveService = new DisposableModuleService();

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new TestSceneScope(s => s.RegisterInstance<ISimpleSceneService>(new SimpleSceneService())));
            builder.Module(new AdditiveModuleA(m => m.RegisterInstance<IDisposableAdditiveService>(additiveService)));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadAdditiveModuleAsync<AdditiveModuleA>();

        Assert.IsFalse(additiveService.Disposed);

        await pipeline.RestartSessionAsync();

        Assert.IsTrue(additiveService.Disposed);
    }

    // Scope markers
    private sealed class PrimaryModule : IModuleScope
    {
        private readonly Action<IGameScopeRegistrationBuilder>? _configure;
        public PrimaryModule() { }
        public PrimaryModule(Action<IGameScopeRegistrationBuilder> configure) => _configure = configure;
        public void Configure(IGameScopeRegistrationBuilder builder) => _configure?.Invoke(builder);
    }
    private sealed class AdditiveModuleA : IModuleScope
    {
        private readonly Action<IGameScopeRegistrationBuilder>? _configure;
        public AdditiveModuleA() { }
        public AdditiveModuleA(Action<IGameScopeRegistrationBuilder> configure) => _configure = configure;
        public void Configure(IGameScopeRegistrationBuilder builder) => _configure?.Invoke(builder);
    }
    private sealed class AlternateScene : ISceneScope
    {
        private readonly Action<IGameScopeRegistrationBuilder>? _configure;
        public AlternateScene() { }
        public AlternateScene(Action<IGameScopeRegistrationBuilder> configure) => _configure = configure;
        public void Configure(IGameScopeRegistrationBuilder builder) => _configure?.Invoke(builder);
    }

    // Service contracts
    private interface IPrimaryModuleService : IModuleInitializableService { }    private interface IAdditiveModuleServiceA : IModuleInitializableService { }    private interface IDisposableAdditiveService : IModuleInitializableService, IModuleDisposableService { }    private interface ISimpleSceneService : ISceneInitializableService { }
    private interface IBlockingAdditiveService : IModuleInitializableService { }
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

    private sealed class BlockingAdditiveModuleService : IBlockingAdditiveService, IAsyncInitializableService
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _initializeCount;

        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int InitializeCount => Volatile.Read(ref _initializeCount);

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _initializeCount);
            Started.TrySetResult(true);

            var cancellationTask = Task.Delay(Timeout.Infinite, cancellationToken);
            var completed = await Task.WhenAny(_release.Task, cancellationTask).ConfigureAwait(false);
            if (completed != _release.Task)
                cancellationToken.ThrowIfCancellationRequested();

            await _release.Task.ConfigureAwait(false);
        }

        public void Release()
        {
            _release.TrySetResult(true);
        }
    }
}

}
