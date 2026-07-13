using System;
using System.Threading;
using NUnit.Framework;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{
    /// <summary>
    /// Covers the health-watchdog exemption for services that block on user/player interaction, applied via the
    /// <see cref="IUserInteractionGatedInitializableService"/> marker.
    /// </summary>
    [TestFixture]
    public sealed class RuntimeHealthSupervisorInteractionGatedTests
    {
        private interface IFakeInteractionGatedService
            : ISessionInitializableService, IUserInteractionGatedInitializableService
        {
        }

        private interface IFakePlainService : ISessionInitializableService
        {
        }

        private static RuntimeHealthSupervisor CreateEnabledSupervisor()
        {
            return RuntimeHealthSupervisor.Create(new RuntimePipelineOptions());
        }

        [Test]
        public void GetServiceTimeout_UserInteractionGatedService_IsInfinite()
        {
            var supervisor = CreateEnabledSupervisor();

            var timeout = supervisor.GetServiceTimeout(GameContextType.Session, typeof(IFakeInteractionGatedService));

            Assert.That(timeout, Is.EqualTo(Timeout.InfiniteTimeSpan),
                "A service implementing IUserInteractionGatedInitializableService blocks on the player for an " +
                "unbounded time and must be exempt from the health watchdog.");
        }

        [Test]
        public void GetServiceTimeout_PlainService_IsBounded()
        {
            var supervisor = CreateEnabledSupervisor();

            var timeout = supervisor.GetServiceTimeout(GameContextType.Session, typeof(IFakePlainService));

            Assert.That(timeout, Is.Not.EqualTo(Timeout.InfiniteTimeSpan),
                "A service that does not block on the player must keep a bounded health timeout.");
            Assert.That(timeout, Is.GreaterThan(TimeSpan.Zero));
        }

        [Test]
        public void GetServiceTimeout_ExplicitOverride_WinsOverInteractionGatedMarker()
        {
            var options = new RuntimePipelineOptions();
            var bound = TimeSpan.FromSeconds(3);
            options.Health.ServiceTimeoutOverrides[typeof(IFakeInteractionGatedService)] = bound;
            var supervisor = RuntimeHealthSupervisor.Create(options);

            var timeout = supervisor.GetServiceTimeout(GameContextType.Session, typeof(IFakeInteractionGatedService));

            Assert.That(timeout, Is.EqualTo(bound),
                "An explicit ServiceTimeoutOverrides entry must take precedence over the marker exemption.");
        }

        [Test]
        public void Marker_IsMarkerOnly_SoMultipleServicesDoNotCollideOnIt()
        {
            Assert.That(
                InitializationContractCatalog.IsMarkerOnlyAsyncInitializationType(
                    typeof(IUserInteractionGatedInitializableService)),
                Is.True,
                "IUserInteractionGatedInitializableService must be marker-only so RuntimeFlow never records a " +
                "service under it; otherwise two interaction-gated services would collide in initializer discovery.");
        }
    }
}
