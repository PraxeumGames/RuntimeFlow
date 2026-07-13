using System;
using System.Collections.Generic;
using System.Linq;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.Contexts
{
    public sealed class RuntimeFlowVContainerEntryPointsInstallerOptions
    {
        private readonly HashSet<Type> _excludedInitializableImplementationTypes = new();
        private readonly HashSet<Type> _excludedStartableImplementationTypes = new();

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

        public RuntimeFlowVContainerEntryPointsInstallerOptions ExcludeStartable(Type implementationType)
        {
            if (implementationType == null) throw new ArgumentNullException(nameof(implementationType));
            _excludedStartableImplementationTypes.Add(implementationType);
            return this;
        }

        public RuntimeFlowVContainerEntryPointsInstallerOptions ExcludeStartable<TImplementation>()
        {
            return ExcludeStartable(typeof(TImplementation));
        }

        internal RuntimeFlowVContainerEntryPointsSettings BuildSettings()
        {
            return new RuntimeFlowVContainerEntryPointsSettings(
                _excludedInitializableImplementationTypes.ToArray(),
                _excludedStartableImplementationTypes.ToArray(),
                Array.Empty<Type>(),
                null);
        }
    }

    public sealed class RuntimeFlowSessionVContainerEntryPointsInstallerOptions
    {
        private readonly HashSet<Type> _excludedInitializableImplementationTypes = new();
        private readonly HashSet<Type> _excludedStartableImplementationTypes = new();
        private readonly List<Type> _prioritizedInitializableImplementationTypes = new();

        public IReadOnlyCollection<Type> ExcludedInitializableImplementationTypes => _excludedInitializableImplementationTypes;
        public IReadOnlyCollection<Type> ExcludedStartableImplementationTypes => _excludedStartableImplementationTypes;
        public IReadOnlyList<Type> PrioritizedInitializableImplementationTypes => _prioritizedInitializableImplementationTypes;
        public Action<IObjectResolver>? AfterPrioritizedInitializablesInitialized { get; set; }

        public RuntimeFlowSessionVContainerEntryPointsInstallerOptions ExcludeInitializable(Type implementationType)
        {
            if (implementationType == null) throw new ArgumentNullException(nameof(implementationType));
            _excludedInitializableImplementationTypes.Add(implementationType);
            return this;
        }

        public RuntimeFlowSessionVContainerEntryPointsInstallerOptions ExcludeInitializable<TImplementation>()
        {
            return ExcludeInitializable(typeof(TImplementation));
        }

        public RuntimeFlowSessionVContainerEntryPointsInstallerOptions ExcludeStartable(Type implementationType)
        {
            if (implementationType == null) throw new ArgumentNullException(nameof(implementationType));
            _excludedStartableImplementationTypes.Add(implementationType);
            return this;
        }

        public RuntimeFlowSessionVContainerEntryPointsInstallerOptions ExcludeStartable<TImplementation>()
        {
            return ExcludeStartable(typeof(TImplementation));
        }

        public RuntimeFlowSessionVContainerEntryPointsInstallerOptions AddPrioritizedInitializable(Type initializableType)
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

        public RuntimeFlowSessionVContainerEntryPointsInstallerOptions AddPrioritizedInitializable<TInitializable>()
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
                excludedTypes.ToArray(),
                _excludedStartableImplementationTypes.ToArray(),
                _prioritizedInitializableImplementationTypes.ToArray(),
                AfterPrioritizedInitializablesInitialized);
        }
    }

    public sealed class RuntimeFlowVContainerEntryPointsSettings
    {
        internal static readonly RuntimeFlowVContainerEntryPointsSettings Default = new(
            Array.Empty<Type>(),
            Array.Empty<Type>(),
            Array.Empty<Type>(),
            null);

        public RuntimeFlowVContainerEntryPointsSettings(
            IReadOnlyCollection<Type> excludedInitializableImplementationTypes,
            IReadOnlyCollection<Type>? prioritizedInitializableImplementationTypes = null,
            Action<IObjectResolver>? afterPrioritizedInitializablesInitialized = null)
            : this(
                excludedInitializableImplementationTypes,
                Array.Empty<Type>(),
                prioritizedInitializableImplementationTypes,
                afterPrioritizedInitializablesInitialized)
        {
        }

        public RuntimeFlowVContainerEntryPointsSettings(
            IReadOnlyCollection<Type> excludedInitializableImplementationTypes,
            IReadOnlyCollection<Type>? excludedStartableImplementationTypes,
            IReadOnlyCollection<Type>? prioritizedInitializableImplementationTypes,
            Action<IObjectResolver>? afterPrioritizedInitializablesInitialized)
        {
            ExcludedInitializableImplementationTypes = excludedInitializableImplementationTypes
                ?? throw new ArgumentNullException(nameof(excludedInitializableImplementationTypes));
            ExcludedStartableImplementationTypes = excludedStartableImplementationTypes ?? Array.Empty<Type>();
            PrioritizedInitializableImplementationTypes = prioritizedInitializableImplementationTypes ?? Array.Empty<Type>();
            AfterPrioritizedInitializablesInitialized = afterPrioritizedInitializablesInitialized;
        }

        public IReadOnlyCollection<Type> ExcludedInitializableImplementationTypes { get; }
        public IReadOnlyCollection<Type> ExcludedStartableImplementationTypes { get; }
        public IReadOnlyCollection<Type> PrioritizedInitializableImplementationTypes { get; }
        public Action<IObjectResolver>? AfterPrioritizedInitializablesInitialized { get; }
    }

    public sealed class RuntimeFlowVContainerEntryPointsSettingsContribution
    {
        public RuntimeFlowVContainerEntryPointsSettingsContribution(
            IReadOnlyCollection<Type> excludedInitializableImplementationTypes,
            IReadOnlyCollection<Type>? excludedStartableImplementationTypes = null)
        {
            ExcludedInitializableImplementationTypes = excludedInitializableImplementationTypes
                ?? throw new ArgumentNullException(nameof(excludedInitializableImplementationTypes));
            ExcludedStartableImplementationTypes = excludedStartableImplementationTypes ?? Array.Empty<Type>();
        }

        public IReadOnlyCollection<Type> ExcludedInitializableImplementationTypes { get; }
        public IReadOnlyCollection<Type> ExcludedStartableImplementationTypes { get; }
    }
}
