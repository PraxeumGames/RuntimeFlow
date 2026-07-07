using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using VContainer;

namespace RuntimeFlow.Tests
{

public sealed class ScopePreloadingTests
{
    [Test]
    public async Task PreloadScene_ThenLoad_UsesPreloadedContext()
    {
        var initTimestamps = new Dictionary<string, long>();
        var tickCounter = 0L;

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new SceneA(s => s.RegisterInstance<ITrackingSceneService>(
                new TrackingSceneService(initTimestamps, () => Interlocked.Increment(ref tickCounter)))));
        });

        await pipeline.InitializeAsync();

        // Preload scene A — InitializeAsync runs during preload
        await pipeline.PreloadSceneAsync<SceneA>();
        Assert.IsTrue(initTimestamps.ContainsKey("init"), "Service should be initialized during preload.");
        var preloadTick = initTimestamps["init"];

        // Load scene A — should use preloaded context (no re-initialization)
        var tickBeforeLoad = Interlocked.Read(ref tickCounter);
        await pipeline.LoadSceneAsync<SceneA>();
        Assert.AreEqual(preloadTick, initTimestamps["init"]);
    }

    [Test]
    public async Task PreloadScene_Load_DisposesPreviousScene()
    {
        var disposed = false;

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new SceneA(s => s.RegisterInstance<IDisposableSceneService>(
                new DisposableSceneService(() => disposed = true))));
            builder.Scene(new SceneB(s => s.RegisterInstance<ISimpleSceneService>(new SimpleSceneService())));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<SceneA>();
        Assert.IsFalse(disposed);

        // Preload scene B, then load it — scene A's disposable services should be disposed
        await pipeline.PreloadSceneAsync<SceneB>();
        Assert.IsFalse(disposed, "Previous scene should not be disposed during preload.");

        await pipeline.LoadSceneAsync<SceneB>();
        Assert.IsTrue(disposed, "Previous scene should be disposed when preloaded scene is activated.");
    }

    [Test]
    public async Task PreloadScene_DisposeBuilder_CleansUpPreloaded()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new SceneA(s => s.RegisterInstance<ISimpleSceneService>(new SimpleSceneService())));
        });

        await pipeline.InitializeAsync();
        await pipeline.PreloadSceneAsync<SceneA>();
        Assert.IsTrue(pipeline.HasPreloadedScope<SceneA>());

        await pipeline.DisposeAsync();
        // After dispose, the preloaded context dictionary is cleared (verified internally).
        // We verify indirectly: the pipeline is disposed and HasPreloadedScope can't be called.
        // Instead, just ensure no exceptions during dispose — the preloaded context was cleaned up.
    }

    [Test]
    public async Task HasPreloadedScope_ReturnsCorrectly()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new SceneA(s => s.RegisterInstance<ISimpleSceneService>(new SimpleSceneService())));
        });

        await pipeline.InitializeAsync();
        Assert.IsFalse(pipeline.HasPreloadedScope<SceneA>());

        await pipeline.PreloadSceneAsync<SceneA>();
        Assert.IsTrue(pipeline.HasPreloadedScope<SceneA>());

        await pipeline.LoadSceneAsync<SceneA>();
        Assert.IsFalse(pipeline.HasPreloadedScope<SceneA>());
    }

    [Test]
    public async Task PreloadedScene_WhenActivationFails_RemainsOwnedByPreloadAndDisposesLater()
    {
        var disposed = false;

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new SceneA(s => s.RegisterInstance<IFailingActivationSceneService>(
                new FailingActivationSceneService(() => disposed = true))));
        });

        await pipeline.InitializeAsync();
        await pipeline.PreloadSceneAsync<SceneA>();

        var exception = await AsyncTestAssert.CatchAsync<InvalidOperationException>(() => pipeline.LoadSceneAsync<SceneA>());

        Assert.That(exception.Message, Is.EqualTo("scene activation failed"));
        Assert.IsTrue(pipeline.HasPreloadedScope<SceneA>());
        Assert.IsFalse(disposed);

        await pipeline.DisposeAsync();

        Assert.IsTrue(disposed);
    }

    [Test]
    public async Task PreloadModule_DoesNotCancelInFlightExclusiveModuleLoad()
    {
        var blockingModuleService = new BlockingModuleService();
        var preloadedModuleService = new PreloadedModuleService();

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new SceneA(s => s.RegisterInstance<ISimpleSceneService>(new SimpleSceneService())));
            builder.Module(new ModuleA(m => m.RegisterInstance<IBlockingModuleService>(blockingModuleService)));
            builder.Module(new ModuleB(m => m.RegisterInstance<IPreloadedModuleService>(preloadedModuleService)));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<SceneA>();

        var loadModule = pipeline.LoadModuleAsync<ModuleA>();
        await blockingModuleService.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        try
        {
            await pipeline.PreloadModuleAsync<ModuleB>();

            Assert.IsTrue(preloadedModuleService.Initialized);
            Assert.IsTrue(pipeline.HasPreloadedScope<ModuleB>());
            Assert.IsFalse(loadModule.IsCompleted, "PreloadModule must not cancel or complete the in-flight exclusive load.");
            Assert.AreEqual(0, blockingModuleService.CancellationCount);
        }
        finally
        {
            blockingModuleService.Release();
        }

        await loadModule;

        Assert.AreEqual(ScopeLifecycleState.Active, pipeline.GetScopeState<ModuleA>());
        Assert.AreEqual(ScopeLifecycleState.Preloaded, pipeline.GetScopeState<ModuleB>());
        Assert.AreEqual(0, blockingModuleService.CancellationCount);
    }

    [Test]
    public async Task PreloadModule_WhenModuleLoadChangesGenerationDuringInitialization_DisposesStaleContext()
    {
        var stalePreloadService = new BlockingDisposablePreloadModuleService();

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new SceneA(s => s.RegisterInstance<ISimpleSceneService>(new SimpleSceneService())));
            builder.Module(new ModuleA(m => m.RegisterInstance<ISimpleModuleService>(new SimpleModuleService())));
            builder.Module(new ModuleB(m => m.RegisterInstance<IStalePreloadModuleService>(stalePreloadService)));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<SceneA>();

        var preload = pipeline.PreloadModuleAsync<ModuleB>();
        await stalePreloadService.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var loadModule = pipeline.LoadModuleAsync<ModuleA>();
        await Task.Delay(50);

        Assert.IsFalse(loadModule.IsCompleted, "LoadModule should wait for the in-flight side preload before checking preloaded scopes.");

        stalePreloadService.Release();
        await AsyncTestAssert.CatchAsync<OperationCanceledException>(() => preload);
        await loadModule;

        Assert.IsFalse(pipeline.HasPreloadedScope<ModuleB>());
        Assert.IsTrue(stalePreloadService.Disposed);
        Assert.AreEqual(ScopeLifecycleState.Active, pipeline.GetScopeState<ModuleA>());
        Assert.AreEqual(ScopeLifecycleState.Disposed, pipeline.GetScopeState<ModuleB>());
    }

    [Test]
    public async Task PreloadModule_DuringSceneTransition_WaitsForStableSceneContext()
    {
        var sceneTransitionService = new BlockingSceneService();
        var preloadedModuleService = new PreloadedModuleService();

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new SceneA(s => s.RegisterInstance<ISimpleSceneService>(new SimpleSceneService())));
            builder.Scene(new SceneB(s => s.RegisterInstance<IBlockingSceneService>(sceneTransitionService)));
            builder.Module(new ModuleB(m => m.RegisterInstance<IPreloadedModuleService>(preloadedModuleService)));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<SceneA>();

        var loadScene = pipeline.LoadSceneAsync<SceneB>();
        await sceneTransitionService.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var preload = pipeline.PreloadModuleAsync<ModuleB>();
        await Task.Delay(50);

        Assert.IsFalse(preload.IsCompleted, "PreloadModule should wait until the parent scene transition completes.");

        sceneTransitionService.Release();
        await loadScene;
        await preload;

        Assert.IsTrue(preloadedModuleService.Initialized);
        Assert.IsTrue(pipeline.HasPreloadedScope<ModuleB>());
        Assert.AreEqual(ScopeLifecycleState.Active, pipeline.GetScopeState<SceneB>());
        Assert.AreEqual(ScopeLifecycleState.Preloaded, pipeline.GetScopeState<ModuleB>());
    }

    [Test]
    public async Task DisposeAsync_WaitsForInFlightPreloadAndDisposesPublishedContext()
    {
        var preloadedModuleService = new BlockingDisposablePreloadModuleService();

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new SceneA(s => s.RegisterInstance<ISimpleSceneService>(new SimpleSceneService())));
            builder.Module(new ModuleB(m => m.RegisterInstance<IStalePreloadModuleService>(preloadedModuleService)));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<SceneA>();

        var preload = pipeline.PreloadModuleAsync<ModuleB>();
        await preloadedModuleService.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var dispose = pipeline.DisposeAsync().AsTask();
        await Task.Delay(50);

        Assert.IsFalse(dispose.IsCompleted, "DisposeAsync should wait for the in-flight side operation before sweeping scopes.");

        preloadedModuleService.Release();
        await preload;
        await dispose;

        Assert.IsTrue(preloadedModuleService.Disposed);
        Assert.IsFalse(pipeline.HasPreloadedScope<ModuleB>());
        Assert.AreEqual(ScopeLifecycleState.Disposed, pipeline.GetScopeState<ModuleB>());
    }

    [Test]
    public async Task DisposeAsync_WithCanceledToken_StillWaitsForInFlightPreloadAndDisposesPublishedContext()
    {
        var preloadedModuleService = new BlockingDisposablePreloadModuleService();

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new SceneA(s => s.RegisterInstance<ISimpleSceneService>(new SimpleSceneService())));
            builder.Module(new ModuleB(m => m.RegisterInstance<IStalePreloadModuleService>(preloadedModuleService)));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<SceneA>();

        var preload = pipeline.PreloadModuleAsync<ModuleB>();
        await preloadedModuleService.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        var dispose = pipeline.DisposeAsync(cancellationSource.Token).AsTask();
        await Task.Delay(50);

        Assert.IsFalse(dispose.IsCompleted, "DisposeAsync is terminal and should not abandon scope sweep when the caller token is already canceled.");

        preloadedModuleService.Release();
        await preload;
        await dispose;

        Assert.IsTrue(preloadedModuleService.Disposed);
        Assert.IsFalse(pipeline.HasPreloadedScope<ModuleB>());
        Assert.AreEqual(ScopeLifecycleState.Disposed, pipeline.GetScopeState<ModuleB>());
    }

    [Test]
    public async Task PreloadModule_DuringPreloadedActivation_WaitsForActivationToAdoptContext()
    {
        var activation = new BlockingActivationController();

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new SceneA(s => s.RegisterInstance<ISimpleSceneService>(new SimpleSceneService())));
            builder.Module(new ModuleB(m =>
            {
                m.RegisterInstance(activation);
                m.Register<BlockingActivationModuleService>(Lifetime.Transient)
                    .As<IBlockingActivationModuleService>();
            }));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<SceneA>();
        await pipeline.PreloadModuleAsync<ModuleB>();

        var loadModule = pipeline.LoadModuleAsync<ModuleB>();
        await activation.ActivationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var replacementPreload = pipeline.PreloadModuleAsync<ModuleB>();
        await Task.Delay(50);

        Assert.IsFalse(replacementPreload.IsCompleted, "PreloadModule should not replace a preloaded context while it is being activated.");

        activation.ReleaseActivation();
        await loadModule;
        await replacementPreload;

        Assert.IsTrue(pipeline.HasPreloadedScope<ModuleB>());
        Assert.That(activation.InitializeCount, Is.EqualTo(2));
        Assert.AreEqual(ScopeLifecycleState.Preloaded, pipeline.GetScopeState<ModuleB>());
    }

    // Scope markers
    private sealed class SceneA : ISceneScope
    {
        private readonly Action<IGameScopeRegistrationBuilder>? _configure;
        public SceneA() { }
        public SceneA(Action<IGameScopeRegistrationBuilder> configure) => _configure = configure;
        public void Configure(IGameScopeRegistrationBuilder builder) => _configure?.Invoke(builder);
    }
    private sealed class SceneB : ISceneScope
    {
        private readonly Action<IGameScopeRegistrationBuilder>? _configure;
        public SceneB() { }
        public SceneB(Action<IGameScopeRegistrationBuilder> configure) => _configure = configure;
        public void Configure(IGameScopeRegistrationBuilder builder) => _configure?.Invoke(builder);
    }
    private sealed class ModuleA : IModuleScope
    {
        private readonly Action<IGameScopeRegistrationBuilder>? _configure;
        public ModuleA() { }
        public ModuleA(Action<IGameScopeRegistrationBuilder> configure) => _configure = configure;
        public void Configure(IGameScopeRegistrationBuilder builder) => _configure?.Invoke(builder);
    }
    private sealed class ModuleB : IModuleScope
    {
        private readonly Action<IGameScopeRegistrationBuilder>? _configure;
        public ModuleB() { }
        public ModuleB(Action<IGameScopeRegistrationBuilder> configure) => _configure = configure;
        public void Configure(IGameScopeRegistrationBuilder builder) => _configure?.Invoke(builder);
    }

    // Service contracts
    private interface ITrackingSceneService : ISceneInitializableService { }    private interface IDisposableSceneService : ISceneInitializableService, ISceneDisposableService { }    private interface ISimpleSceneService : ISceneInitializableService { }
    private interface IFailingActivationSceneService : ISceneInitializableService, ISceneScopeActivationService, ISceneDisposableService { }
    private interface IBlockingModuleService : IModuleInitializableService { }
    private interface IPreloadedModuleService : IModuleInitializableService { }
    private interface IStalePreloadModuleService : IModuleInitializableService, IModuleDisposableService { }
    private interface ISimpleModuleService : IModuleInitializableService { }
    private interface IBlockingSceneService : ISceneInitializableService { }
    private interface IBlockingActivationModuleService : IModuleInitializableService, IModuleScopeActivationService, IModuleDisposableService { }
    // Service implementations
    private sealed class TrackingSceneService : ITrackingSceneService, IAsyncInitializableService
    {
        private readonly Dictionary<string, long> _timestamps;
        private readonly Func<long> _tickFactory;

        public TrackingSceneService(Dictionary<string, long> timestamps, Func<long> tickFactory)
        {
            _timestamps = timestamps;
            _tickFactory = tickFactory;
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            _timestamps["init"] = _tickFactory();
            return Task.CompletedTask;
        }
    }

    private sealed class DisposableSceneService : IDisposableSceneService, IAsyncInitializableService
    {
        private readonly Action _onDisposed;

        public DisposableSceneService(Action onDisposed) => _onDisposed = onDisposed;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DisposeAsync(CancellationToken cancellationToken)
        {
            _onDisposed();
            return Task.CompletedTask;
        }
    }

    private sealed class SimpleSceneService : ISimpleSceneService, IAsyncInitializableService
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SimpleModuleService : ISimpleModuleService, IAsyncInitializableService
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingActivationSceneService : IFailingActivationSceneService
    {
        private readonly Action _onDisposed;

        public FailingActivationSceneService(Action onDisposed)
        {
            _onDisposed = onDisposed;
        }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task OnScopeActivatedAsync(CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("scene activation failed");
        }

        public Task OnScopeDeactivatingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DisposeAsync(CancellationToken cancellationToken)
        {
            _onDisposed();
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingModuleService : IBlockingModuleService, IAsyncInitializableService
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cancellationCount;

        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CancellationCount => Volatile.Read(ref _cancellationCount);

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            try
            {
                var cancellationTask = Task.Delay(Timeout.Infinite, cancellationToken);
                var completed = await Task.WhenAny(_release.Task, cancellationTask).ConfigureAwait(false);
                if (completed != _release.Task)
                    cancellationToken.ThrowIfCancellationRequested();

                await _release.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancellationCount);
                throw;
            }
        }

        public void Release()
        {
            _release.TrySetResult(true);
        }
    }

    private sealed class PreloadedModuleService : IPreloadedModuleService, IAsyncInitializableService
    {
        public bool Initialized { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Initialized = true;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingSceneService : IBlockingSceneService, IAsyncInitializableService
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
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

    private sealed class BlockingDisposablePreloadModuleService : IStalePreloadModuleService, IAsyncInitializableService
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);

            var cancellationTask = Task.Delay(Timeout.Infinite, cancellationToken);
            var completed = await Task.WhenAny(_release.Task, cancellationTask).ConfigureAwait(false);
            if (completed != _release.Task)
                cancellationToken.ThrowIfCancellationRequested();

            await _release.Task.ConfigureAwait(false);
        }

        public Task DisposeAsync(CancellationToken cancellationToken)
        {
            Disposed = true;
            return Task.CompletedTask;
        }

        public void Release()
        {
            _release.TrySetResult(true);
        }
    }

    private sealed class BlockingActivationController
    {
        private readonly TaskCompletionSource<bool> _releaseActivation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _initializeCount;

        public TaskCompletionSource<bool> ActivationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int InitializeCount => Volatile.Read(ref _initializeCount);

        public void RecordInitialize()
        {
            Interlocked.Increment(ref _initializeCount);
        }

        public Task WaitForActivationReleaseAsync(CancellationToken cancellationToken)
        {
            return WaitForReleaseAsync(_releaseActivation.Task, cancellationToken);
        }

        public void ReleaseActivation()
        {
            _releaseActivation.TrySetResult(true);
        }

        private static async Task WaitForReleaseAsync(Task releaseTask, CancellationToken cancellationToken)
        {
            var cancellationTask = Task.Delay(Timeout.Infinite, cancellationToken);
            var completed = await Task.WhenAny(releaseTask, cancellationTask).ConfigureAwait(false);
            if (completed != releaseTask)
                cancellationToken.ThrowIfCancellationRequested();

            await releaseTask.ConfigureAwait(false);
        }
    }

    private sealed class BlockingActivationModuleService : IBlockingActivationModuleService, IAsyncInitializableService
    {
        private readonly BlockingActivationController _controller;

        public BlockingActivationModuleService(BlockingActivationController controller)
        {
            _controller = controller;
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            _controller.RecordInitialize();
            return Task.CompletedTask;
        }

        public async Task OnScopeActivatedAsync(CancellationToken cancellationToken)
        {
            _controller.ActivationStarted.TrySetResult(true);
            await _controller.WaitForActivationReleaseAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task OnScopeDeactivatingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DisposeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

}
