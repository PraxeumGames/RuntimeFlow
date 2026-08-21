using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    public partial class GameContextBuilder
    {
        private Task ExecuteScopeActivationEnterAsync(GameContextType scope, GameContext context, CancellationToken ct)
            => _activationService.ExecuteEnterAsync(scope, context, NullInitializationProgressNotifier.Instance, 0, ct);
        private Task ExecuteScopeActivationEnterAsync(GameContextType scope, GameContext context, IInitializationProgressNotifier n, int totalServices, CancellationToken ct)
            => _activationService.ExecuteEnterAsync(scope, context, n, totalServices, ct);
        private Task ExecuteScopeActivationEnterAsync(GameContextType scope, GameContext context, ScopeActivationExecutionPlan plan, IInitializationProgressNotifier n, int totalServices, CancellationToken ct)
            => _activationService.ExecuteEnterAsync(scope, context, n, totalServices, ct, plan);
        private Task ExecuteScopeActivationExitAsync(GameContextType scope, GameContext context, CancellationToken ct)
            => _activationService.ExecuteExitAsync(scope, context, NullInitializationProgressNotifier.Instance, ct);
        private Task ExecuteScopeActivationExitAsync(GameContextType scope, GameContext context, IInitializationProgressNotifier n, CancellationToken ct)
            => _activationService.ExecuteExitAsync(scope, context, n, ct);
        private Task ExecuteScopeActivationExitAsync(GameContextType scope, GameContext context, ScopeActivationExecutionPlan plan, IInitializationProgressNotifier n, CancellationToken ct)
            => _activationService.ExecuteExitAsync(scope, context, n, ct, plan);

        private ScopeActivationExecutionPlan DiscoverScopeActivationExecutionPlan(GameContextType scope, GameContext context)
            => _activationService.DiscoverPlan(scope, context);
    }
}
