using NUnit.Framework;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests.Initialization
{
    public sealed class InitializationContractCatalogTests
    {
        [Test]
        public void StartupStageContracts_AreDiscoverableAsyncMarkers()
        {
            var discoverableMarkers = InitializationContractCatalog
                .DiscoverableAsyncInitializationMarkerTypes
                .ToArray();

            foreach (var stageContract in RuntimeFlowStartupStageContracts.Ordered)
            {
                Assert.That(
                    discoverableMarkers,
                    Does.Contain(stageContract.ServiceMarkerType),
                    $"{stageContract.ServiceMarkerType.Name} must be discoverable by RuntimeFlow lifecycle registration.");
            }
        }

        [Test]
        public void MarkerOnlyContracts_AreNotDependencyEdges()
        {
            foreach (var markerType in InitializationContractCatalog.MarkerOnlyAsyncInitializationTypesForDiagnostics)
            {
                Assert.That(
                    InitializationGraphRules.IsExplicitDependencyType(markerType),
                    Is.False,
                    $"{markerType.Name} is a marker-only contract and must not be a dependency edge.");
                Assert.That(
                    InitializationGraphRules.IsAsyncDependencyType(markerType),
                    Is.False,
                    $"{markerType.Name} is a marker-only contract and must not be inferred from constructors.");
            }
        }

        [Test]
        public void ServiceContract_IsDependencyEdge()
        {
            Assert.That(
                InitializationGraphRules.IsExplicitDependencyType(typeof(ICatalogSpecificDependencyService)),
                Is.True);
            Assert.That(
                InitializationGraphRules.IsAsyncDependencyType(typeof(ICatalogSpecificDependencyService)),
                Is.True);
        }

        [Test]
        public void ConcreteServiceImplementation_IsExplicitButNotConstructorDependency()
        {
            Assert.That(
                InitializationGraphRules.IsExplicitDependencyType(typeof(CatalogSpecificDependencyService)),
                Is.True);
            Assert.That(
                InitializationGraphRules.IsAsyncDependencyType(typeof(CatalogSpecificDependencyService)),
                Is.False);
        }

        private interface ICatalogSpecificDependencyService : ISessionInitializableService
        {
        }

        private sealed class CatalogSpecificDependencyService : ICatalogSpecificDependencyService
        {
            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }
    }
}
