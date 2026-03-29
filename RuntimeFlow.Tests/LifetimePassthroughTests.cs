using System.Collections;
using System.Reflection;
using RuntimeFlow.Contexts;
using VContainer;

namespace RuntimeFlow.Tests;

public interface ILifetimeTestService { }

public class TransientTestService : ILifetimeTestService { }

public class LifetimePassthroughTests
{
    [Fact]
    public void Register_WithTransientLifetime_ResolvesDifferentInstances()
    {
        var context = new GameContext();
        context.Register(typeof(ILifetimeTestService), typeof(TransientTestService), Lifetime.Transient);
        context.Initialize();

        var first = context.Resolve<ILifetimeTestService>();
        var second = context.Resolve<ILifetimeTestService>();

        Assert.NotSame(first, second);
    }

    [Fact]
    public void Register_WithSingletonLifetime_ResolvesSameInstance()
    {
        var context = new GameContext();
        context.Register(typeof(ILifetimeTestService), typeof(TransientTestService), Lifetime.Singleton);
        context.Initialize();

        var first = context.Resolve<ILifetimeTestService>();
        var second = context.Resolve<ILifetimeTestService>();

        Assert.Same(first, second);
    }

    [Fact]
    public void Register_TwoParamOverload_DefaultsToSingleton()
    {
        var context = new GameContext();
        context.Register(typeof(ILifetimeTestService), typeof(TransientTestService));
        context.Initialize();

        var first = context.Resolve<ILifetimeTestService>();
        var second = context.Resolve<ILifetimeTestService>();

        Assert.Same(first, second);
    }

    [Fact]
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
        Assert.Single(providers);

        var provider = providers[0]!;
        var spawnMethod = provider.GetType().GetMethod(nameof(VContainer.IInstanceProvider.SpawnInstance))!;

        Assert.Same(instance, spawnMethod.Invoke(provider, new object[] { resolver }));

        context.Dispose();

        var exception = Assert.Throws<TargetInvocationException>(() => spawnMethod.Invoke(provider, new object[] { resolver }));
        Assert.IsType<ObjectDisposedException>(exception.InnerException);
    }
}
