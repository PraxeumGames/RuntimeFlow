using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

public sealed class LazyInitializationTests
{
    [Fact]
    public async Task LazyService_NotInitializedDuringBuild()
    {
        var service = new TestLazySessionService();
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<ITestLazySessionService>(service);
        });

        await pipeline.InitializeAsync();

        Assert.Equal(0, service.InitCount);
    }

    [Fact]
    public async Task LazyService_InitializedOnDemand()
    {
        var service = new TestLazySessionService();
        var builder = new GameContextBuilder();
        builder.DefineSessionScope();
        builder.Session().RegisterInstance<ITestLazySessionService>(service);

        await builder.BuildAsync();
        Assert.Equal(0, service.InitCount);

        await builder.EnsureLazyServiceInitializedAsync(typeof(ITestLazySessionService));
        Assert.Equal(1, service.InitCount);
    }

    [Fact]
    public async Task LazyService_InitializedOnlyOnce()
    {
        var service = new TestLazySessionService();
        var builder = new GameContextBuilder();
        builder.DefineSessionScope();
        builder.Session().RegisterInstance<ITestLazySessionService>(service);

        await builder.BuildAsync();

        await builder.EnsureLazyServiceInitializedAsync(typeof(ITestLazySessionService));
        await builder.EnsureLazyServiceInitializedAsync(typeof(ITestLazySessionService));
        Assert.Equal(1, service.InitCount);
    }

    [Fact]
    public async Task LazyService_ReinitializedAfterBuildRebuild()
    {
        var service = new TestLazySessionService();
        var builder = new GameContextBuilder();
        builder.DefineSessionScope();
        builder.Session().RegisterInstance<ITestLazySessionService>(service);

        await builder.BuildAsync();
        await builder.EnsureLazyServiceInitializedAsync(typeof(ITestLazySessionService));

        await builder.BuildAsync();
        await builder.EnsureLazyServiceInitializedAsync(typeof(ITestLazySessionService));

        Assert.Equal(2, service.InitCount);
    }

    [Fact]
    public async Task NonLazyService_StillInitializedEagerly()
    {
        var service = new TestEagerSessionService();
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<ITestEagerSessionService>(service);
        });

        await pipeline.InitializeAsync();

        Assert.Equal(1, service.InitCount);
    }

    // --- Contracts ---
    internal interface ITestLazySessionService : ISessionInitializableService { int InitCount { get; } }
    internal interface ITestEagerSessionService : ISessionInitializableService { int InitCount { get; } }

    // --- Implementations ---
    internal sealed class TestLazySessionService : ITestLazySessionService, ILazyInitializableService
    {
        private int _initCount;
        public int InitCount => _initCount;
        public Task InitializeAsync(CancellationToken ct) { Interlocked.Increment(ref _initCount); return Task.CompletedTask; }
    }

    internal sealed class TestEagerSessionService : ITestEagerSessionService
    {
        private int _initCount;
        public int InitCount => _initCount;
        public Task InitializeAsync(CancellationToken ct) { Interlocked.Increment(ref _initCount); return Task.CompletedTask; }
    }
}
