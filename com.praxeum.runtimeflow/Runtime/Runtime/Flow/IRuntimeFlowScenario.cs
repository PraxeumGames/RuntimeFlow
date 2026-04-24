using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    /// <summary>Defines the top-level runtime flow that orchestrates scene transitions and game startup.</summary>
    public interface IRuntimeFlowScenario
    {
        Task ExecuteAsync(IRuntimeFlowContext context, CancellationToken cancellationToken = default);
    }

    /// <summary>Marker for the framework's restart-aware scene bootstrap scenario contract.</summary>
    public interface IRestartAwareSceneBootstrapScenario : IRuntimeFlowScenario
    {
    }
}
