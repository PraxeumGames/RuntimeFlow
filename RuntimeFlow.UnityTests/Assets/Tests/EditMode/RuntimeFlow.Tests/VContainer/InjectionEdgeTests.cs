using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;
using VContainer.Internal;

namespace RuntimeFlow.Tests.VContainerIntegration
{
    /// <summary>
    /// Edges follow what VContainer actually injects: through plain (non-initializable) registrations,
    /// through [Inject] members, to the registration VContainer picks — never to a type that merely
    /// happens to be assignable — and never for a parameter a registration supplies itself.
    /// </summary>
    [TestFixture]
    public sealed class InjectionEdgeTests
    {
        public sealed class Auth : AutoService { }

        public interface IProfileApi { }

        /// <summary>A plain singleton (no IAsyncInitializable) that needs an initializable one.</summary>
        public sealed class ProfileApi : IProfileApi
        {
            public ProfileApi(Auth auth) { }
        }

        public sealed class Profile : AutoService
        {
            public Profile(IProfileApi api) { }
        }

        public sealed class Repository
        {
            public Repository(IProfileApi api) { }
        }

        public sealed class Facade : AutoService
        {
            public Facade(Repository repository) { }
        }

        public sealed class MethodInjected : AutoService
        {
            [Inject]
            public void Construct(Auth auth) { }
        }

        public abstract class InjectedBase : AutoService
        {
            [Inject] public Auth? FromBase;
        }

        public sealed class FieldInjected : InjectedBase
        {
            [Inject] public Profile? Profile { get; set; }
        }

        public interface IFoo { }

        /// <summary>Implements IFoo but is registered without exposing it.</summary>
        public sealed class HiddenFoo : AutoService, IFoo
        {
            public HiddenFoo(NeedsFoo needs) { }
        }

        public sealed class RealFoo : AutoService, IFoo { }

        public sealed class NeedsFoo : AutoService
        {
            public NeedsFoo(IFoo foo) => Foo = foo;

            public IFoo Foo { get; }
        }

        public sealed class TakesParameter : AutoService
        {
            public TakesParameter(Auth auth) => Auth = auth;

            public Auth Auth { get; }
        }

        public sealed class LocalUser : AutoService
        {
            public LocalUser(ContainerLocal<Auth> auth) { }
        }

        public sealed class ExplodingConstructor : AutoService
        {
            public ExplodingConstructor() => throw new InvalidOperationException("disk full.");
        }

        public interface IFactoryMade : IAsyncInitializable { }

        public sealed class FactoryMade : AutoService, IFactoryMade { }

        [DependsOn(typeof(FactoryMade))]
        public sealed class AfterFactoryMade : AutoService { }

        public sealed class AsyncDisposableService : AutoService, IAsyncDisposable
        {
            public int Disposed { get; private set; }

            public ValueTask DisposeAsync()
            {
                Disposed++;
                return default;
            }
        }

        [DependsOn(typeof(CycleB))]
        public sealed class CycleA : AutoService { }

        [DependsOn(typeof(CycleA))]
        public sealed class CycleB : AutoService { }

        private CapturingLogger _log = null!;
        private RuntimeFlowOptions _options = null!;

        private readonly RunTracker _tracker = new RunTracker();

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _options = TestScope.Options(_log);
        }

        [TearDown]
        public void DisposeTrackedRuns() => _tracker.DisposeAll();

        private static ServiceStatus Status(ScopeRun run, string service) => run.GetStatus().Service(service);

