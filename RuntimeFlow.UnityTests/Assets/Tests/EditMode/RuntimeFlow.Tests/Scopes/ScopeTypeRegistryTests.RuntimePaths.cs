using NUnit.Framework;
using System;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using VContainer;

namespace RuntimeFlow.Tests
{

public sealed partial class ScopeTypeRegistryTests
{
    [Test]
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

    [Test]
    public async Task LoadSceneAsync_WithoutDeclaration_ThrowsDeterministicDiagnostic()
    {
        var pipeline = RuntimePipeline.Create(_ => { });

        await pipeline.InitializeAsync();

        var exception = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.LoadSceneAsync<SceneScope>());

        Assert.That(exception.Message, Does.Contain("SceneScope"));
    }

    [Test]
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

        Assert.AreEqual(2, FluentSessionService.Attempts);
        Assert.AreEqual(2, FluentSceneService.Attempts);
        Assert.AreEqual(2, FluentModuleService.Attempts);
    }

    [Test]
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

        Assert.AreEqual(2, moduleService.Attempts);
        Assert.AreEqual("reload_module", pipeline.GetRuntimeStatus().CurrentOperationCode);
    }

    [Test]
    public async Task ReloadModuleAsync_WithoutSceneContext_ThrowsDeterministicPrecondition()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.Module(new ModuleScope(s => s
                .RegisterInstance<ITestModuleService>(
                    new AttemptControlledModuleService((_, _) => Task.CompletedTask))));
        });

        await pipeline.InitializeAsync();

        var exception = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.ReloadModuleAsync<ModuleScope>());
        Assert.AreEqual("Scene context is not initialized. Call LoadSceneAsync first.", exception.Message);
        Assert.AreEqual("reload_module", pipeline.GetRuntimeStatus().CurrentOperationCode);
    }

    [Test]
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

        Assert.AreEqual(2, sessionService.Attempts);
        Assert.AreEqual(2, selectedSceneService.Attempts);
        Assert.AreEqual(2, selectedModuleService.Attempts);
        Assert.AreEqual(0, alternateSceneService.Attempts);
        Assert.AreEqual(0, alternateModuleService.Attempts);
    }
}

}
