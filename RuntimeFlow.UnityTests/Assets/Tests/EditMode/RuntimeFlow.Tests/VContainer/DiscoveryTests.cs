using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Testing;
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

        public sealed class EnumerableBarrier : AutoService
        {
            public EnumerableBarrier(IEnumerable<IBackgroundTask> tasks) => Tasks = tasks;

            public IEnumerable<IBackgroundTask> Tasks { get; }
        }

        public sealed class ArrayBarrier : AutoService
        {
            public ArrayBarrier(IBackgroundTask[] tasks) => Tasks = tasks;

            public IBackgroundTask[] Tasks { get; }
        }

        public sealed class ReadOnlyCollectionBarrier : AutoService
        {
            public ReadOnlyCollectionBarrier(IReadOnlyCollection<IBackgroundTask> tasks) => Tasks = tasks;

            public IReadOnlyCollection<IBackgroundTask> Tasks { get; }
        }

        public sealed class ListBarrier : AutoService
        {
            public ListBarrier(List<IBackgroundTask> tasks) => Tasks = tasks;

            public List<IBackgroundTask> Tasks { get; }
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

        public sealed class ZuluDependency : AutoService { }

        public sealed class AlphaDependency : AutoService { }

        /// <summary>
        /// Two constructors of equal arity and no [Inject]: VContainer injects the first one reflection
        /// returns. The first is declared with the type that sorts last, so a tie broken by signature
        /// picks the other constructor and the graph edge would disagree with the injected dependency.
        /// </summary>
        public sealed class AmbiguousConstructors : AutoService
        {
            public AmbiguousConstructors(ZuluDependency zulu) => Injected = zulu;

            public AmbiguousConstructors(AlphaDependency alpha) => Injected = alpha;

            public object Injected { get; }
        }

        private CapturingLogger _log = null!;
        private RuntimeFlowOptions _options = null!;

        private readonly RunTracker _tracker = new RunTracker();

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _options = TestScope.Options(_log);
        }

        private static List<string> Dependencies(ScopeRun run, string service)
            => run.GetStatus().Service(service).Dependencies.ToList();

        /// <summary>Disposes every run and container this fixture created, so nothing leaks into the next test.</summary>
        [TearDown]
        public void DisposeTrackedRuns() => _tracker.DisposeAll();

        [Test]
        public void AConcreteConstructorParameterIsAnEdge()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<Concrete>();
                b.Add<Config>();
            });

            var run = _tracker.Create(container, "session", _options);

            Assert.That(Dependencies(run, "Concrete"), Is.EqualTo(new[] { "Config" }));
        }

        [Test]
        public void AnInterfaceParameterDependsOnTheImplementationVContainerInjects()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<ThingUser>();
                b.Add<ThingOne>();
                b.Add<ThingTwo>();
            });

            var run = _tracker.Create(container, "session", _options);

            // VContainer resolves a single IThing to the last registration exposing it; only a collection
            // parameter depends on every implementation.
            Assert.That(container.Resolve<ThingUser>().Thing, Is.TypeOf<ThingTwo>());
            Assert.That(Dependencies(run, "ThingUser"), Is.EqualTo(new[] { "ThingTwo" }));
        }

        [Test]
        public void ACollectionParameterIsABarrierOverEveryElement()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<Barrier>();
                b.Add<TaskOne>();
                b.Add<TaskTwo>();
            });

            var run = _tracker.Create(container, "session", _options);

            Assert.That(Dependencies(run, "Barrier"), Is.EquivalentTo(new[] { "TaskOne", "TaskTwo" }));
        }

        [Test]
        public void OnlyTheCollectionShapesVContainerResolvesAreBarriers()
        {
            Assert.That(DependenciesOf<EnumerableBarrier>(), Is.EquivalentTo(new[] { "TaskOne", "TaskTwo" }),
                "IEnumerable<X>");
            Assert.That(DependenciesOf<Barrier>(), Is.EquivalentTo(new[] { "TaskOne", "TaskTwo" }),
                "IReadOnlyList<X>");

            // VContainer cannot resolve these shapes: the service fails construction, it has no edges.
            Assert.That(DependenciesOf<ArrayBarrier>(), Is.Empty, "X[]");
            Assert.That(DependenciesOf<ReadOnlyCollectionBarrier>(), Is.Empty, "IReadOnlyCollection<X>");
            Assert.That(DependenciesOf<ListBarrier>(), Is.Empty, "List<X>");
        }

        /// <summary>Edges of a single collection-taking service; the graph is built, not run.</summary>
        private List<string> DependenciesOf<T>() where T : class, IAsyncInitializable
        {
            var container = _tracker.Build(b =>
            {
                b.Add<T>();
                b.Add<TaskOne>();
                b.Add<TaskTwo>();
            });
            return Dependencies(_tracker.Create(container, "session", _options), typeof(T).Name);
        }

        [Test]
        public void FuncAndLazyParametersAreNotEdges()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<LazyUser>();
                b.Add<Config>();
                b.Add<ThingOne>();
                b.RegisterFactory<Config>(() => new Config());
                b.RegisterInstance<ILazy<ThingOne>>(new LazyBox<ThingOne>(new ThingOne()));
            });

            var run = _tracker.Create(container, "session", _options);

            Assert.That(Dependencies(run, "LazyUser"), Is.Empty);
            Assert.That(run.Describe(), Does.Contain("lazy: Func<Config> config"));
            Assert.That(run.Describe(), Does.Contain("lazy: ILazy<ThingOne> thing"));
        }

        [Test]
        public void ADecoratorRegisteredThroughAFactoryStillGetsItsEdges()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<Real>();
                b.Register<IDecorated>(r => new Decorator(r.Resolve<Real>()), Lifetime.Singleton)
                    .As<IAsyncInitializable>();
            });

            var run = _tracker.Create(container, "session", _options);

            Assert.That(Dependencies(run, "Decorator"), Is.EqualTo(new[] { "Real" }));
        }

        [Test]
        [Timeout(10000)]
        public async Task RegisterInstanceServicesAreInitialized()
        {
            var instance = new Instanced();
            var container = _tracker.Build(b => b.RegisterInstance(instance).AsImplementedInterfaces());

            await _tracker.Create(container, "session", _options).RunAsync();

            Assert.That(instance.Attempts, Is.EqualTo(1));
        }

        [Test]
        public void ParentScopeServicesBecomeExternalNodes()
        {
            var global = _tracker.Build(b => b.Add<GlobalConfig>());
            var globalRun = _tracker.Create(global, "global", _options);
            var session = global.CreateScope(b => b.Add<SessionUser>());

            var run = _tracker.Create(session, "session", _options, new List<ScopeRun> { globalRun });

            Assert.That(run.GetStatus().TotalCount, Is.EqualTo(1));
            Assert.That(Dependencies(run, "SessionUser"), Is.EqualTo(new[] { "GlobalConfig" }));
        }

        [Test]
        [Timeout(10000)]
        public async Task AChildScopeNeverReinitializesParentServices()
        {
            var global = _tracker.Build(b => b.Add<GlobalConfig>());
            var globalRun = _tracker.Create(global, "global", _options);
            await globalRun.RunAsync();
            var session = global.CreateScope(b => b.Add<SessionUser>());

            await _tracker.Create(session, "session", _options, new List<ScopeRun> { globalRun }).RunAsync();

            var config = (GlobalConfig)global.Resolve<GlobalConfig>();
            Assert.That(config.Attempts, Is.EqualTo(1));
        }

        [Test]
        public void WithoutExplicitParentsTheParentChainIsWalked()
        {
            var global = _tracker.Build(b => b.Add<GlobalConfig>());
            var session = global.CreateScope(b => b.Add<SessionUser>());

            var run = _tracker.Create(session, "session", _options);

            Assert.That(Dependencies(run, "SessionUser"), Is.EqualTo(new[] { "GlobalConfig" }));
            Assert.That(run.Describe(), Does.Contain("external (from parent scopes): GlobalConfig [parent, initialized]"));
        }

        [Test]
        public void EqualArityConstructorsGetTheEdgeOfTheConstructorVContainerInjectsAndAWarning()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<ZuluDependency>();
                b.Add<AlphaDependency>();
                b.Add<AmbiguousConstructors>();
            });

            var run = _tracker.Create(container, "session", _options);
            var injected = container.Resolve<AmbiguousConstructors>().Injected.GetType().Name;

            Assert.That(Dependencies(run, "AmbiguousConstructors"), Is.EqualTo(new[] { injected }),
                "the edge must come from the very constructor VContainer injected");
            Assert.That(_log.Has(Microsoft.Extensions.Logging.LogLevel.Warning,
                "[RuntimeFlow] session: AmbiguousConstructors has 2 constructors with 1 parameter and none is marked [Inject]"),
                Is.True, _log.Dump());
        }

        private sealed class LazyBox<T> : ILazy<T>
        {
            public LazyBox(T value) => Value = value;

            public T Value { get; }
        }
    }
}