        [Test]
        public void AnEdgeRunsThroughAPlainRegistration()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<Profile>();
                b.Register<ProfileApi>(Lifetime.Singleton).As<IProfileApi>();
                b.Add<Auth>();
            });

            var run = _tracker.Create(container, "session", _options);

            Assert.That(Status(run, "Profile").Dependencies, Is.EqualTo(new[] { "Auth" }));
            Assert.That(run.Describe(), Does.Contain("ctor: IProfileApi api via ProfileApi"), run.Describe());
        }

        [Test]
        public void AnEdgeRunsThroughSeveralPlainRegistrations()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<Facade>();
                b.Register<Repository>(Lifetime.Singleton);
                b.Register<ProfileApi>(Lifetime.Singleton).As<IProfileApi>();
                b.Add<Auth>();
            });

            var run = _tracker.Create(container, "session", _options);

            Assert.That(Status(run, "Facade").Dependencies, Is.EqualTo(new[] { "Auth" }));
            Assert.That(run.Describe(), Does.Contain("ctor: Repository repository via Repository > ProfileApi"), run.Describe());
        }

        [Test]
        public void InjectMethodsFieldsAndPropertiesAreEdgesToo()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<MethodInjected>();
                b.Add<FieldInjected>();
                b.Add<Profile>();
                b.Register<ProfileApi>(Lifetime.Singleton).As<IProfileApi>();
                b.Add<Auth>();
            });

            var run = _tracker.Create(container, "session", _options);

            Assert.That(Status(run, "MethodInjected").Dependencies, Is.EqualTo(new[] { "Auth" }));
            Assert.That(Status(run, "FieldInjected").Dependencies, Is.EquivalentTo(new[] { "Auth", "Profile" }));
            Assert.That(run.Describe(), Does.Contain("[Inject] Construct(Auth auth)"), run.Describe());
        }

        [Test]
        public void ATypeThatIsOnlyAssignableButNotRegisteredAsTheParameterIsNoEdge()
        {
            var container = _tracker.Build(b =>
            {
                b.Register<RealFoo>(Lifetime.Singleton).As<IFoo, IAsyncInitializable>();
                b.Register<HiddenFoo>(Lifetime.Singleton).AsSelf().As<IAsyncInitializable>();
                b.Add<NeedsFoo>();
            });

            // HiddenFoo -> NeedsFoo -> IFoo: VContainer injects RealFoo, so there is no cycle back to HiddenFoo.
            var run = _tracker.Create(container, "session", _options);

            Assert.That(container.Resolve<NeedsFoo>().Foo, Is.TypeOf<RealFoo>());
            Assert.That(Status(run, "NeedsFoo").Dependencies, Is.EqualTo(new[] { "RealFoo" }));
        }

        [Test]
        public void AParameterSuppliedByWithParameterIsNoEdge()
        {
            var supplied = new Auth();
            var container = _tracker.Build(b =>
            {
                b.Add<TakesParameter>().WithParameter(supplied);
                b.Add<Auth>();
            });

            var run = _tracker.Create(container, "session", _options);

            Assert.That(container.Resolve<TakesParameter>().Auth, Is.SameAs(supplied));
            Assert.That(Status(run, "TakesParameter").Dependencies, Is.Empty);
        }

        [Test]
        public void AContainerLocalParameterIsAnEdgeToItsValue()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<LocalUser>();
                b.Add<Auth>();
            });

            var run = _tracker.Create(container, "session", _options);

            Assert.That(Status(run, "LocalUser").Dependencies, Is.EqualTo(new[] { "Auth" }));
        }

        [Test]
        [Timeout(10000)]
        public async Task AThrowingConstructorIsReportedWithItsOwnException()
        {
            var container = _tracker.Build(b => b.Add<ExplodingConstructor>());
            var run = _tracker.Create(container, "session", _options);

            var error = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => run.RunAsync());

            Assert.That(error.InnerException!.Message, Is.EqualTo(
                "Could not construct ExplodingConstructor in scope 'session': its construction threw " +
                "InvalidOperationException: disk full. See InnerException."));
            Assert.That(error.InnerException.InnerException, Is.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void AStandaloneChildRunNamesAFactoryMadeParentServiceByItsInstanceType()
        {
            var global = _tracker.Build(b =>
                b.Register<IFactoryMade>(_ => new FactoryMade(), Lifetime.Singleton).As<IAsyncInitializable>());
            var session = global.CreateScope(b => b.Add<AfterFactoryMade>());

            var run = _tracker.Create(session, "session", _options);

            Assert.That(Status(run, "AfterFactoryMade").Dependencies, Is.EqualTo(new[] { "FactoryMade" }));
        }

        [Test]
        public void AGraphErrorReleasesTheServicesItConstructedAndTheOwnedScope()
        {
            var container = TestScope.Build(b =>
            {
                b.Add<AsyncDisposableService>();
                b.Register<SyncDisposableHolder>(Lifetime.Singleton);
                b.Add<CycleA>();
                b.Add<CycleB>();
            });
            var service = container.Resolve<AsyncDisposableService>();
            var holder = container.Resolve<SyncDisposableHolder>();

            Assert.Throws<InitGraphException>(() => ScopeRun.Create(container, "session", _options, ownsScope: true));

            Assert.That(service.Disposed, Is.EqualTo(1), "IAsyncDisposable services are released by the framework");
            Assert.That(holder.Disposed, Is.True, "the owned scope is disposed, and VContainer disposes its IDisposables");
        }

        public sealed class SyncDisposableHolder : IDisposable
        {
            public bool Disposed { get; private set; }

            public void Dispose() => Disposed = true;
        }

        [Test]
        public void InvalidOptionsAreRejectedBeforeAnythingIsConstructed()
        {
            var container = _tracker.Build(b => b.Add<Auth>());

            _options.CancellationGrace = TimeSpan.FromSeconds(-2);
            var grace = Assert.Throws<InitGraphException>(() => _tracker.Create(container, "session", _options));
            Assert.That(grace!.Message, Does.Contain("RuntimeFlowOptions.CancellationGrace is -2.0s"));

            _options.CancellationGrace = TimeSpan.Zero;
            _options.Phases = new[] { "boot", "ui", "boot" };
            var phases = Assert.Throws<InitGraphException>(() => _tracker.Create(container, "session", _options));
            Assert.That(phases!.Message, Does.Contain("lists phase 'boot' twice"));
        }

        [Init(TimeoutSeconds = 1e12)]
        public sealed class PracticallyForever : AutoService { }

        [Test]
        [Timeout(10000)]
        public async Task AnAstronomicalTimeoutDoesNotOverflow()
        {
            _options.TimeoutMultiplier = 1.0;
            var container = _tracker.Build(b => b.Add<PracticallyForever>());
            var run = _tracker.Create(container, "session", _options);

            var result = await run.RunAsync();

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
        }
    }
}
