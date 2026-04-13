using System;
using System.Collections.Generic;

namespace RuntimeFlow.Contexts
{
    public sealed class RuntimeFlowLoadingRestartInstallerOptions
    {
        private readonly RuntimeFlowLoadingRestartInstallerOptions? _defaults;

        public RuntimeFlowLoadingRestartInstallerOptions()
        {
        }

        private RuntimeFlowLoadingRestartInstallerOptions(RuntimeFlowLoadingRestartInstallerOptions defaults)
        {
            _defaults = defaults;
        }

        internal static RuntimeFlowLoadingRestartInstallerOptions WithDefaults(RuntimeFlowLoadingRestartInstallerOptions defaults)
        {
            if (defaults == null) throw new ArgumentNullException(nameof(defaults));
            return new RuntimeFlowLoadingRestartInstallerOptions(defaults);
        }

        public bool RegisterLoadingState { get; set; }
        public bool RegisterRestartHandler { get; set; }

        public Type? LoadingStateImplementationType { get; set; }
        public Type? LoadingStateServiceType { get; set; }
        public object? LoadingStateOptionsInstance { get; set; }

        public Type? RestartHandlerImplementationType { get; set; }
        public IReadOnlyCollection<Type>? RestartHandlerServiceTypes { get; set; }

        public IReadOnlyCollection<(Type ImplementationType, IReadOnlyCollection<Type>? ServiceTypes)>? AdditionalRegistrations { get; set; }

        internal bool ResolveRegisterLoadingState(bool fallbackValue) =>
            ResolveBool(RegisterLoadingState, _defaults?.RegisterLoadingState, fallbackValue);

        internal bool ResolveRegisterRestartHandler(bool fallbackValue) =>
            ResolveBool(RegisterRestartHandler, _defaults?.RegisterRestartHandler, fallbackValue);

        internal Type? ResolveLoadingStateImplementationType(Type? fallbackValue) =>
            LoadingStateImplementationType ?? _defaults?.LoadingStateImplementationType ?? fallbackValue;

        internal Type? ResolveLoadingStateServiceType(Type? fallbackValue) =>
            LoadingStateServiceType ?? _defaults?.LoadingStateServiceType ?? fallbackValue;

        internal object? ResolveLoadingStateOptionsInstance() =>
            LoadingStateOptionsInstance ?? _defaults?.LoadingStateOptionsInstance;

        internal Type? ResolveRestartHandlerImplementationType(Type? fallbackValue) =>
            RestartHandlerImplementationType ?? _defaults?.RestartHandlerImplementationType ?? fallbackValue;

        internal IReadOnlyCollection<Type>? ResolveRestartHandlerServiceTypes(IReadOnlyCollection<Type>? fallbackValue) =>
            RestartHandlerServiceTypes ?? _defaults?.RestartHandlerServiceTypes ?? fallbackValue;

        internal IReadOnlyCollection<(Type ImplementationType, IReadOnlyCollection<Type>? ServiceTypes)>? ResolveAdditionalRegistrations(
            IReadOnlyCollection<(Type ImplementationType, IReadOnlyCollection<Type>? ServiceTypes)>? fallbackValue) =>
            AdditionalRegistrations ?? _defaults?.AdditionalRegistrations ?? fallbackValue;

        private static bool ResolveBool(bool value, bool? inherited, bool fallbackValue)
        {
            if (value)
                return true;
            if (inherited.HasValue && inherited.Value)
                return true;
            return fallbackValue;
        }
    }
}
