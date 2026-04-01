using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    public sealed class NullInitializationProgressNotifier : IInitializationProgressNotifier, IRuntimeScopeLifecycleProgressNotifier
    {
        public static readonly IInitializationProgressNotifier Instance = new NullInitializationProgressNotifier();

        public void OnScopeStarted(GameContextType scope, int totalServices) { }
        public void OnServiceStarted(GameContextType scope, Type serviceType, int completedServices, int totalServices) { }
        public void OnServiceCompleted(GameContextType scope, Type serviceType, int completedServices, int totalServices) { }
        public void OnScopeCompleted(GameContextType scope, int totalServices) { }
        public void OnServiceProgress(GameContextType scope, Type serviceType, float progress, string? message, int completedServices, int totalServices) { }
        public Task OnGlobalContextReadyForSessionInitializationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OnSessionRestartTeardownCompletedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void OnScopeActivationStarted(GameContextType scope, int currentStep, int totalSteps) { }
        public void OnScopeActivationCompleted(GameContextType scope, int currentStep, int totalSteps) { }
        public void OnScopeDeactivationStarted(GameContextType scope) { }
        public void OnScopeDeactivationCompleted(GameContextType scope) { }
    }
}
