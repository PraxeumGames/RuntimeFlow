using NUnit.Framework;
using System;
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
        public void RegisterInstance_OwnedInstance_DisposedOnceByScopeAndUnresolvableAfterDispose()
        {
            var context = new GameContext();
            var instance = new TransientTestService();
            context.RegisterInstance<ILifetimeTestService>(instance);
            context.Initialize();

            var resolver = context.Resolver;
            Assert.That(resolver, Is.SameAs(context), "The context is its own resolver.");

            Assert.That(context.Resolve<ILifetimeTestService>(), Is.SameAs(instance));

            context.Dispose();

            Assert.That(
                () => context.Resolve<ILifetimeTestService>(),
                Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("Context not initialized"));
            Assert.That(resolver.TryResolve(typeof(ILifetimeTestService), out _), Is.False,
                "Resolution must be impossible after the context is disposed.");
        }
    }
}
