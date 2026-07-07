using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    internal sealed class CompositeInitializationProgressNotifier :
        IInitializationProgressNotifier,
        IRuntimeScopeLifecycleProgressNotifier,
        IStartupOperationProgressNotifier
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

        public void OnStartupOperationStarted(
            GameContextType scope,
            string phase,
            string operationName,
            int completedOperations,
            int totalOperations,
            TimeSpan elapsed)
        {
            if (_first is IStartupOperationProgressNotifier first)
                first.OnStartupOperationStarted(scope, phase, operationName, completedOperations, totalOperations, elapsed);
            if (_second is IStartupOperationProgressNotifier second)
                second.OnStartupOperationStarted(scope, phase, operationName, completedOperations, totalOperations, elapsed);
        }

        public void OnStartupOperationStep(
            GameContextType scope,
            string phase,
            string operationName,
            string step,
            string? detail,
            int completedOperations,
            int totalOperations,
            TimeSpan elapsed)
        {
            if (_first is IStartupOperationProgressNotifier first)
                first.OnStartupOperationStep(scope, phase, operationName, step, detail, completedOperations, totalOperations, elapsed);
            if (_second is IStartupOperationProgressNotifier second)
                second.OnStartupOperationStep(scope, phase, operationName, step, detail, completedOperations, totalOperations, elapsed);
        }

        public void OnStartupOperationCompleted(
            GameContextType scope,
            string phase,
            string operationName,
            int completedOperations,
            int totalOperations,
            TimeSpan elapsed)
        {
            if (_first is IStartupOperationProgressNotifier first)
                first.OnStartupOperationCompleted(scope, phase, operationName, completedOperations, totalOperations, elapsed);
            if (_second is IStartupOperationProgressNotifier second)
                second.OnStartupOperationCompleted(scope, phase, operationName, completedOperations, totalOperations, elapsed);
        }

        public void OnStartupOperationFailed(
            GameContextType scope,
            string phase,
            string operationName,
            string? step,
            string? detail,
            Exception exception,
            int completedOperations,
            int totalOperations,
            TimeSpan elapsed)
        {
            if (_first is IStartupOperationProgressNotifier first)
                first.OnStartupOperationFailed(scope, phase, operationName, step, detail, exception, completedOperations, totalOperations, elapsed);
            if (_second is IStartupOperationProgressNotifier second)
                second.OnStartupOperationFailed(scope, phase, operationName, step, detail, exception, completedOperations, totalOperations, elapsed);
        }
    }
}
