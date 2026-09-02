using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Graph
{
    /// <summary>Graph problems must be reported before any service is initialized, with actionable text.</summary>
    [TestFixture]
    public sealed class GraphValidationTests
    {
        public interface IMirrorSelector { }

        public sealed class Auth : AutoService { }

        public sealed class Config : AutoService { }

        public sealed class ScopedService : AutoService { }

        [DependsOn(typeof(IMirrorSelector))]
        public sealed class RemoteCatalog : AutoService { }

        [DependsOn(typeof(CycleB))]
        public sealed class CycleA : AutoService { }

        [DependsOn(typeof(CycleA))]
        public sealed class CycleB : AutoService { }

        [Init(UserGated = true, TimeoutSeconds = 5)]
        public sealed class GdprConsent : AutoService { }

        public interface IMissing { }

        public sealed class NeedsMissing : AutoService
        {
            public NeedsMissing(IMissing missing) => Missing = missing;

            public IMissing Missing { get; }
        }

        public sealed class SessionOnly : AutoService { }

        [DependsOn(typeof(SessionOnly))]
        public sealed class GlobalNeedsSession : AutoService { }

        /// <summary>Implements IAsyncInitializable, but a registration may still forget to expose it.</summary>
        public sealed class HiddenInitializable : AutoService { }

        [DependsOn(typeof(HiddenInitializable))]
        public sealed class NeedsHidden : AutoService { }

        public interface IDupA : IAsyncInitializable { }

        public interface IDupB : IAsyncInitializable { }

        public sealed class Dup : AutoService, IDupA, IDupB { }

        private CapturingLogger _log = null!;
        private RuntimeFlowOptions _options = null!;

        private readonly RunTracker _tracker = new RunTracker();

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _options = TestScope.Options(_log);
        }

        /// <summary>Disposes every run and container this fixture created, so nothing leaks into the next test.</summary>
        [TearDown]
        public void DisposeTrackedRuns() => _tracker.DisposeAll();

        [Test]
        public void NonSingletonServiceIsRejected()
        {
            var container = _tracker.Build(b => b.Register<ScopedService>(Lifetime.Scoped).AsImplementedInterfaces());

            var error = Assert.Throws<InitGraphException>(() => _tracker.Create(container, "session", _options));

            Assert.That(error!.Scope, Is.EqualTo("session"));
            Assert.That(error.Message, Is.EqualTo(
                "ScopedService is registered with Lifetime.Scoped in scope 'session'; initializable services must be " +
                "Lifetime.Singleton (a Scoped/Transient service would be re-created uninitialized in child scopes)."));
        }

        [Test]
        public void TransientServiceIsRejectedToo()
        {
            var container = _tracker.Build(b => b.Register<ScopedService>(Lifetime.Transient).AsImplementedInterfaces());

            var error = Assert.Throws<InitGraphException>(() => _tracker.Create(container, "session", _options));

            Assert.That(error!.Scope, Is.EqualTo("session"));
            Assert.That(error.Message, Is.EqualTo(
                "ScopedService is registered with Lifetime.Transient in scope 'session'; initializable services must be " +
                "Lifetime.Singleton (a Scoped/Transient service would be re-created uninitialized in child scopes)."));
        }

        [Test]
        public void TwoRegistrationsOfTheSameImplementationWarn()
        {
            var container = _tracker.Build(b =>
            {
                b.Register<IDupA>(_ => new Dup(), Lifetime.Singleton).As<IAsyncInitializable>();
                b.Register<IDupB>(_ => new Dup(), Lifetime.Singleton).As<IAsyncInitializable>();
            });

            _tracker.Create(container, "session", _options);

            Assert.That(_log.Messages(LogLevel.Warning), Does.Contain(
                "[RuntimeFlow] session: Dup is registered as IAsyncInitializable 2 times; each registration will be initialized."));
        }

        [Test]
        public void UnknownDependsOnTargetListsKnownServices()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<RemoteCatalog>();
                b.Add<Auth>();
                b.Add<Config>();
            });

            var error = Assert.Throws<InitGraphException>(() => _tracker.Create(container, "session", _options));

            Assert.That(error!.Message, Is.EqualTo(
                "RemoteCatalog declares [DependsOn(typeof(IMirrorSelector))], but no initializable service assignable to " +
                "IMirrorSelector is registered in scope 'session' or its parents. A target must implement IAsyncInitializable " +
                "and live in the same scope or a parent scope; a parent scope can never depend on a child scope. " +
                "Known services — session: Auth, Config, RemoteCatalog."));
        }

        [Test]
        public void ADependsOnTargetRegisteredWithoutIAsyncInitializableSaysSo()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<NeedsHidden>();
                b.Register<HiddenInitializable>(Lifetime.Singleton).AsSelf();
            });

            var error = Assert.Throws<InitGraphException>(() => _tracker.Create(container, "session", _options));

            Assert.That(error!.Message, Is.EqualTo(
                "NeedsHidden declares [DependsOn(typeof(HiddenInitializable))]; HiddenInitializable implements " +
                "IAsyncInitializable but is not registered as one, so it is never initialized. Register it with " +
                "RegisterInitializable<T>() or add .As<IAsyncInitializable>() to its registration."));
        }

        [Test]
        public void UnknownDependsOnInAChildScopeGroupsTheKnownServicesByScope()
        {
            var global = _tracker.Build(b => b.Add<Config>());
            var globalRun = _tracker.Create(global, "global", _options);
            var session = _tracker.Track(global.CreateScope(b =>
            {
                b.Add<RemoteCatalog>();
                b.Add<Auth>();
            }));

            var error = Assert.Throws<InitGraphException>(
                () => _tracker.Create(session, "session", _options, new[] { globalRun }));

            Assert.That(error!.Message, Does.EndWith("Known services — session: Auth, RemoteCatalog; global: Config."));
        }

        [Test]
        public void CycleIsReportedWithItsPathAndEdgeOrigins()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<CycleA>();
                b.Add<CycleB>();
            });

            var error = Assert.Throws<InitGraphException>(() => _tracker.Create(container, "session", _options));

            Assert.That(error!.Message, Is.EqualTo(
                "Initialization graph of scope 'session' has a cycle: CycleA -> CycleB -> CycleA. " +
                "Edges: CycleA -> CycleB (DependsOn), CycleB -> CycleA (DependsOn). " +
                "Remove one dependency or take it lazily (Func<T>/ILazy<T> parameters are not edges)."));
        }

        [Test]
        public void UserGatedServiceMayNotDeclareATimeout()
        {
            var container = _tracker.Build(b => b.Add<GdprConsent>());

            var error = Assert.Throws<InitGraphException>(() => _tracker.Create(container, "session", _options));

            Assert.That(error!.Message, Is.EqualTo(
                "GdprConsent is user-gated and declares TimeoutSeconds = 5; user-gated services never time out. Remove one of them."));
        }

        [Test]
        [Timeout(10000)]
        public async Task ConstructionFailureFailsOnlyItsOwnNode()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<NeedsMissing>();
                b.Add<Config>();
            });

            var run = _tracker.Create(container, "session", _options);
            Assert.That(run.GetStatus().Service("NeedsMissing").State, Is.EqualTo(ServiceState.Pending));

            var error = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => run.RunAsync());

            Assert.That(error.Service, Is.EqualTo("NeedsMissing"));
            Assert.That(error.InnerException, Is.TypeOf<InitGraphException>());
            Assert.That(error.InnerException!.Message, Does.StartWith("Could not construct NeedsMissing in scope 'session': "));
            Assert.That(error.InnerException.Message, Does.EndWith("Register the missing type in 'session' or a parent scope."));
            Assert.That(run.GetStatus().Service("NeedsMissing").State, Is.EqualTo(ServiceState.Failed));
        }

        [Test]
        public void AParentScopeCannotDependOnAChildScope()
        {
            var global = _tracker.Build(b => b.Add<GlobalNeedsSession>());
            using (global)
            {
                global.CreateScope(b => b.Add<SessionOnly>());

                var error = Assert.Throws<InitGraphException>(() => _tracker.Create(global, "global", _options));

                Assert.That(error!.Message, Does.Contain("a parent scope can never depend on a child scope"));
                Assert.That(error.Message, Does.Contain("Known services — global: GlobalNeedsSession."));
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task AnEmptyScopeCompletesImmediately()
        {
            var container = _tracker.Build(_ => { });
            var run = _tracker.Create(container, "session", _options);

            var result = await run.RunAsync();

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(run.GetStatus().TotalCount, Is.Zero);
            Assert.That(run.GetStatus().Percent, Is.EqualTo(100.0));
        }
    }
}
