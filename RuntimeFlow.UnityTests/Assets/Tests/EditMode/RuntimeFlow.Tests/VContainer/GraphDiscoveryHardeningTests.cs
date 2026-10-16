using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.VContainerIntegration
{
    /// <summary>
    /// Discovery hardening: parent services inferred without linked runs, constructor edges of
    /// instances and factory products that VContainer never resolved, invalid [DependsOn] targets and
    /// open generic registrations that can never be discovered.
    /// </summary>
    [TestFixture]
    public sealed class GraphDiscoveryHardeningTests
    {
        private readonly RunTracker _tracker = new RunTracker();
        private CapturingLogger _log = null!;
        private RuntimeFlowOptions _options = null!;

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _options = TestScope.Options(_log);
        }

        [TearDown]
        public void TearDown() => _tracker.DisposeAll();

        // ------------------------------------------------------------------ inferred parent services

        public interface IBrokenParent : IAsyncInitializable { }

        [DependsOn(typeof(IBrokenParent))]
        public sealed class NeedsTheBrokenParent : AutoService { }

        public sealed class InferredParent : AutoService { }

        public sealed class NeedsTheInferredParent : AutoService
        {
            public NeedsTheInferredParent(InferredParent parent) { }
        }

        [Test]
        [Timeout(10000)]
        public async Task AStandaloneChildRunFailsTheDependentOfAParentServiceThatCannotBeConstructed()
        {
            var global = _tracker.Build(b => b.Register<IBrokenParent>(_ => throw new InvalidOperationException("parent boom"),
                Lifetime.Singleton).As<IAsyncInitializable>());
            var session = _tracker.Track(global.CreateScope(b => b.Add<NeedsTheBrokenParent>()));
            var run = _tracker.Create(session, "session", _options);

            var failure = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => run.RunAsync());

            Assert.That(failure.InnerException, Is.TypeOf<InitGraphException>());
            Assert.That(failure.InnerException!.Message, Does.Contain("could not construct").And.Contain("parent boom"));
            Assert.That(session.Resolve<NeedsTheBrokenParent>().Attempts, Is.Zero, "the dependent never started against a missing parent");
            Assert.That(run.Describe(), Does.Contain("failed"), run.Describe());
        }

        [Test]
        public void AStandaloneChildRunWarnsOnceAboutParentServicesNoLinkedRunInitialized()
        {
            var global = _tracker.Build(b => b.Add<InferredParent>());
            var session = _tracker.Track(global.CreateScope(b => b.Add<NeedsTheInferredParent>()));

            _tracker.Create(session, "session", _options);

            var warning = _log.Find(LogLevel.Warning, "without a linked parent run");
            Assert.That(warning, Does.Contain(nameof(InferredParent)), _log.Dump());
        }

        [Test]
        public void ALinkedChildRunDoesNotWarnAboutItsParentServices()
        {
            var global = _tracker.Build(b => b.Add<InferredParent>());
            var globalRun = _tracker.Create(global, "global", _options);
            var session = _tracker.Track(global.CreateScope(b => b.Add<NeedsTheInferredParent>()));

            _tracker.Create(session, "session", _options, new List<ScopeRun> { globalRun });

            Assert.That(_log.Has(LogLevel.Warning, "without a linked parent run"), Is.False, _log.Dump());
        }

        // ------------------------------------------------------------------ edges VContainer never resolved

        public sealed class UsesTheHolder : AutoService
        {
            public UsesTheHolder(PrebuiltHolder holder) { }
        }

        /// <summary>Built by user code; its constructor parameter is never resolved by VContainer.</summary>
        public sealed class PrebuiltHolder : AutoService
        {
            public PrebuiltHolder(UsesTheHolder? user) => User = user;
            public UsesTheHolder? User { get; }
        }

        [TestCase(false)]
        [TestCase(true)]
        [Timeout(10000)]
        public async Task AnUnresolvedConstructorParameterOfAnInstanceOrFactoryCannotCloseACycle(bool factory)
        {
            var container = _tracker.Build(b =>
            {
                if (factory)
                    b.Register(_ => new PrebuiltHolder(null), Lifetime.Singleton).AsSelf().As<IAsyncInitializable>();
                else
                    b.RegisterInstance(new PrebuiltHolder(null)).AsSelf().As<IAsyncInitializable>();
                b.Add<UsesTheHolder>();
            });

            ScopeRun? run = null;
            Assert.DoesNotThrow(() => run = _tracker.Create(container, "session", _options), "a false cycle failed the graph");

            Assert.That(run!.GetStatus().Service(nameof(UsesTheHolder)).Dependencies, Is.EqualTo(new[] { nameof(PrebuiltHolder) }), run.Describe());
            Assert.That(run.GetStatus().Service(nameof(PrebuiltHolder)).Dependencies, Is.Empty, run.Describe());
            Assert.That(_log.Find(LogLevel.Warning, "ignoring the edge"), Does.Contain(nameof(PrebuiltHolder)), _log.Dump());
            Assert.That((await run.RunAsync()).Outcome, Is.EqualTo(StartupOutcome.Completed));
        }

        // ------------------------------------------------------------------ invalid [DependsOn] targets

#pragma warning disable CS8625 // the null target is the point of the test
        [DependsOn(null)]
        public sealed class DependsOnNull : AutoService { }
#pragma warning restore CS8625

        public interface IRepository<T> { }

        [DependsOn(typeof(IRepository<>))]
        public sealed class DependsOnAnOpenGeneric : AutoService { }

        [Test]
        public void ANullDependsOnTargetIsAGraphError()
        {
            var container = _tracker.Build(b => b.Add<DependsOnNull>());

            var error = Assert.Throws<InitGraphException>(() => _tracker.Create(container, "session", _options));

            Assert.That(error!.Message, Does.Contain(nameof(DependsOnNull)).And.Contain("[DependsOn(null)]"));
        }

        [Test]
        public void AnOpenGenericDependsOnTargetIsAGraphError()
        {
            var container = _tracker.Build(b => b.Add<DependsOnAnOpenGeneric>());

            var error = Assert.Throws<InitGraphException>(() => _tracker.Create(container, "session", _options));

            Assert.That(error!.Message, Does.Contain(nameof(DependsOnAnOpenGeneric)).And.Contain("open generic")
                .And.Contain("IRepository<T>"));
        }

        // ------------------------------------------------------------------ open generic registrations

        public interface IGenericMarker<T> { }

        public sealed class GenericInitializable<T> : IGenericMarker<T>, IAsyncInitializable
        {
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        public sealed class Plain : AutoService { }

        [Test]
        public void AnOpenGenericInitializableRegistrationIsReported()
        {
            var container = _tracker.Build(b =>
            {
                b.Register(typeof(GenericInitializable<>), Lifetime.Singleton).As(typeof(IGenericMarker<>));
                b.Add<Plain>();
            });

            _tracker.Create(container, "session", _options);

            var warning = _log.Find(LogLevel.Warning, "open generic");
            Assert.That(warning, Does.Contain("GenericInitializable<T>"), _log.Dump());
        }
    }
}
