using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using RuntimeFlow.Pipeline;

namespace RuntimeFlow.Tests.PlayMode
{
    /// <summary>
    /// PlayMode regression for resolution paths that the pipeline executes on worker
    /// continuations: decorated services (materialized lazily at resolve time) and
    /// auto-services with parent-scope dependencies (read from the parent ledger).
    /// Both used to rely on blocking cross-thread dispatch and would throw after the
    /// honest resolution contract landed.
    /// </summary>
    public sealed class WorkerThreadResolutionPathsTests
    {
        private interface IPaymentsService
        {
            string Pay();
        }

        private sealed class PaymentsService : IPaymentsService
        {
            public string Pay() => "inner";
        }

        private sealed class LoggingPaymentsDecorator : IPaymentsService
        {
            private readonly IPaymentsService _inner;

            public LoggingPaymentsDecorator(IPaymentsService inner) => _inner = inner;

            public string Pay() => "decorated+" + _inner.Pay();
        }

        [Test]
        public async Task Pipeline_DecoratedService_ResolvesAcrossWorkerBuiltContext()
        {
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().Register<IPaymentsService, PaymentsService>(DiLifetime.Singleton);
                builder.Session().Decorate<IPaymentsService, LoggingPaymentsDecorator>();
            });

            await pipeline.InitializeAsync();

            var session = pipeline.SessionContext;
            var payments = await session.ResolveAsync<IPaymentsService>();

            Assert.That(payments, Is.InstanceOf<LoggingPaymentsDecorator>(),
                "Decorated instances must materialize at resolve time, not at context-initialize time.");
            Assert.That(payments.Pay(), Is.EqualTo("decorated+inner"));
        }

        private interface IParentConfigService
        {
            string Value { get; }
        }

        private sealed class ParentConfigService : IParentConfigService
        {
            public string Value => "from-parent";
        }

        [Test]
        public void ParentLedgerRead_ReturnsMaterializedInstanceWithoutDispatch()
        {
            var parent = new GameContext();
            parent.Register<IParentConfigService, ParentConfigService>();
            parent.Initialize();
            parent.Resolve<IParentConfigService>();

            // Cross-context reads (auto-service parent fallbacks) go through this ledger
            // API precisely so they never dispatch or construct off the main thread.
            Assert.That(parent.TryGetInitializedByType(typeof(IParentConfigService), out var instance), Is.True,
                "A materialized parent singleton must be readable through the parent ledger without resolution.");
            Assert.That(instance, Is.InstanceOf<ParentConfigService>());
        }
    }
}
