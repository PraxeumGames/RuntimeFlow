using NUnit.Framework;
using System;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using RuntimeFlow.Pipeline;

namespace RuntimeFlow.Tests.PlayMode
{
    /// <summary>
    /// Ownership contract for decorated services: the wrapper produced by decoration is a
    /// real instance owned by the context, so it must participate in teardown alongside the
    /// inner instance it wraps.
    /// </summary>
    public sealed class DecorationOwnershipTests
    {
        private interface ITrackedService { }

        private sealed class TrackedService : ITrackedService, IDisposable
        {
            public int DisposeCount { get; private set; }
            public void Dispose() => DisposeCount++;
        }

        private sealed class PassThroughDecorator : ITrackedService, IDisposable
        {
            public int DisposeCount { get; private set; }
            public void Dispose() => DisposeCount++;
        }

        [Test]
        public async Task Pipeline_DecoratedWrapper_IsDisposedOnTeardown()
        {
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().Register<ITrackedService, TrackedService>(DiLifetime.Singleton);
                builder.Session().Decorate<ITrackedService, PassThroughDecorator>();
            });

            await pipeline.InitializeAsync();
            var session = pipeline.SessionContext;
            var wrapper = await session.ResolveAsync<ITrackedService>();
            Assert.That(wrapper, Is.InstanceOf<PassThroughDecorator>());

            var inner = session.Resolve<ITrackedService>();
            Assert.AreSame(wrapper, inner);

            await pipeline.DisposeAsync();

            var wrapperDisposeCount = ((PassThroughDecorator)wrapper).DisposeCount;
            Assert.That(wrapperDisposeCount, Is.EqualTo(1),
                "The decorator wrapper is owned by the context and must be disposed exactly once.");
        }
    }
}
