using NUnit.Framework;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{
    public sealed class RuntimeFlowServiceResolverPreInitTests
    {
        [Test]
        public void TryResolveFromContext_BeforeInitialize_DoesNotThrow_AndReturnsFalse()
        {
            _ = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().RegisterInstance<PreInitProbeService>(new PreInitProbeService());
            });

            var resolved = true;
            PreInitProbeService service = null!;

            Assert.That(() =>
            {
                resolved = RuntimeFlowServiceResolver.TryResolveFromContext(
                    (IGameContext?)null,
                    out service);
            }, Throws.Nothing);

            Assert.That(resolved, Is.False);
            Assert.That(service, Is.Null);
        }

        [Test]
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

            Assert.That(resolved, Is.True);
            Assert.That(service, Is.Not.Null);
        }

        private sealed class PreInitProbeService
        {
        }
    }
}
