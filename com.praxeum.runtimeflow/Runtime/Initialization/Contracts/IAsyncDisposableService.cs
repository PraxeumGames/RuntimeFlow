using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Services implementing this interface receive a disposal callback when their scope is torn down.
    /// Called in reverse initialization order.
    /// </summary>
    public interface IAsyncDisposableService
    {
        Task DisposeAsync(CancellationToken cancellationToken);
    }

    public interface IGlobalDisposableService : IAsyncDisposableService { }
    public interface ISessionDisposableService : IAsyncDisposableService { }
    public interface ISceneDisposableService : IAsyncDisposableService { }
    public interface IModuleDisposableService : IAsyncDisposableService { }
}
