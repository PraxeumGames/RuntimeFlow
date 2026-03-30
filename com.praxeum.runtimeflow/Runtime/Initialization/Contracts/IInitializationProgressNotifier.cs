using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    public interface IInitializationProgressNotifier
    {
        void OnScopeStarted(GameContextType scope, int totalServices);
        void OnServiceStarted(GameContextType scope, Type serviceType, int completedServices, int totalServices);
        void OnServiceCompleted(GameContextType scope, Type serviceType, int completedServices, int totalServices);
        void OnScopeCompleted(GameContextType scope, int totalServices);
        void OnServiceProgress(GameContextType scope, Type serviceType, float progress, string? message, int completedServices, int totalServices);
        Task OnGlobalContextReadyForSessionInitializationAsync(CancellationToken cancellationToken);
        Task OnSessionRestartTeardownCompletedAsync(CancellationToken cancellationToken);
    }
}
