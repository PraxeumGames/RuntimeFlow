using NUnit.Framework;
using System;
using System.Collections.Generic;
using RuntimeFlow.Contexts;
using VContainer;

namespace RuntimeFlow.Tests
{
    public sealed class RegistrationStoreRegressionTests
    {
        private interface IServiceA { }
        private interface IServiceB { }
        private interface IUnrelatedService { }

        private sealed class SharedImplementation : IServiceA, IServiceB
        {
        }

        private sealed class TrackingDisposable : IServiceA, IServiceB, IDisposable
        {
            public int DisposeCount { get; private set; }

            public void Dispose()
            {
                DisposeCount++;
            }
        }

        [Test]
        public void Register_SameImplementation_LastLifetimeWins()
        {
            var store = new GameContextRegistrationStore();
            store.Register(typeof(IServiceA), typeof(SharedImplementation), Lifetime.Singleton);
            store.Register(typeof(IServiceB), typeof(SharedImplementation), Lifetime.Transient);

            var builder = new ContainerBuilder();
            store.ApplyRegistrations(builder);
            var container = builder.Build();
            try
            {
                var a1 = container.Resolve(typeof(IServiceA));
                var a2 = container.Resolve(typeof(IServiceA));
                var b1 = container.Resolve(typeof(IServiceB));

                Assert.That(a1, Is.Not.SameAs(b1), "The last registration (Transient) must win over the earlier Singleton.");
                Assert.That(a2, Is.Not.SameAs(a1), "Transient instances must not be shared.");
            }
            finally
            {
                container.Dispose();
            }
        }

        [Test]
        public void RegisterInstance_ReplacingOwnedInstance_DisposesPreviousInstance()
        {
            var first = new TrackingDisposable();
            var second = new TrackingDisposable();
            var store = new GameContextRegistrationStore();

            store.RegisterInstance(typeof(TrackingDisposable), first, new[] { typeof(IServiceA) }, ownsLifetime: true);
            store.RegisterInstance(typeof(TrackingDisposable), second, new[] { typeof(IServiceB) }, ownsLifetime: true);

            Assert.That(first.DisposeCount, Is.EqualTo(1), "Replaced owned instance must be disposed to avoid a leak.");
            Assert.That(second.DisposeCount, Is.Zero);
        }

        [Test]
        public void RegisterInstance_MixedOwnership_KeepsOwnership()
        {
            var instance = new TrackingDisposable();
            var store = new GameContextRegistrationStore();

            store.RegisterInstance(typeof(TrackingDisposable), instance, new[] { typeof(IServiceA) }, ownsLifetime: true);
            store.RegisterInstance(typeof(TrackingDisposable), instance, new[] { typeof(IServiceB) }, ownsLifetime: false);

            List<Exception>? failures = null;
            store.DisposeOwnedRegisteredInstances(ref failures);

            Assert.That(instance.DisposeCount, Is.EqualTo(1), "Ownership claimed by any registration must be retained.");
            Assert.That(failures, Is.Null);
        }

        [Test]
        public void DisposeOwnedRegisteredInstances_SkipsInstancesResolvedByContainer()
        {
            var instance = new TrackingDisposable();
            var store = new GameContextRegistrationStore();
            store.RegisterInstance(typeof(TrackingDisposable), instance, new[] { typeof(IServiceA) }, ownsLifetime: true);

            var builder = new ContainerBuilder();
            store.ApplyRegistrations(builder);
            var container = builder.Build();
            try
            {
                container.Resolve(typeof(IServiceA));

                List<Exception>? failures = null;
                store.DisposeOwnedRegisteredInstances(ref failures);

                Assert.That(instance.DisposeCount, Is.Zero, "Resolved instances must be left to the container to avoid double-disposal.");

                container.Dispose();

                Assert.That(instance.DisposeCount, Is.EqualTo(1));
            }
            finally
            {
                container.Dispose();
            }
        }

        [Test]
        public void RegisterInstance_ReplacingSpawnedInstance_DoesNotDoubleDispose()
        {
            var first = new TrackingDisposable();
            var second = new TrackingDisposable();
            var store = new GameContextRegistrationStore();
            store.RegisterInstance(typeof(TrackingDisposable), first, new[] { typeof(IServiceA) }, ownsLifetime: true);

            var builder = new ContainerBuilder();
            store.ApplyRegistrations(builder);
            var container = builder.Build();
            try
            {
                container.Resolve(typeof(IServiceA));

                store.RegisterInstance(typeof(TrackingDisposable), second, new[] { typeof(IServiceB) }, ownsLifetime: true);

                Assert.That(first.DisposeCount, Is.Zero, "A container-resolved instance is tracked by the container; manual dispose would double-dispose it.");

                container.Dispose();

                Assert.That(first.DisposeCount, Is.EqualTo(1), "The container disposes the spawned instance exactly once.");
            }
            finally
            {
                container.Dispose();
            }

            List<Exception>? failures = null;
            store.DisposeOwnedRegisteredInstances(ref failures);
            Assert.That(second.DisposeCount, Is.EqualTo(1), "The replacement instance is still owned by the scope.");
            Assert.That(failures, Is.Null);
        }

        [Test]
        public void RegisterInstance_ServiceTypeNotAssignable_ThrowsEagerly()
        {
            var instance = new TrackingDisposable();
            var store = new GameContextRegistrationStore();

            Assert.That(
                () => store.RegisterInstance(
                    typeof(TrackingDisposable),
                    instance,
                    new[] { typeof(IUnrelatedService) },
                    ownsLifetime: true),
                Throws.TypeOf<InvalidOperationException>());
        }
    }
}