using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Optional extension of <see cref="IAsyncInitializableService"/>. Services implementing this interface
    /// receive an <see cref="IServiceInitializationContext"/> for reporting sub-progress during initialization.
    /// </summary>
    public interface IProgressAwareInitializableService : IAsyncInitializableService
    {
        Task InitializeAsync(IServiceInitializationContext context, CancellationToken cancellationToken);
    }
}
