using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VContainer;

namespace RuntimeFlow.Tests.Components
{
    public interface IGraphSubtypeHelper { }

    public sealed class GraphBaseDependency : IAsyncInitializable
    {
        public Task InitializeAsync(InitContext context, CancellationToken token) => Task.CompletedTask;
    }

    public sealed class GraphDerivedDependency : IAsyncInitializable
    {
        public Task InitializeAsync(InitContext context, CancellationToken token) => Task.CompletedTask;
    }

    public sealed class GraphInheritedDependency : IAsyncInitializable
    {
        public Task InitializeAsync(InitContext context, CancellationToken token) => Task.CompletedTask;
    }

    public sealed class GraphDerivedOnlyDependency : IAsyncInitializable
    {
        public Task InitializeAsync(InitContext context, CancellationToken token) => Task.CompletedTask;
    }

    public class GraphAncestorComponent : MonoBehaviour
    {
        [Inject]
        public GraphInheritedDependency InheritedDependency { get; set; } = null!;
    }

    public class GraphBaseComponent : GraphAncestorComponent, IAsyncInitializable, IGraphSubtypeHelper
    {
        [Inject]
        public GraphBaseDependency Dependency { get; set; } = null!;

        public Task InitializeAsync(InitContext context, CancellationToken token) => Task.CompletedTask;
    }

    public sealed class GraphSubtypeComponent : GraphBaseComponent
    {
        [Inject]
        public new GraphDerivedDependency Dependency { get; set; } = null!;

        [Inject]
        public GraphDerivedOnlyDependency DerivedOnlyDependency { get; set; } = null!;
    }
}
