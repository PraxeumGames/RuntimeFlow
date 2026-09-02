using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.VContainerIntegration
{
    /// <summary>Edges come from whatever VContainer resolved: constructors, collections and decorators.</summary>
    [TestFixture]
    public sealed class DiscoveryTests
    {
        public interface IThing { }

        public interface IBackgroundTask { }

        public interface ILazy<out T> { T Value { get; } }

        public sealed class Config : AutoService { }

        public sealed class Concrete : AutoService
        {
            public Concrete(Config config) => Config = config;

            public Config Config { get; }
        }

        public sealed class ThingOne : AutoService, IThing { }

        public sealed class ThingTwo : AutoService, IThing { }

        public sealed class ThingUser : AutoService
        {
            public ThingUser(IThing thing) => Thing = thing;

            public IThing Thing { get; }
        }

        public sealed class TaskOne : AutoService, IBackgroundTask { }

        public sealed class TaskTwo : AutoService, IBackgroundTask { }

        public sealed class Barrier : AutoService
        {
            public Barrier(IReadOnlyList<IBackgroundTask> tasks) => Tasks = tasks;

            public IReadOnlyList<IBackgroundTask> Tasks { get; }
        }

        public sealed class LazyUser : AutoService
        {
            public LazyUser(Func<Config> config, ILazy<ThingOne> thing)
            {
                Config = config;
                Thing = thing;
            }

            public Func<Config> Config { get; }
            public ILazy<ThingOne> Thing { get; }
        }

        public interface IDecorated : IAsyncInitializable { }

        public sealed class Real : AutoService, IDecorated { }

        public sealed class Decorator : AutoService, IDecorated
        {
            public Decorator(Real inner) => Inner = inner;

            public Real Inner { get; }
        }

        public sealed class Instanced : AutoService { }

        public sealed class GlobalConfig : AutoService { }

        public sealed class SessionUser : AutoService
        {
            public SessionUser(GlobalConfig config) => Config = config;

            public GlobalConfig Config { get; }
        }

        private CapturingLogger _log = null!;
        private RuntimeFlowOptions _options = null!;

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _options = TestScope.Options(_log);
        }

        private static List<string> Dependencies(ScopeRun run, string service)
            => run.GetStatus().Service(service).Dependencies.ToList();

        [Test]
        public void AConcreteConstructorParameterIsAnEdge()
        {
            var container = TestScope.Build(b =>
            {
                b.Add<Concrete>();
                b.Add<Config>();
            });

            var run = ScopeRun.Create(container, "session", _options);

            Assert.That(Dependencies(run, "Concrete"), Is.EqualTo(new[] { "Config" }));
        }

        [Test]
        public void AnInterfaceParameterWithTwoImplementationsDependsOnBoth()
        {
            var container = TestScope.Build(b =>
            {
                b.Add<ThingUser>();
                b.Add<ThingOne>();
                b.Add<ThingTwo>();
            });

            var run = ScopeRun.Create(container, "session", _options);

            Assert.That(Dependencies(run, "ThingUser"), Is.EquivalentTo(new[] { "ThingOne", "ThingTwo" }));
        }

        [Test]
        public void ACollectionParameterIsABarrierOverEveryElement()
        {
            var container = TestScope.Build(b =>
            {
                b.Add<Barrier>();
                b.Add<TaskOne>();
                b.Add<TaskTwo>();
            });

            var run = ScopeRun.Create(container, "session", _options);

            Assert.That(Dependencies(run, "Barrier"), Is.EquivalentTo(new[] { "TaskOne", "TaskTwo" }));
        }

        [Test]
        public void FuncAndLazyParametersAreNotEdges()
        {
            var container = TestScope.Build(b =>
            {
                b.Add<LazyUser>();
                b.Add<Config>();
                b.Add<ThingOne>();
                b.RegisterFactory<Config>(() => new Config());
                b.RegisterInstance<ILazy<ThingOne>>(new LazyBox<ThingOne>(new ThingOne()));
            });

            var run = ScopeRun.Create(container, "session", _options);

            Assert.That(Dependencies(run, "LazyUser"), Is.Empty);
            Assert.That(run.Describe(), Does.Contain("lazy: Func<Config> config"));
            Assert.That(run.Describe(), Does.Contain("lazy: ILazy<ThingOne> thing"));
        }

        [Test]
        public void ADecoratorRegisteredThroughAFactoryStillGetsItsEdges()
        {
            var container = TestScope.Build(b =>
            {
                b.Add<Real>();
                b.Register<IDecorated>(r => new Decorator(r.Resolve<Real>()), Lifetime.Singleton)
                    .As<IAsyncInitializable>();
            });

            var run = ScopeRun.Create(container, "session", _options);

            Assert.That(Dependencies(run, "Decorator"), Is.EqualTo(new[] { "Real" }));
        }

        [Test]
        [Timeout(10000)]
        public async Task RegisterInstanceServicesAreInitialized()
        {
            var instance = new Instanced();
            var container = TestScope.Build(b => b.RegisterInstance(instance).AsImplementedInterfaces());

            await ScopeRun.Create(container, "session", _options).RunAsync();

            Assert.That(instance.Attempts, Is.EqualTo(1));
        }

        [Test]
        public void ParentScopeServicesBecomeExternalNodes()
        {
            var global = TestScope.Build(b => b.Add<GlobalConfig>());
            var globalRun = ScopeRun.Create(global, "global", _options);
            var session = global.CreateScope(b => b.Add<SessionUser>());

            var run = ScopeRun.Create(session, "session", _options, new List<ScopeRun> { globalRun });

            Assert.That(run.GetStatus().TotalCount, Is.EqualTo(1));
            Assert.That(Dependencies(run, "SessionUser"), Is.EqualTo(new[] { "GlobalConfig" }));
        }

        [Test]
        [Timeout(10000)]
        public async Task AChildScopeNeverReinitializesParentServices()
        {
            var global = TestScope.Build(b => b.Add<GlobalConfig>());
            var globalRun = ScopeRun.Create(global, "global", _options);
            await globalRun.RunAsync();
            var session = global.CreateScope(b => b.Add<SessionUser>());

            await ScopeRun.Create(session, "session", _options, new List<ScopeRun> { globalRun }).RunAsync();

            var config = (GlobalConfig)global.Resolve<GlobalConfig>();
            Assert.That(config.Attempts, Is.EqualTo(1));
        }

        [Test]
        public void WithoutExplicitParentsTheParentChainIsWalked()
        {
            var global = TestScope.Build(b => b.Add<GlobalConfig>());
            var session = global.CreateScope(b => b.Add<SessionUser>());

            var run = ScopeRun.Create(session, "session", _options);

            Assert.That(Dependencies(run, "SessionUser"), Is.EqualTo(new[] { "GlobalConfig" }));
            Assert.That(run.Describe(), Does.Contain("external (from parent scopes): GlobalConfig [parent, initialized]"));
        }

        private sealed class LazyBox<T> : ILazy<T>
        {
            public LazyBox(T value) => Value = value;

            public T Value { get; }
        }
    }
}
