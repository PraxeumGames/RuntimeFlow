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
}
