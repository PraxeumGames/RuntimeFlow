using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    internal sealed class CompositeInitializationProgressNotifier : IInitializationProgressNotifier, IRuntimeScopeLifecycleProgressNotifier
    {
        private readonly IInitializationProgressNotifier _first;
        private readonly IInitializationProgressNotifier _second;

        public CompositeInitializationProgressNotifier(
            IInitializationProgressNotifier first,
            IInitializationProgressNotifier second)
        {
            _first = first ?? throw new ArgumentNullException(nameof(first));
            _second = second ?? throw new ArgumentNullException(nameof(second));
        }

        public void OnScopeStarted(GameContextType scope, int totalServices)
        {
            _first.OnScopeStarted(scope, totalServices);
            _second.OnScopeStarted(scope, totalServices);
        }

        public void OnServiceStarted(GameContextType scope, Type serviceType, int completedServices, int totalServices)
        {
            _first.OnServiceStarted(scope, serviceType, completedServices, totalServices);
            _second.OnServiceStarted(scope, serviceType, completedServices, totalServices);
        }

        public void OnServiceCompleted(GameContextType scope, Type serviceType, int completedServices, int totalServices)
        {
            _first.OnServiceCompleted(scope, serviceType, completedServices, totalServices);
            _second.OnServiceCompleted(scope, serviceType, completedServices, totalServices);
        }

        public void OnScopeCompleted(GameContextType scope, int totalServices)
        {
            _first.OnScopeCompleted(scope, totalServices);
            _second.OnScopeCompleted(scope, totalServices);
        }

        public void OnServiceProgress(GameContextType scope, Type serviceType, float progress, string? message, int completedServices, int totalServices)
        {
            _first.OnServiceProgress(scope, serviceType, progress, message, completedServices, totalServices);
            _second.OnServiceProgress(scope, serviceType, progress, message, completedServices, totalServices);
        }

        public async Task OnGlobalContextReadyForSessionInitializationAsync(CancellationToken cancellationToken)
        {
            await _first.OnGlobalContextReadyForSessionInitializationAsync(cancellationToken).ConfigureAwait(false);
            await _second.OnGlobalContextReadyForSessionInitializationAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task OnSessionRestartTeardownCompletedAsync(CancellationToken cancellationToken)
        {
            await _first.OnSessionRestartTeardownCompletedAsync(cancellationToken).ConfigureAwait(false);
            await _second.OnSessionRestartTeardownCompletedAsync(cancellationToken).ConfigureAwait(false);
        }

        public void OnScopeActivationStarted(GameContextType scope, int currentStep, int totalSteps)
        {
            if (_first is IRuntimeScopeLifecycleProgressNotifier first)
                first.OnScopeActivationStarted(scope, currentStep, totalSteps);
            if (_second is IRuntimeScopeLifecycleProgressNotifier second)
                second.OnScopeActivationStarted(scope, currentStep, totalSteps);
        }

        public void OnScopeActivationCompleted(GameContextType scope, int currentStep, int totalSteps)
        {
            if (_first is IRuntimeScopeLifecycleProgressNotifier first)
                first.OnScopeActivationCompleted(scope, currentStep, totalSteps);
            if (_second is IRuntimeScopeLifecycleProgressNotifier second)
                second.OnScopeActivationCompleted(scope, currentStep, totalSteps);
        }

        public void OnScopeDeactivationStarted(GameContextType scope)
        {
            if (_first is IRuntimeScopeLifecycleProgressNotifier first)
                first.OnScopeDeactivationStarted(scope);
            if (_second is IRuntimeScopeLifecycleProgressNotifier second)
                second.OnScopeDeactivationStarted(scope);
        }

        public void OnScopeDeactivationCompleted(GameContextType scope)
        {
            if (_first is IRuntimeScopeLifecycleProgressNotifier first)
                first.OnScopeDeactivationCompleted(scope);
            if (_second is IRuntimeScopeLifecycleProgressNotifier second)
                second.OnScopeDeactivationCompleted(scope);
        }
    }
}
