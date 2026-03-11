using System;

namespace RuntimeFlow.Contexts
{
    internal sealed class ServiceInitializationContext : IServiceInitializationContext
    {
        private readonly GameContextType _scope;
        private readonly Type _serviceType;
        private readonly IInitializationProgressNotifier _notifier;
        private readonly int _completedServices;
        private readonly int _totalServices;

        public ServiceInitializationContext(
            GameContextType scope,
            Type serviceType,
            IInitializationProgressNotifier notifier,
            int completedServices,
            int totalServices)
        {
            _scope = scope;
            _serviceType = serviceType;
            _notifier = notifier;
            _completedServices = completedServices;
            _totalServices = totalServices;
        }

        public void ReportProgress(float progress, string? message)
        {
            var clamped = Math.Clamp(progress, 0f, 1f);
            _notifier.OnServiceProgress(_scope, _serviceType, clamped, message, _completedServices, _totalServices);
        }
    }
}
