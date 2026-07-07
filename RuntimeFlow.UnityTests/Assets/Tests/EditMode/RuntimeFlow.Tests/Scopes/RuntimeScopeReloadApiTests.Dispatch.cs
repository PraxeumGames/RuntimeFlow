using NUnit.Framework;
using System;
using System.Linq;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{

public sealed partial class RuntimeScopeReloadApiTests
{
    [Test]
    public async Task ReloadScopeAsync_GenericDispatch_ReloadsSessionSceneAndModule()
    {
        var sessionService = new AttemptControlledSessionService((_, _) => Task.CompletedTask);
        var sceneService = new AttemptControlledSceneService((_, _) => Task.CompletedTask);
        var moduleService = new AttemptControlledModuleService((_, _) => Task.CompletedTask);

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<ITestSessionService>(sessionService);
            builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(sceneService)));
            builder.Module(new TestModuleScope(m => m.RegisterInstance<ITestModuleService>(moduleService)));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadModuleAsync<TestModuleScope>();
        await pipeline.ReloadScopeAsync<SessionScope>();
        await pipeline.ReloadScopeAsync<TestSceneScope>();
        await pipeline.ReloadScopeAsync<TestModuleScope>();

        Assert.AreEqual(2, sessionService.Attempts);
        Assert.AreEqual(3, sceneService.Attempts);
        Assert.AreEqual(3, moduleService.Attempts);
    }

    [Test]
    public async Task ReloadScopeAsync_GlobalScope_ThrowsDeterministicException()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineGlobalScope();
        });

        await pipeline.InitializeAsync();

        var exception = await AsyncTestAssert.ThrowsAsync<ScopeNotRestartableException>(() =>
            pipeline.ReloadScopeAsync<GlobalScope>());

        Assert.AreEqual(typeof(GlobalScope), exception.ScopeType);
    }

    [Test]
    public async Task ReloadScopeAsync_UndeclaredScope_ThrowsDeterministicDiagnostic()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
        });

        await pipeline.InitializeAsync();

        var exception = await AsyncTestAssert.ThrowsAsync<ScopeNotDeclaredException>(() =>
            pipeline.ReloadScopeAsync<UndeclaredScope>());

        Assert.AreEqual(typeof(UndeclaredScope), exception.ScopeType);
    }

    [Test]
    public async Task ReloadScopeAsync_ModuleScopeWithoutSceneContext_ThrowsDeterministicPrecondition()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.Module(new TestModuleScope(m => m.RegisterInstance<ITestModuleService>(
                new AttemptControlledModuleService((_, _) => Task.CompletedTask))));
        });

        await pipeline.InitializeAsync();

        var exception = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.ReloadScopeAsync<TestModuleScope>());

        Assert.AreEqual("Scene context is not initialized. Call LoadSceneAsync first.", exception.Message);
        Assert.AreEqual("reload_module", pipeline.GetRuntimeStatus().CurrentOperationCode);
    }

    [Test]
    public async Task ReloadScopeAsync_DispatchPublishesRestartAndReloadOperationKinds()
    {
        var observer = new CollectingRuntimeLoadingProgressObserver();
        var sessionService = new AttemptControlledSessionService((_, _) => Task.CompletedTask);
        var sceneService = new AttemptControlledSceneService((_, _) => Task.CompletedTask);
        var moduleService = new AttemptControlledModuleService((_, _) => Task.CompletedTask);

        var pipeline = RuntimePipeline.Create(
            builder =>
            {
                builder.DefineSessionScope();
                builder.Session().RegisterInstance<ITestSessionService>(sessionService);
                builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(sceneService)));
                builder.Module(new TestModuleScope(m => m.RegisterInstance<ITestModuleService>(moduleService)));
            },
            options => options.LoadingProgressObserver = observer);

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadModuleAsync<TestModuleScope>();
        await pipeline.ReloadScopeAsync<SessionScope>();
        await pipeline.ReloadScopeAsync<TestSceneScope>();
        await pipeline.ReloadScopeAsync<TestModuleScope>();

        var scopeReloadSnapshots = observer.Snapshots
            .Where(snapshot => snapshot.OperationKind is RuntimeLoadingOperationKind.RestartSession
                or RuntimeLoadingOperationKind.ReloadScene
                or RuntimeLoadingOperationKind.ReloadModule)
            .ToArray();

        AssertScopeReloadProgressContract(scopeReloadSnapshots);
    }

    [Test]
    public async Task FlowContextReloadScopeAsync_GenericDispatch_ReloadsSessionSceneAndModule()
    {
        var observer = new CollectingRuntimeLoadingProgressObserver();
        var sessionService = new AttemptControlledSessionService((_, _) => Task.CompletedTask);
        var sceneService = new AttemptControlledSceneService((_, _) => Task.CompletedTask);
        var moduleService = new AttemptControlledModuleService((_, _) => Task.CompletedTask);

        var pipeline = RuntimePipeline.Create(
                builder =>
                {
                    builder.DefineSessionScope();
                    builder.Session().RegisterInstance<ITestSessionService>(sessionService);
                    builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(sceneService)));
                    builder.Module(new TestModuleScope(m => m.RegisterInstance<ITestModuleService>(moduleService)));
                },
                options => options.LoadingProgressObserver = observer)
            .ConfigureFlow(new DelegateRuntimeFlowScenario(async (context, cancellationToken) =>
            {
                await context.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await context.LoadScopeSceneAsync<TestSceneScope>(cancellationToken).ConfigureAwait(false);
                await context.LoadScopeModuleAsync<TestModuleScope>(cancellationToken).ConfigureAwait(false);
                await context.ReloadScopeAsync<SessionScope>(cancellationToken).ConfigureAwait(false);
                await context.ReloadScopeAsync<TestSceneScope>(cancellationToken).ConfigureAwait(false);
                await context.ReloadScopeAsync<TestModuleScope>(cancellationToken).ConfigureAwait(false);
            }));

        await pipeline.RunAsync(NoopSceneLoader.Instance);

        Assert.AreEqual(2, sessionService.Attempts);
        Assert.AreEqual(3, sceneService.Attempts);
        Assert.AreEqual(3, moduleService.Attempts);

        var scopeReloadSnapshots = observer.Snapshots
            .Where(snapshot => snapshot.OperationKind is RuntimeLoadingOperationKind.RestartSession
                or RuntimeLoadingOperationKind.ReloadScene
                or RuntimeLoadingOperationKind.ReloadModule)
            .ToArray();

        AssertScopeReloadProgressContract(scopeReloadSnapshots);
    }

    [Test]
    public async Task FlowContextReloadScopeAsync_GlobalScope_ThrowsDeterministicException()
    {
        var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineGlobalScope();
            })
            .ConfigureFlow(new DelegateRuntimeFlowScenario(async (context, cancellationToken) =>
            {
                await context.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await context.ReloadScopeAsync<GlobalScope>(cancellationToken).ConfigureAwait(false);
            }));

        var exception = await AsyncTestAssert.ThrowsAsync<ScopeNotRestartableException>(() =>
            pipeline.RunAsync(NoopSceneLoader.Instance));

        Assert.AreEqual(typeof(GlobalScope), exception.ScopeType);
    }

    [Test]
    public async Task FlowContextReloadScopeAsync_UndeclaredScope_ThrowsDeterministicDiagnostic()
    {
        var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
            })
            .ConfigureFlow(new DelegateRuntimeFlowScenario(async (context, cancellationToken) =>
            {
                await context.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await context.ReloadScopeAsync<UndeclaredScope>(cancellationToken).ConfigureAwait(false);
            }));

        var exception = await AsyncTestAssert.ThrowsAsync<ScopeNotDeclaredException>(() =>
            pipeline.RunAsync(NoopSceneLoader.Instance));

        Assert.AreEqual(typeof(UndeclaredScope), exception.ScopeType);
    }
}

}
