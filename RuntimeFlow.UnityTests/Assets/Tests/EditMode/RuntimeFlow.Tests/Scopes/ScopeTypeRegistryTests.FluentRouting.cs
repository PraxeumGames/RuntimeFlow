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

        Assert.AreEqual(1, FluentSceneService.Attempts);
    }

    [Test]
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

        Assert.AreEqual(1, FluentSceneService.Attempts);
    }

    [Test]
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

        Assert.AreEqual(1, sceneService.Attempts);
    }

    [Test]
    public void ForScope_RegisterOpenGenericFluent_ThrowsDeterministicDiagnostic()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            RuntimePipeline.Create(builder =>
            {
                builder.Scene(new SceneScope(s => s
                    .Register(typeof(OpenGenericFluentSceneService<>), Lifetime.Singleton)));
            }));

        Assert.That(exception.Message, Does.Contain("RFRC2003"));
    }
}

}
