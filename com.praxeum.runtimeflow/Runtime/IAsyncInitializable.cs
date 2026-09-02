using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow
{
    /// <summary>
    /// The single lifecycle contract of RuntimeFlow: a service that needs asynchronous startup work.
    /// Register the implementation with VContainer under this interface and the scope's graph will run it.
    /// </summary>
    public interface IAsyncInitializable
    {
        /// <summary>
        /// Runs the service's startup work. Called once per run, after every dependency has finished.
        /// </summary>
        /// <param name="context">Run metadata plus the halt and progress hooks.</param>
        /// <param name="cancellationToken">Cancelled on failure, halt, restart and disposal; observe it.</param>
        Task InitializeAsync(InitContext context, CancellationToken cancellationToken);
    }
}
