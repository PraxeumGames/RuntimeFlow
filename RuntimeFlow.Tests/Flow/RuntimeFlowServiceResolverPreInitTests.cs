using RuntimeFlow.Contexts;
using Xunit;

namespace RuntimeFlow.Tests;

public sealed class RuntimeFlowServiceResolverPreInitTests
{
    [Fact]
    public void TryResolveFromContext_BeforeInitialize_DoesNotThrow_AndReturnsFalse()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<PreInitProbeService>(new PreInitProbeService());
        });

        var exception = Record.Exception(() =>
        {
            var resolved = RuntimeFlowServiceResolver.TryResolveFromContext(
                (IGameContext)null,
                out PreInitProbeService service);

            Assert.False(resolved);
            Assert.Null(service);
        });

        Assert.Null(exception);
    }

    [Fact]
    public async Task TryResolveFromContext_AfterInitialize_ResolvesRegisteredService()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<PreInitProbeService>(new PreInitProbeService());
        });

        await pipeline.InitializeAsync();

        var resolved = RuntimeFlowServiceResolver.TryResolveFromContext(
            pipeline.SessionContext,
            out PreInitProbeService service);

        Assert.True(resolved);
        Assert.NotNull(service);
    }

    private sealed class PreInitProbeService
    {
    }
}
