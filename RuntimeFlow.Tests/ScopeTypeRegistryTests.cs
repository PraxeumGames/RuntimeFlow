using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using VContainer;
using Xunit;

namespace RuntimeFlow.Tests;

public sealed class ScopeTypeRegistryTests
{
    [Fact]
    public void DefineScope_StoresTypeToContextTypeMapping()
    {
        var builder = new GameContextBuilder();

        builder.DefineGlobalScope();
        builder.DefineSessionScope();
        builder.Scene<SceneScope>();
        builder.Module<ModuleScope>();

        AssertScopeMapping(builder, typeof(GlobalScope), GameContextType.Global);
        AssertScopeMapping(builder, typeof(SessionScope), GameContextType.Session);
        AssertScopeMapping(builder, typeof(SceneScope), GameContextType.Scene);
        AssertScopeMapping(builder, typeof(ModuleScope), GameContextType.Module);
    }

    [Fact]
    public void DefineScope_DuplicateDeclaration_ThrowsDeterministicDiagnostic()
    {
        var builder = new GameContextBuilder();
        builder.Scene<SceneScope>();

        var exception = Assert.Throws<ScopeRegistrationException>(() => builder.Scene<SceneScope>());

        Assert.Equal("GBSR3001", exception.DiagnosticCode);
    }

    [Fact]
    public void DefineScope_ConflictingDeclaration_ThrowsDeterministicDiagnostic()
    {
        var builder = new GameContextBuilder();
        builder.Scene<SharedScope>();

        var exception = Assert.Throws<ScopeRegistrationException>(() => builder.Module<SharedScope>());

        Assert.Equal("GBSR3002", exception.DiagnosticCode);
    }

    [Fact]
    public async Task LoadSceneAsync_WithoutDeclaration_ThrowsDeterministicDiagnostic()
    {
        var pipeline = RuntimePipeline.Create(_ => { });

        await pipeline.InitializeAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.LoadSceneAsync<SceneScope>());

