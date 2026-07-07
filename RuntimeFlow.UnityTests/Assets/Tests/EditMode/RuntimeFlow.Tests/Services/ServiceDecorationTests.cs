using NUnit.Framework;
using System;
using RuntimeFlow.Contexts;
using VContainer;

namespace RuntimeFlow.Tests
{
    public interface IDecorableService
    {
        string GetValue();
    }

    public class RealDecorableService : IDecorableService
    {
        public string GetValue() => "real";
    }

    public class LoggingDecorator : IDecorableService
    {
        private readonly IDecorableService _inner;
        public LoggingDecorator(IDecorableService inner) { _inner = inner; }
        public string GetValue() => $"logged:{_inner.GetValue()}";
    }

    public class CachingDecorator : IDecorableService
    {
        private readonly IDecorableService _inner;
        public CachingDecorator(IDecorableService inner) { _inner = inner; }
        public string GetValue() => $"cached:{_inner.GetValue()}";
    }

    public interface IExtraDependency
    {
        string Tag { get; }
    }

    public class ExtraDependency : IExtraDependency
    {
        public string Tag => "extra";
    }

    public class DecoratorWithDeps : IDecorableService
    {
        private readonly IDecorableService _inner;
        private readonly IExtraDependency _dep;

        public DecoratorWithDeps(IDecorableService inner, IExtraDependency dep)
        {
            _inner = inner;
            _dep = dep;
        }

        public string GetValue() => $"{_dep.Tag}:{_inner.GetValue()}";
    }

    public class ServiceDecorationTests
    {
        [Test]
        public void Decorate_WrapsService_ResolveReturnsDecorator()
        {
            var context = new GameContext();
            context.Register(typeof(IDecorableService), typeof(RealDecorableService), Lifetime.Singleton);
            context.Decorate(typeof(IDecorableService), typeof(LoggingDecorator));
            context.Initialize();

            var result = context.Resolve<IDecorableService>();

            Assert.That(result, Is.TypeOf<LoggingDecorator>());
            Assert.That(result.GetValue(), Is.EqualTo("logged:real"));
        }

        [Test]
        public void Decorate_ChainedDecorators_AppliedInOrder()
        {
            var context = new GameContext();
            context.Register(typeof(IDecorableService), typeof(RealDecorableService), Lifetime.Singleton);
            context.Decorate(typeof(IDecorableService), typeof(LoggingDecorator));
            context.Decorate(typeof(IDecorableService), typeof(CachingDecorator));
            context.Initialize();

            var result = context.Resolve<IDecorableService>();

            Assert.That(result, Is.TypeOf<CachingDecorator>());
            Assert.That(result.GetValue(), Is.EqualTo("cached:logged:real"));
        }

        [Test]
        public void Decorate_DecoratorWithOtherDependencies_Resolved()
        {
            var context = new GameContext();
            context.Register(typeof(IDecorableService), typeof(RealDecorableService), Lifetime.Singleton);
            context.Register(typeof(IExtraDependency), typeof(ExtraDependency), Lifetime.Singleton);
            context.Decorate(typeof(IDecorableService), typeof(DecoratorWithDeps));
            context.Initialize();

            var result = context.Resolve<IDecorableService>();

            Assert.That(result, Is.TypeOf<DecoratorWithDeps>());
            Assert.That(result.GetValue(), Is.EqualTo("extra:real"));
        }

        [Test]
        public void Decorate_NonExistentService_Throws()
        {
            var context = new GameContext();
            context.Decorate(typeof(IDecorableService), typeof(LoggingDecorator));

            Assert.That(() => context.Initialize(), Throws.TypeOf<InvalidOperationException>());
        }
    }
}
