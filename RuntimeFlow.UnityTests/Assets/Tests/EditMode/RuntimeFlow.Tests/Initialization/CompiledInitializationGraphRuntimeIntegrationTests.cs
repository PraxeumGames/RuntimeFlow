using NUnit.Framework;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using VContainer;

namespace RuntimeFlow.Contexts.Generated
{
    internal static class CompiledInitializationGraph
    {
        internal const string RuleVersion = "compiled-explicit-dependencies-v4";

        internal sealed class Node
        {
            internal Node(System.Type serviceType, System.Type implementationType, GameContextType scope, System.Type[] dependencies)
            {
                ServiceType = serviceType;
                ImplementationType = implementationType;
                Scope = scope;
                Dependencies = dependencies;
            }

            internal System.Type ServiceType { get; }
            internal System.Type ImplementationType { get; }
            internal GameContextType Scope { get; }
            internal System.Type[] Dependencies { get; }
        }

        internal static readonly Node[] Nodes =
        {
            new(
                typeof(RuntimeFlow.Tests.Initialization.IZzzGeneratedGraphPrerequisiteService),
                typeof(RuntimeFlow.Tests.Initialization.ZzzGeneratedGraphPrerequisiteService),
                GameContextType.Session,
                System.Array.Empty<System.Type>()),
            new(
                typeof(RuntimeFlow.Tests.Initialization.IAaaGeneratedGraphDependentService),
                typeof(RuntimeFlow.Tests.Initialization.AaaGeneratedGraphDependentService),
                GameContextType.Session,
                new[] { typeof(RuntimeFlow.Tests.Initialization.IZzzGeneratedGraphPrerequisiteService) })
        };
    }
}

namespace RuntimeFlow.Tests.Initialization
{
    public sealed class CompiledInitializationGraphRuntimeIntegrationTests
    {
        [Test]
        public async Task RuntimeDiscovery_UsesGeneratedCompiledGraphDependencies()
        {
            var events = new List<string>();
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineGlobalScope();
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    containerBuilder.RegisterInstance(events);
                    containerBuilder.Register<AaaGeneratedGraphDependentService>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                    containerBuilder.Register<ZzzGeneratedGraphPrerequisiteService>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                });
            });

            await pipeline.InitializeAsync();

            Assert.That(events, Is.EqualTo(new[] { "prerequisite", "dependent" }));
        }
    }

    public interface IAaaGeneratedGraphDependentService : ISessionInitializableService
    {
    }

    public interface IZzzGeneratedGraphPrerequisiteService : ISessionInitializableService
    {
    }

    public sealed class AaaGeneratedGraphDependentService : IAaaGeneratedGraphDependentService
    {
        private readonly List<string> _events;

        public AaaGeneratedGraphDependentService(List<string> events)
        {
            _events = events;
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            _events.Add("dependent");
            return Task.CompletedTask;
        }
    }

    public sealed class ZzzGeneratedGraphPrerequisiteService : IZzzGeneratedGraphPrerequisiteService
    {
        private readonly List<string> _events;

        public ZzzGeneratedGraphPrerequisiteService(List<string> events)
        {
            _events = events;
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            _events.Add("prerequisite");
            return Task.CompletedTask;
        }
    }
}
