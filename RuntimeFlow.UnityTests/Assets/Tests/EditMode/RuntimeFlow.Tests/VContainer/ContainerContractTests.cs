using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.VContainerIntegration
{
    /// <summary>
    /// The VContainer behaviour RuntimeFlow depends on. If upstream changes any of this, discovery breaks,
    /// so these tests pin the contract rather than the framework.
    /// </summary>
    [TestFixture]
    public sealed class ContainerContractTests
    {
        public sealed class Alpha : AutoService { }

        public sealed class Beta : AutoService { }

        public sealed class Gamma : AutoService { }

        private static List<Registration> Initializables(IObjectResolver scope)
        {
            Assert.That(scope.TryGetRegistration(typeof(IReadOnlyList<IAsyncInitializable>), out var registration), Is.True);
            var provider = registration.Provider as IEnumerable<Registration>;
            Assert.That(provider, Is.Not.Null, "the collection provider must be enumerable");
            return provider!.ToList();
        }

        [Test]
        public void TheInitializableCollectionRegistrationIsEnumerable()
        {
            var container = TestScope.Build(b =>
            {
                b.Add<Alpha>();
                b.Add<Beta>();
            });

            var registrations = Initializables(container);

            Assert.That(registrations.Select(r => r.ImplementationType), Is.EqualTo(new[] { typeof(Alpha), typeof(Beta) }));
            Assert.That(registrations.All(r => r.Lifetime == Lifetime.Singleton), Is.True);
        }

        [Test]
        public void ASingleRegistrationStillEnumerates()
        {
            var container = TestScope.Build(b => b.Add<Alpha>());

            Assert.That(Initializables(container).Select(r => r.ImplementationType), Is.EqualTo(new[] { typeof(Alpha) }));
        }

        [Test]
        public void AnEmptyScopeEnumeratesNothing()
        {
            Assert.That(Initializables(TestScope.Build(_ => { })), Is.Empty);
        }

        [Test]
        public void AChildScopeEnumeratesOnlyItsOwnRegistrations()
        {
            var global = TestScope.Build(b =>
            {
                b.Add<Alpha>();
                b.Add<Beta>();
            });
            var session = global.CreateScope(b => b.Add<Gamma>());

            Assert.That(Initializables(session).Select(r => r.ImplementationType), Is.EqualTo(new[] { typeof(Gamma) }));
        }

        [Test]
        public void ResolvingByRegistrationReturnsTheSharedSingleton()
        {
            var container = TestScope.Build(b => b.Add<Alpha>());
            var registration = Initializables(container).Single();

            var first = container.Resolve(registration);
            var second = container.Resolve<Alpha>();

            Assert.That(first, Is.SameAs(second));
        }

        [Test]
        public void AChildScopeSharesTheParentSingletonInstance()
        {
            var global = TestScope.Build(b => b.Add<Alpha>());
            var session = global.CreateScope(_ => { });

            Assert.That(session.Resolve<Alpha>(), Is.SameAs(global.Resolve<Alpha>()));
        }

        [Test]
        public void AChildScopeReportsItsParent()
        {
            var global = TestScope.Build(_ => { });
            var session = global.CreateScope(_ => { });

            Assert.That(session.Parent, Is.Not.Null);
            Assert.That(session.Parent.TryGetRegistration(typeof(IObjectResolver), out _), Is.True);
        }
    }
}