        Assert.Contains("SceneScope", exception.Message);
    }

    [Fact]
    public async Task ForScope_RegisterFluentChain_DeclaredSceneScope_RoutesToSceneProfile()
    {
        FluentSceneService.Reset();

        var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.Scene(new SceneScope(s => s
                    .Register<FluentSceneService>(Lifetime.Singleton)
                    .As<ITestSceneService>()
                    .AsSelf()));
            })
            .ConfigureFlow(new DelegateRuntimeFlowScenario(async (context, cancellationToken) =>
            {
                await context.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await context.LoadScopeSceneAsync<SceneScope>(cancellationToken).ConfigureAwait(false);
            }));

        await pipeline.RunAsync(NoopSceneLoader.Instance);

        Assert.Equal(1, FluentSceneService.Attempts);
    }

    [Fact]
    public async Task ForScope_RegisterByTypeFluentChain_DeclaredSceneScope_RoutesToSceneProfile()
    {
        FluentSceneService.Reset();

        var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.Scene(new SceneScope(s => s
                    .Register(typeof(FluentSceneService), Lifetime.Singleton)
                    .As(typeof(ITestSceneService))
                    .AsSelf()));
            })
            .ConfigureFlow(new DelegateRuntimeFlowScenario(async (context, cancellationToken) =>
            {
                await context.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await context.LoadScopeSceneAsync<SceneScope>(cancellationToken).ConfigureAwait(false);
            }));

        await pipeline.RunAsync(NoopSceneLoader.Instance);

        Assert.Equal(1, FluentSceneService.Attempts);
    }

    [Fact]
    public async Task ForScope_RegisterInstanceFluent_DeclaredSceneScope_RoutesToSceneProfile()
    {
        var sceneService = new AttemptControlledSceneService((_, _) => Task.CompletedTask);

        var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.Scene(new SceneScope(s => s
                    .RegisterInstance<ITestSceneService>(sceneService)));
            })
            .ConfigureFlow(new DelegateRuntimeFlowScenario(async (context, cancellationToken) =>
            {
                await context.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await context.LoadScopeSceneAsync<SceneScope>(cancellationToken).ConfigureAwait(false);
            }));

        await pipeline.RunAsync(NoopSceneLoader.Instance);

        Assert.Equal(1, sceneService.Attempts);
    }

    [Fact]
    public async Task ForScope_FluentRegistrations_AreAppliedAcrossInitializeLoadAndRestartRuntimePaths()
    {
        FluentSessionService.Reset();
        FluentSceneService.Reset();
        FluentModuleService.Reset();

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();

            builder.Session()
                .Register<FluentSessionService>(Lifetime.Singleton)
                .As<ITestSessionService>()
                .AsSelf();

            builder.Scene(new SceneScope(s => s
                .Register<FluentSceneService>(Lifetime.Singleton)
                .As<ITestSceneService>()
                .AsSelf()));

            builder.Module(new ModuleScope(s => s
                .Register<FluentModuleService>(Lifetime.Singleton)
                .As<ITestModuleService>()
                .AsSelf()));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<SceneScope>();
        await pipeline.LoadModuleAsync<ModuleScope>();
        await pipeline.RestartSessionAsync();

        Assert.Equal(2, FluentSessionService.Attempts);
        Assert.Equal(2, FluentSceneService.Attempts);
        Assert.Equal(2, FluentModuleService.Attempts);
    }

    [Fact]
    public async Task ReloadModuleAsync_CurrentModuleScope_ReloadsModuleAndUpdatesStatusCode()
    {
        var moduleService = new AttemptControlledModuleService((_, _) => Task.CompletedTask);

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.Scene(new SceneScope(s => s
                .RegisterInstance<ITestSceneService>(
                    new AttemptControlledSceneService((_, _) => Task.CompletedTask))));

            builder.Module(new ModuleScope(s => s
                .RegisterInstance<ITestModuleService>(moduleService)));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<SceneScope>();
        await pipeline.LoadModuleAsync<ModuleScope>();
        await pipeline.ReloadModuleAsync<ModuleScope>();

        Assert.Equal(2, moduleService.Attempts);
        Assert.Equal("reload_module", pipeline.GetRuntimeStatus().CurrentOperationCode);
    }

    [Fact]
    public async Task ReloadModuleAsync_WithoutSceneContext_ThrowsDeterministicPrecondition()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.Module(new ModuleScope(s => s
                .RegisterInstance<ITestModuleService>(
                    new AttemptControlledModuleService((_, _) => Task.CompletedTask))));
        });

        await pipeline.InitializeAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.ReloadModuleAsync<ModuleScope>());
        Assert.Equal("Scene context is not initialized. Call LoadSceneAsync first.", exception.Message);
        Assert.Equal("reload_module", pipeline.GetRuntimeStatus().CurrentOperationCode);
    }

    [Fact]
    public async Task ForScope_RunFlow_RestartKeepsActiveTypedSceneAndModuleProfiles()
    {
        var sessionService = new AttemptControlledSessionService((_, _) => Task.CompletedTask);
        var selectedSceneService = new AttemptControlledSceneService((_, _) => Task.CompletedTask);
        var alternateSceneService = new AttemptControlledSceneService((_, _) => Task.CompletedTask);
        var selectedModuleService = new AttemptControlledModuleService((_, _) => Task.CompletedTask);
        var alternateModuleService = new AttemptControlledModuleService((_, _) => Task.CompletedTask);

        var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();

                builder.Session().RegisterInstance<ITestSessionService>(sessionService);

                builder.Scene(new SceneScope(s => s
                    .RegisterInstance<ITestSceneService>(selectedSceneService)));
                builder.Scene(new SecondarySceneScope(s => s
                    .RegisterInstance<ITestSceneService>(alternateSceneService)));

                builder.Module(new ModuleScope(s => s
                    .RegisterInstance<ITestModuleService>(selectedModuleService)));
                builder.Module(new SecondaryModuleScope(s => s
                    .RegisterInstance<ITestModuleService>(alternateModuleService)));
            })
            .ConfigureFlow(new DelegateRuntimeFlowScenario(async (context, cancellationToken) =>
            {
                await context.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await context.GoToAsync(
                        SceneRoute.ToScene<SceneScope>("Gameplay").WithModule<ModuleScope>(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }));

        await pipeline.RunAsync(NoopSceneLoader.Instance);
        await pipeline.RestartSessionAsync();

        Assert.Equal(2, sessionService.Attempts);
        Assert.Equal(2, selectedSceneService.Attempts);
        Assert.Equal(2, selectedModuleService.Attempts);
        Assert.Equal(0, alternateSceneService.Attempts);
        Assert.Equal(0, alternateModuleService.Attempts);
    }

    [Fact]
    public void ForScope_RegisterOpenGenericFluent_ThrowsDeterministicDiagnostic()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            RuntimePipeline.Create(builder =>
            {
                builder.Scene(new SceneScope(s => s
                    .Register(typeof(OpenGenericFluentSceneService<>), Lifetime.Singleton)));
            }));

        Assert.Contains("RFRC2003", exception.Message);
    }

    private static void AssertScopeMapping(GameContextBuilder builder, Type scopeType, GameContextType expectedScope)
    {
        var found = builder.TryResolveScopeType(scopeType, out var actualScope);
        Assert.True(found);
        Assert.Equal(expectedScope, actualScope);
    }

    private sealed class FluentSceneService : ITestSceneService
    {
        private static int _attempts;

        public static int Attempts => Volatile.Read(ref _attempts);

        int ITestSceneService.Attempts => Volatile.Read(ref _attempts);

        public static void Reset()
        {
            Interlocked.Exchange(ref _attempts, 0);
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            return Task.CompletedTask;
        }
    }

    private sealed class FluentSessionService : ITestSessionService
    {
        private static int _attempts;

        public static int Attempts => Volatile.Read(ref _attempts);

        int ITestSessionService.Attempts => Volatile.Read(ref _attempts);

        public static void Reset()
        {
            Interlocked.Exchange(ref _attempts, 0);
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            return Task.CompletedTask;
        }
    }

    private sealed class FluentModuleService : ITestModuleService
    {
        private static int _attempts;

        public static int Attempts => Volatile.Read(ref _attempts);

        int ITestModuleService.Attempts => Volatile.Read(ref _attempts);

        public static void Reset()
        {
            Interlocked.Exchange(ref _attempts, 0);
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            return Task.CompletedTask;
        }
    }

    private sealed class AttemptControlledModuleService : ITestModuleService
    {
        private readonly Func<int, CancellationToken, Task> _behavior;
        private int _attempts;

        public AttemptControlledModuleService(Func<int, CancellationToken, Task> behavior)
        {
            _behavior = behavior ?? throw new ArgumentNullException(nameof(behavior));
        }

        public int Attempts => Volatile.Read(ref _attempts);

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            return _behavior(attempt, cancellationToken);
        }
    }

    private interface ITestModuleService : IModuleInitializableService
    {
        int Attempts { get; }
    }

    private sealed class OpenGenericFluentSceneService<T> where T : class { }

    private sealed class SceneScope : ISceneScope
    {
        private readonly Action<IGameScopeRegistrationBuilder>? _configure;

        public SceneScope() { }

        public SceneScope(Action<IGameScopeRegistrationBuilder> configure)
            => _configure = configure;

        public void Configure(IGameScopeRegistrationBuilder builder)
            => _configure?.Invoke(builder);
    }

    private sealed class SecondarySceneScope : ISceneScope
    {
        private readonly Action<IGameScopeRegistrationBuilder>? _configure;

        public SecondarySceneScope() { }

        public SecondarySceneScope(Action<IGameScopeRegistrationBuilder> configure)
            => _configure = configure;

        public void Configure(IGameScopeRegistrationBuilder builder)
            => _configure?.Invoke(builder);
    }

    private sealed class ModuleScope : IModuleScope
    {
        private readonly Action<IGameScopeRegistrationBuilder>? _configure;

        public ModuleScope() { }

        public ModuleScope(Action<IGameScopeRegistrationBuilder> configure)
            => _configure = configure;

        public void Configure(IGameScopeRegistrationBuilder builder)
            => _configure?.Invoke(builder);
    }

    private sealed class SecondaryModuleScope : IModuleScope
    {
        private readonly Action<IGameScopeRegistrationBuilder>? _configure;

        public SecondaryModuleScope() { }

        public SecondaryModuleScope(Action<IGameScopeRegistrationBuilder> configure)
            => _configure = configure;

        public void Configure(IGameScopeRegistrationBuilder builder)
            => _configure?.Invoke(builder);
    }

    private sealed class SharedScope : ISceneScope, IModuleScope
    {
        public void Configure(IGameScopeRegistrationBuilder builder) { }
    }
}
