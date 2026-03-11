using System;

namespace RuntimeFlow.Contexts
{
    public interface IInitializationProgressNotifier
    {
        void OnScopeStarted(GameContextType scope, int totalServices);
        void OnServiceStarted(GameContextType scope, Type serviceType, int completedServices, int totalServices);
        void OnServiceCompleted(GameContextType scope, Type serviceType, int completedServices, int totalServices);
        void OnScopeCompleted(GameContextType scope, int totalServices);
        void OnServiceProgress(GameContextType scope, Type serviceType, float progress, string? message, int completedServices, int totalServices);
    }
}
