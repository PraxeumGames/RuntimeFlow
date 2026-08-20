using NUnit.Framework;
using System;
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
            var context = new GameContext();
            context.Register(typeof(IServiceA), typeof(SharedImplementation), Lifetime.Singleton);
            context.Register(typeof(IServiceB), typeof(SharedImplementation), Lifetime.Transient);
            context.Initialize();
            try
            {
                var a1 = context.Resolve(typeof(IServiceA));
                var a2 = context.Resolve(typeof(IServiceA));
                var b1 = context.Resolve(typeof(IServiceB));

                Assert.That(a1, Is.Not.SameAs(b1), "The last registration (Transient) must win over the earlier Singleton.");
                Assert.That(a2, Is.Not.SameAs(a1), "Transient instances must not be shared.");
            }
            finally
            {
                context.Dispose();
            }
        }

        [Test]
        public void RegisterInstance_ReplacingInstance_DisposesEachOwnedInstanceExactlyOnce()
        {
            var first = new TrackingDisposable();
            var second = new TrackingDisposable();
            var context = new GameContext();
            context.RegisterInstance(typeof(IServiceA), first);
            context.RegisterInstance(typeof(IServiceB), second);
            context.Initialize();
            try
            {
                Assert.That(first.DisposeCount, Is.Zero);
                Assert.That(second.DisposeCount, Is.Zero);
            }
            finally
            {
                context.Dispose();
            }

            Assert.That(first.DisposeCount, Is.EqualTo(1), "Replaced instances are owned by the scope and disposed on teardown.");
            Assert.That(second.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void RegisterInstance_ResolvedByScope_IsDisposedExactlyOnceByScope()
        {
            var instance = new TrackingDisposable();
            var context = new GameContext();
            context.RegisterInstance(typeof(IServiceA), instance);
            context.Initialize();
            try
            {
                Assert.That(context.Resolve(typeof(IServiceA)), Is.SameAs(instance));
            }
            finally
            {
                context.Dispose();
            }

            Assert.That(instance.DisposeCount, Is.EqualTo(1), "The scope is the single owner; no container double-disposes resolved instances.");
        }

        [Test]
        public void RegisterInstance_ServiceTypeNotAssignable_ThrowsEagerly()
        {
            var instance = new TrackingDisposable();
            var context = new GameContext();

            Assert.That(
                () => context.RegisterInstance(typeof(IUnrelatedService), instance),
                Throws.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void RegisterInstance_NotResolved_IsStillDisposedByScope()
        {
            var instance = new TrackingDisposable();
            var context = new GameContext();
            context.RegisterInstance(typeof(IServiceA), instance);
            context.Initialize();
            context.Dispose();

            Assert.That(instance.DisposeCount, Is.EqualTo(1), "Ownership is decided at registration, not at first resolve.");
        }

        [Test]
        public void TypedRegistration_ImplementationType_IsNotExposedByDefault()
        {
            var context = new GameContext();
            context.Register(typeof(IServiceA), typeof(SharedImplementation));
            context.Initialize();
            try
            {
                Assert.That(context.IsRegistered(typeof(SharedImplementation)), Is.False,
                    "Interface-only registration must not expose the implementation type.");
                Assert.That(
                    () => context.Resolve(typeof(SharedImplementation)),
                    Throws.TypeOf<VContainerException>());
            }
            finally
            {
                context.Dispose();
            }
        }
    }
}