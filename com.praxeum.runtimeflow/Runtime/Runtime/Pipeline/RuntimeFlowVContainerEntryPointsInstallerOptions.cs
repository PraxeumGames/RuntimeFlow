using System;
using System.Collections.Generic;
using System.Linq;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.Contexts
{
    public sealed class RuntimeFlowVContainerEntryPointsInstallerOptions
    {
        private readonly HashSet<Type> _managedServiceMarkerTypes = new(RuntimeFlowVContainerEntryPointsSettings.CreateDefaultManagedServiceMarkerTypes());
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
        private readonly HashSet<Type> _managedServiceMarkerTypes = new(RuntimeFlowVContainerEntryPointsSettings.CreateDefaultManagedServiceMarkerTypes());
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

    public sealed class RuntimeFlowVContainerEntryPointsSettings
    {
        internal static readonly RuntimeFlowVContainerEntryPointsSettings Default = new(
            CreateDefaultManagedServiceMarkerTypes(),
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

        internal static Type[] CreateDefaultManagedServiceMarkerTypes()
        {
            return new[]
            {
                typeof(IGlobalInitializableService),
                typeof(ISessionInitializableService),
                typeof(IStartupStageInitializableService)
            };
        }
    }

    public interface IRuntimeFlowSessionSyncEntryPointsBootstrapService : ISessionInitializableService
    {
    }
}
