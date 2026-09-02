using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Tests.Support;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.Tests.VContainerIntegration
{
    /// <summary>
    /// VContainer entry points run synchronously inside Build(); RuntimeFlow's async graph starts only
    /// after the container exists, so IInitializable always precedes any InitializeAsync.
    /// </summary>
    [TestFixture]
    public sealed class EntryPointTests
    {
        public sealed class Trace
        {
            public List<string> Entries { get; } = new List<string>();
        }

        public sealed class EarlyEntryPoint : IInitializable
        {
            private readonly Trace _trace;

            public EarlyEntryPoint(Trace trace) => _trace = trace;

            public void Initialize() => _trace.Entries.Add("entry-point");
        }

        public sealed class ThrowingEntryPoint : IInitializable
        {
            public void Initialize() => throw new InvalidOperationException("entry point exploded");
        }

        public sealed class Recorded : IAsyncInitializable
        {
            private readonly Trace _trace;

            public Recorded(Trace trace) => _trace = trace;

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _trace.Entries.Add("async-init");
                return Task.CompletedTask;
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task InitializableRunsInsideBuildBeforeAnyInitializeAsync()
        {
            var trace = new Trace();
            var log = new CapturingLogger();

            var container = TestScope.Build(b =>
            {
                b.RegisterInstance(trace);
                b.Register<EarlyEntryPoint>(Lifetime.Singleton).AsImplementedInterfaces();
                b.Register<Recorded>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces();
                EntryPointsBuilder.EnsureDispatcherRegistered(b);
            });

            Assert.That(trace.Entries, Is.EqualTo(new[] { "entry-point" }), "IInitializable must run inside Build()");

            await ScopeRun.Create(container, "session", TestScope.Options(log)).RunAsync();

            Assert.That(trace.Entries, Is.EqualTo(new[] { "entry-point", "async-init" }));
        }

        [Test]
        public void AnEntryPointExceptionReachesTheRegisteredHandler()
        {
            var collected = new List<Exception>();

            TestScope.Build(b =>
            {
                b.RegisterEntryPointExceptionHandler(collected.Add);
                b.Register<ThrowingEntryPoint>(Lifetime.Singleton).AsImplementedInterfaces();
                EntryPointsBuilder.EnsureDispatcherRegistered(b);
            });

            Assert.That(collected, Has.Count.EqualTo(1));
            Assert.That(collected[0], Is.TypeOf<InvalidOperationException>());
            Assert.That(collected[0].Message, Is.EqualTo("entry point exploded"));
        }

        [Test]
        public void TheDispatcherIsRegisteredOnlyOnce()
        {
            var container = TestScope.Build(b =>
            {
                EntryPointsBuilder.EnsureDispatcherRegistered(b);
                EntryPointsBuilder.EnsureDispatcherRegistered(b);
            });

            Assert.That(container.TryGetRegistration(typeof(EntryPointDispatcher), out var registration), Is.True);
            Assert.That(registration.Provider, Is.Not.InstanceOf<IEnumerable<Registration>>(),
                "a second registration would turn the dispatcher into a collection");
        }
    }
}
