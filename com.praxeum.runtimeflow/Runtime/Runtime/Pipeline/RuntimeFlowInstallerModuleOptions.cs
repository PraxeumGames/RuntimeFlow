using System;
using System.Collections.Generic;
using System.Linq;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.Contexts
{
    public sealed class RuntimeFlowGlobalInstallerOptions
    {
        public bool EnableLazySingletons { get; set; } = true;
        public bool RegisterPipelineProvider { get; set; } = true;
        public Action<IContainerBuilder>? ConfigureLazySingletons { get; set; }
    }

    public sealed class RuntimeFlowSessionInstallerOptions
    {
        public bool EnableLazySingletons { get; set; } = true;
        public bool RegisterBuildCallbackResolverWarmup { get; set; } = false;
        public Type? BuildCallbackResolverType { get; set; }
        public Action<IObjectResolver>? BuildCallbackResolverAction { get; set; }
        public Action<IContainerBuilder>? ConfigureLazySingletons { get; set; }
    }

    public sealed class RuntimeFlowSessionBootstrapInstallerOptions
    {
        public RuntimeFlowSessionInstallerOptions? SessionInfrastructure { get; set; }
        public RuntimeFlowSessionSyncEntryPointsBootstrapOptions? SessionSyncEntryPointsBootstrap { get; set; }
        public RuntimeFlowLoadingRestartInstallerOptions? LoadingAndRestartInfrastructure { get; set; }
    }

    public sealed class RuntimeFlowGlobalBootstrapPresetOptions
    {
        public RuntimeFlowGlobalInstallerOptions GlobalInfrastructure { get; } = new();
        public RuntimeFlowVContainerEntryPointsInstallerOptions GlobalVContainerEntryPoints { get; } = new();
        public bool RegisterGlobalVContainerEntryPoints { get; set; } = true;

        public RuntimeFlowGlobalBootstrapPresetOptions ConfigureGlobalInfrastructure(
            Action<RuntimeFlowGlobalInstallerOptions> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            configure(GlobalInfrastructure);
            return this;
        }

        public RuntimeFlowGlobalBootstrapPresetOptions ConfigureGlobalVContainerEntryPoints(
            Action<RuntimeFlowVContainerEntryPointsInstallerOptions> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            configure(GlobalVContainerEntryPoints);
            return this;
        }
    }

    public sealed class RuntimeFlowSessionBootstrapPresetOptions
    {
        public RuntimeFlowSessionInstallerOptions SessionInfrastructure { get; } = new();
        public RuntimeFlowSessionSyncEntryPointsBootstrapOptions SessionSyncEntryPointsBootstrap { get; } = new();
        public RuntimeFlowLoadingRestartInstallerOptions LoadingAndRestartInfrastructure { get; } = new();

        public RuntimeFlowSessionBootstrapPresetOptions ConfigureSessionInfrastructure(
            Action<RuntimeFlowSessionInstallerOptions> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            configure(SessionInfrastructure);
            return this;
        }

        public RuntimeFlowSessionBootstrapPresetOptions ConfigureSessionSyncEntryPointsBootstrap(
            Action<RuntimeFlowSessionSyncEntryPointsBootstrapOptions> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            configure(SessionSyncEntryPointsBootstrap);
            return this;
        }

        public RuntimeFlowSessionBootstrapPresetOptions ConfigureLoadingAndRestartInfrastructure(
            Action<RuntimeFlowLoadingRestartInstallerOptions> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            configure(LoadingAndRestartInfrastructure);
            return this;
        }

        internal RuntimeFlowSessionBootstrapInstallerOptions BuildInstallerOptions()
        {
            return new RuntimeFlowSessionBootstrapInstallerOptions
            {
                SessionInfrastructure = SessionInfrastructure,
                SessionSyncEntryPointsBootstrap = SessionSyncEntryPointsBootstrap,
                LoadingAndRestartInfrastructure = LoadingAndRestartInfrastructure
            };
        }
    }

    public sealed class RuntimeFlowVContainerEntryPointsInstallerOptions
    {
        private readonly HashSet<Type> _managedServiceMarkerTypes = new()
        {
            typeof(IGlobalInitializableService),
            typeof(ISessionInitializableService),
            typeof(IStartupStageInitializableService)
        };

        private readonly HashSet<Type> _excludedInitializableImplementationTypes = new();

        public RuntimeFlowVContainerEntryPointsInstallerOptions AddManagedServiceMarker(Type serviceType)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (!serviceType.IsInterface)
                throw new ArgumentException("Managed marker type must be an interface.", nameof(serviceType));

            _managedServiceMarkerTypes.Add(serviceType);
            return this;
        }

        public RuntimeFlowVContainerEntryPointsInstallerOptions AddManagedServiceMarker<TService>()
        {
            return AddManagedServiceMarker(typeof(TService));
        }

        public RuntimeFlowVContainerEntryPointsInstallerOptions ExcludeInitializable(Type implementationType)
        {
            if (implementationType == null) throw new ArgumentNullException(nameof(implementationType));
            _excludedInitializableImplementationTypes.Add(implementationType);
            return this;
        }

        public RuntimeFlowVContainerEntryPointsInstallerOptions ExcludeInitializable<TImplementation>()
        {
            return ExcludeInitializable(typeof(TImplementation));
        }

        internal RuntimeFlowVContainerEntryPointsSettings BuildSettings()
        {
            return new RuntimeFlowVContainerEntryPointsSettings(
                _managedServiceMarkerTypes.ToArray(),
                _excludedInitializableImplementationTypes.ToArray());
        }
    }

    public sealed class RuntimeFlowSessionSyncEntryPointsBootstrapOptions
    {
        private readonly HashSet<Type> _managedServiceMarkerTypes = new()
        {
            typeof(IGlobalInitializableService),
            typeof(ISessionInitializableService),
            typeof(IStartupStageInitializableService)
        };

        private readonly HashSet<Type> _excludedInitializableImplementationTypes = new();
        private readonly List<Type> _prioritizedInitializableImplementationTypes = new();

        public IReadOnlyCollection<Type> ManagedServiceMarkerTypes => _managedServiceMarkerTypes;
        public IReadOnlyCollection<Type> ExcludedInitializableImplementationTypes => _excludedInitializableImplementationTypes;
        public IReadOnlyList<Type> PrioritizedInitializableImplementationTypes => _prioritizedInitializableImplementationTypes;
        public Action<IObjectResolver>? AfterPrioritizedInitializablesInitialized { get; set; }

        public RuntimeFlowSessionSyncEntryPointsBootstrapOptions AddManagedServiceMarker(Type serviceType)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (!serviceType.IsInterface)
                throw new ArgumentException("Managed marker type must be an interface.", nameof(serviceType));

            _managedServiceMarkerTypes.Add(serviceType);
            return this;
        }

        public RuntimeFlowSessionSyncEntryPointsBootstrapOptions AddManagedServiceMarker<TService>()
        {
            return AddManagedServiceMarker(typeof(TService));
        }

        public RuntimeFlowSessionSyncEntryPointsBootstrapOptions ExcludeInitializable(Type implementationType)
        {
            if (implementationType == null) throw new ArgumentNullException(nameof(implementationType));
            _excludedInitializableImplementationTypes.Add(implementationType);
            return this;
        }

        public RuntimeFlowSessionSyncEntryPointsBootstrapOptions ExcludeInitializable<TImplementation>()
        {
            return ExcludeInitializable(typeof(TImplementation));
        }

        public RuntimeFlowSessionSyncEntryPointsBootstrapOptions AddPrioritizedInitializable(Type initializableType)
        {
            if (initializableType == null) throw new ArgumentNullException(nameof(initializableType));
            if (!typeof(IInitializable).IsAssignableFrom(initializableType))
            {
                throw new ArgumentException(
                    $"Type '{initializableType.FullName}' must implement '{typeof(IInitializable).FullName}'.",
                    nameof(initializableType));
            }

            if (_prioritizedInitializableImplementationTypes.Contains(initializableType))
                return this;

            _prioritizedInitializableImplementationTypes.Add(initializableType);
            return this;
        }

        public RuntimeFlowSessionSyncEntryPointsBootstrapOptions AddPrioritizedInitializable<TInitializable>()
            where TInitializable : class, IInitializable
        {
            return AddPrioritizedInitializable(typeof(TInitializable));
        }

        internal RuntimeFlowVContainerEntryPointsSettings BuildSettings()
        {
            var excludedTypes = new HashSet<Type>(_excludedInitializableImplementationTypes);
            foreach (var prioritizedType in _prioritizedInitializableImplementationTypes)
            {
                excludedTypes.Add(prioritizedType);
            }

            return new RuntimeFlowVContainerEntryPointsSettings(
                _managedServiceMarkerTypes.ToArray(),
                excludedTypes.ToArray(),
                _prioritizedInitializableImplementationTypes.ToArray(),
                AfterPrioritizedInitializablesInitialized);
        }
    }

    public sealed class RuntimeFlowLoadingRestartInstallerOptions
    {
        private readonly RuntimeFlowLoadingRestartInstallerOptions _defaults;

        public RuntimeFlowLoadingRestartInstallerOptions()
        {
            _defaults = null;
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

    public sealed class RuntimeFlowVContainerEntryPointsSettings
    {
        internal static readonly RuntimeFlowVContainerEntryPointsSettings Default = new(
            new[]
            {
                typeof(IGlobalInitializableService),
                typeof(ISessionInitializableService),
                typeof(IStartupStageInitializableService)
            },
            Array.Empty<Type>(),
            Array.Empty<Type>(),
            null);

        public RuntimeFlowVContainerEntryPointsSettings(
            IReadOnlyCollection<Type> managedServiceMarkerTypes,
            IReadOnlyCollection<Type> excludedInitializableImplementationTypes,
            IReadOnlyCollection<Type>? prioritizedInitializableImplementationTypes = null,
            Action<IObjectResolver>? afterPrioritizedInitializablesInitialized = null)
        {
            ManagedServiceMarkerTypes = managedServiceMarkerTypes ?? throw new ArgumentNullException(nameof(managedServiceMarkerTypes));
            ExcludedInitializableImplementationTypes = excludedInitializableImplementationTypes
                ?? throw new ArgumentNullException(nameof(excludedInitializableImplementationTypes));
            PrioritizedInitializableImplementationTypes = prioritizedInitializableImplementationTypes ?? Array.Empty<Type>();
            AfterPrioritizedInitializablesInitialized = afterPrioritizedInitializablesInitialized;
        }

        public IReadOnlyCollection<Type> ManagedServiceMarkerTypes { get; }
        public IReadOnlyCollection<Type> ExcludedInitializableImplementationTypes { get; }
        public IReadOnlyCollection<Type> PrioritizedInitializableImplementationTypes { get; }
        public Action<IObjectResolver>? AfterPrioritizedInitializablesInitialized { get; }
    }

    public interface IRuntimeFlowSessionSyncEntryPointsBootstrapService : ISessionInitializableService
    {
    }
}
