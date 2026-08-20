using NUnit.Framework;
using System;
using RuntimeFlow.Contexts;
using VContainer;

namespace RuntimeFlow.Tests
{
    public sealed class ResolverBackedGameContextTests
    {
        private interface ICountingService { }
        private interface INotRegisteredService { }

        private sealed class CountingService : ICountingService
        {
            public static int Instances;

            public CountingService()
            {
                Instances++;
            }
        }

        [Test]
        public void IsRegistered_DoesNotInstantiateServices()
        {
            CountingService.Instances = 0;
            var builder = new ContainerBuilder();
            builder.Register<CountingService>(Lifetime.Singleton).As<ICountingService>();
            var container = builder.Build();
            try
            {
                var context = new ResolverBackedGameContext(container);

                Assert.That(context.IsRegistered(typeof(ICountingService)), Is.True);
                Assert.That(context.IsRegistered(typeof(INotRegisteredService)), Is.False);
                Assert.That(CountingService.Instances, Is.Zero, "IsRegistered must not construct services.");
            }
            finally
            {
                container.Dispose();
            }
        }

        [Test]
        public void IsRegistered_ExactTypeOnly_WhenInterfaceTypesExcluded()
        {
            CountingService.Instances = 0;
            var builder = new ContainerBuilder();
            builder.Register<CountingService>(Lifetime.Singleton).As<ICountingService>().AsSelf();
            var container = builder.Build();
            try
            {
                var context = new ResolverBackedGameContext(container);

                Assert.That(context.IsRegistered(typeof(ICountingService), includeInterfaceTypes: false), Is.False);
                Assert.That(context.IsRegistered(typeof(CountingService), includeInterfaceTypes: false), Is.True);
            }
            finally
            {
                container.Dispose();
            }
        }
    }
}