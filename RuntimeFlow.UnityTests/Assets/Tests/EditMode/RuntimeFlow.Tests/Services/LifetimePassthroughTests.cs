using NUnit.Framework;
using System;
using System.Collections;
using System.Reflection;
using RuntimeFlow.Contexts;
using VContainer;

namespace RuntimeFlow.Tests
{
    public interface ILifetimeTestService { }

    public class TransientTestService : ILifetimeTestService { }

    public class LifetimePassthroughTests
    {
        [Test]
        public void Register_WithTransientLifetime_ResolvesDifferentInstances()
        {
            var context = new GameContext();
            context.Register(typeof(ILifetimeTestService), typeof(TransientTestService), Lifetime.Transient);
            context.Initialize();

            var first = context.Resolve<ILifetimeTestService>();
            var second = context.Resolve<ILifetimeTestService>();

            Assert.That(first, Is.Not.SameAs(second));
        }

        [Test]
        public void Register_WithSingletonLifetime_ResolvesSameInstance()
        {
            var context = new GameContext();
            context.Register(typeof(ILifetimeTestService), typeof(TransientTestService), Lifetime.Singleton);
            context.Initialize();

            var first = context.Resolve<ILifetimeTestService>();
            var second = context.Resolve<ILifetimeTestService>();

            Assert.That(first, Is.SameAs(second));
        }

        [Test]
        public void Register_TwoParamOverload_DefaultsToSingleton()
        {
            var context = new GameContext();
            context.Register(typeof(ILifetimeTestService), typeof(TransientTestService));
            context.Initialize();

            var first = context.Resolve<ILifetimeTestService>();
            var second = context.Resolve<ILifetimeTestService>();

            Assert.That(first, Is.SameAs(second));
        }

        [Test]
        public void RegisterInstance_ReleasesProviderWhenContextDisposed()
        {
            var context = new GameContext();
            var instance = new TransientTestService();
            context.RegisterInstance<ILifetimeTestService>(instance);
            context.Initialize();

            var resolver = context.Resolver;
            var providers = (IList)typeof(GameContext)
                .GetField("_instanceProviders", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(context)!;
            Assert.That(providers, Has.Count.EqualTo(1));

            var provider = providers[0]!;
            var spawnMethod = provider.GetType().GetMethod(nameof(VContainer.IInstanceProvider.SpawnInstance))!;

            Assert.That(spawnMethod.Invoke(provider, new object[] { resolver }), Is.SameAs(instance));

            context.Dispose();

            Assert.That(
                () => spawnMethod.Invoke(provider, new object[] { resolver }),
                Throws.TypeOf<TargetInvocationException>()
                    .With.InnerException.TypeOf<ObjectDisposedException>());
        }
    }
}
