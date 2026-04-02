using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.Contexts
{
    public static class RuntimeFlowPrioritizedInitializableResolver
    {
        public static IInitializable ResolvePrioritizedInitializable(
            IObjectResolver resolver,
            Type initializableType,
            ILogger? logger = null)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            if (initializableType == null) throw new ArgumentNullException(nameof(initializableType));
            if (!typeof(IInitializable).IsAssignableFrom(initializableType))
            {
                throw new ArgumentException(
                    $"Type '{initializableType.FullName}' must implement '{typeof(IInitializable).FullName}'.",
                    nameof(initializableType));
            }

            if (resolver.TryResolve(initializableType, out var resolvedInstance)
                && resolvedInstance is IInitializable directlyResolved)
            {
                return directlyResolved;
            }

            if (TryResolveFromInitializableRegistrations(resolver, initializableType, out var resolvedFromRegistrations))
            {
                return resolvedFromRegistrations;
            }

            if (resolvedInstance == null)
            {
                (logger ?? NullLogger.Instance).LogError(
                    "Prioritized initializable {Type} is not resolvable via direct registration or entry point registrations.",
                    initializableType.FullName ?? initializableType.Name);
                throw new InvalidOperationException(
                    $"Prioritized initializable '{initializableType.FullName}' is not registered as a resolvable service.");
            }

            throw new InvalidOperationException(
                $"Resolved initializable '{initializableType.FullName}' does not implement '{typeof(IInitializable).FullName}'.");
        }

        public static T ResolvePrioritizedInitializable<T>(
            IObjectResolver resolver,
            ILogger? logger = null)
            where T : class, IInitializable
        {
            return (T)ResolvePrioritizedInitializable(resolver, typeof(T), logger);
        }

        private static bool TryResolveFromInitializableRegistrations(
            IObjectResolver resolver,
            Type initializableType,
            out IInitializable resolvedInitializable)
        {
            resolvedInitializable = null!;
            var collectionType = typeof(IReadOnlyList<IInitializable>);
            if (!resolver.TryGetRegistration(collectionType, out var registration) || registration?.Provider == null)
                return false;

            if (registration.Provider is not IEnumerable registrations)
                return false;

            foreach (var entryPointRegistration in registrations.Cast<object>().OfType<Registration>())
            {
                var implementationType = entryPointRegistration.ImplementationType;
                if (implementationType == null || implementationType != initializableType)
                    continue;

                if (resolver.Resolve(entryPointRegistration) is not IInitializable initializable)
                    continue;

                resolvedInitializable = initializable;
                return true;
            }

            return false;
        }
    }

    public abstract class RuntimeFlowVContainerEntryPointsInitializationService
    {
        private readonly IObjectResolver _resolver;
        private readonly string _scopeName;
        private readonly ILogger _logger;
        private readonly RuntimeFlowVContainerEntryPointsSettings _settings;

        protected RuntimeFlowVContainerEntryPointsInitializationService(
            IObjectResolver resolver,
            string scopeName,
            ILogger logger,
            RuntimeFlowVContainerEntryPointsSettings? settings = null)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _scopeName = scopeName ?? throw new ArgumentNullException(nameof(scopeName));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? RuntimeFlowVContainerEntryPointsSettings.Default;
        }

        protected Task InitializeVContainerEntryPointsAsync(CancellationToken cancellationToken)
        {
            InitializePrioritizedInitializables(cancellationToken);

            var resolver = GetEntryPointResolver(_resolver);
            var initializableRegistrations = GetScopeLocalRegistrations<IInitializable>(resolver);
            var startableRegistrations = GetScopeLocalRegistrations<IStartable>(resolver);
            _logger.LogInformation(
                "Resolving VContainer entry points for {Scope} scope. Resolver={ResolverType}, Initializables={InitializableRegistrations}, Startables={StartableRegistrations}",
                _scopeName,
                resolver.GetType().FullName ?? resolver.GetType().Name,
                DescribeRegistrations(initializableRegistrations),
                DescribeRegistrations(startableRegistrations));

            _logger.LogInformation(
                "Initializing VContainer entry points for {Scope} scope. Initializables={Initializables}, Startables={Startables}",
                _scopeName,
                initializableRegistrations.Count,
                startableRegistrations.Count);

            InitializeInitializables(resolver, initializableRegistrations, cancellationToken);

            foreach (var registration in startableRegistrations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (resolver.Resolve(registration) is not IStartable startable)
                {
                    throw new InvalidOperationException(
                        $"Resolved entry point '{registration.ImplementationType?.FullName ?? registration.ImplementationType?.Name ?? "<unknown>"}' " +
                        $"does not implement '{typeof(IStartable).FullName}'.");
                }

                startable.Start();
            }

            return Task.CompletedTask;
        }

        protected virtual IObjectResolver GetEntryPointResolver(IObjectResolver resolver) => resolver;

        private void InitializePrioritizedInitializables(CancellationToken cancellationToken)
        {
            foreach (var initializableType in _settings.PrioritizedInitializableImplementationTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var initializable = RuntimeFlowPrioritizedInitializableResolver.ResolvePrioritizedInitializable(
                    _resolver,
                    initializableType,
                    _logger);
                initializable.Initialize();
            }

            _settings.AfterPrioritizedInitializablesInitialized?.Invoke(_resolver);
        }

        private void InitializeInitializables(
            IObjectResolver resolver,
            IReadOnlyList<Registration> initializableRegistrations,
            CancellationToken cancellationToken)
        {
            if (string.Equals(_scopeName, "session", StringComparison.OrdinalIgnoreCase))
            {
                InitializeSessionInitializablesByStages(resolver, initializableRegistrations, cancellationToken);
                return;
            }

            var initializedInstances = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var registration in initializableRegistrations)
            {
                InitializeRegistration(resolver, registration, initializedInstances, cancellationToken);
            }
        }

        private void InitializeSessionInitializablesByStages(
            IObjectResolver resolver,
            IReadOnlyList<Registration> initializableRegistrations,
            CancellationToken cancellationToken)
        {
            if (initializableRegistrations.Count == 0)
                return;

            var initializedInstances = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var initializedImplementationTypes = new HashSet<Type>();
            var stagedRegistrationSet = new HashSet<Registration>();

            _logger.LogInformation(
                "Initializing staged startup entry points for {Scope} scope. Stages={StageCount}",
                _scopeName,
                RuntimeFlowStartupStageContracts.Ordered.Count);

            foreach (var stageContract in RuntimeFlowStartupStageContracts.Ordered)
            {
                var stageRegistrations = initializableRegistrations
                    .Where(registration => IsStageRegistration(registration, stageContract.ServiceMarkerType))
                    .ToArray();

                _logger.LogInformation(
                    "Startup stage {Stage} begin for {Scope} scope. Candidates={Count}",
                    stageContract.Stage,
                    _scopeName,
                    stageRegistrations.Length);

                var initializedInStage = 0;
                foreach (var registration in stageRegistrations)
                {
                    stagedRegistrationSet.Add(registration);
                    if (TryInitializeRegistration(
                            resolver,
                            registration,
                            initializedInstances,
                            initializedImplementationTypes,
                            cancellationToken))
                    {
                        initializedInStage++;
                    }
                }

                _logger.LogInformation(
                    "Startup stage {Stage} end for {Scope} scope. Initialized={Initialized}, Candidates={Count}",
                    stageContract.Stage,
                    _scopeName,
                    initializedInStage,
                    stageRegistrations.Length);
            }

            var remainingRegistrations = initializableRegistrations
                .Where(registration => !stagedRegistrationSet.Contains(registration))
                .ToArray();

            _logger.LogInformation(
                "Startup non-staged begin for {Scope} scope. Candidates={Count}",
                _scopeName,
                remainingRegistrations.Length);

            var initializedNonStaged = 0;
            foreach (var registration in remainingRegistrations)
            {
                if (TryInitializeRegistration(
                        resolver,
                        registration,
                        initializedInstances,
                        initializedImplementationTypes,
                        cancellationToken))
                {
                    initializedNonStaged++;
                }
            }

            _logger.LogInformation(
                "Startup non-staged end for {Scope} scope. Initialized={Initialized}, Candidates={Count}",
                _scopeName,
                initializedNonStaged,
                remainingRegistrations.Length);
        }

        private static bool IsStageRegistration(Registration registration, Type stageMarkerType)
        {
            if (registration?.ImplementationType == null || stageMarkerType == null)
                return false;

            return stageMarkerType.IsAssignableFrom(registration.ImplementationType);
        }

        private static IInitializable ResolveInitializableOrThrow(IObjectResolver resolver, Registration registration)
        {
            if (resolver.Resolve(registration) is IInitializable initializable)
                return initializable;

            throw new InvalidOperationException(
                $"Resolved entry point '{registration.ImplementationType?.FullName ?? registration.ImplementationType?.Name ?? "<unknown>"}' " +
                $"does not implement '{typeof(IInitializable).FullName}'.");
        }

        private static void InitializeRegistration(
            IObjectResolver resolver,
            Registration registration,
            ISet<object> initializedInstances,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var initializable = ResolveInitializableOrThrow(resolver, registration);
            if (!initializedInstances.Add(initializable))
                return;

            initializable.Initialize();
        }

        private static bool TryInitializeRegistration(
            IObjectResolver resolver,
            Registration registration,
            ISet<object> initializedInstances,
            ISet<Type> initializedImplementationTypes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var initializable = ResolveInitializableOrThrow(resolver, registration);
            var implementationType = registration.ImplementationType ?? initializable.GetType();

            if (!initializedImplementationTypes.Add(implementationType))
                return false;

            if (!initializedInstances.Add(initializable))
                return false;

            initializable.Initialize();
            return true;
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new();

            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private static string DescribeRegistrations(IReadOnlyCollection<Registration> registrations)
        {
            var names = registrations
                .Select(registration => registration.ImplementationType)
                .Where(type => type != null)
                .Select(type => type!.FullName ?? type.Name)
                .Distinct()
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            return names.Length == 0 ? "empty" : string.Join(", ", names);
        }

        private IReadOnlyList<Registration> GetScopeLocalRegistrations<TEntryPoint>(IObjectResolver resolver)
            where TEntryPoint : class
        {
            var collectionType = typeof(IReadOnlyList<TEntryPoint>);
            if (!resolver.TryGetRegistration(collectionType, out var registration) || registration?.Provider == null)
            {
                return Array.Empty<Registration>();
            }

            if (registration.Provider is IEnumerable registrations)
            {
                return registrations
                    .Cast<object>()
                    .OfType<Registration>()
                    .Where(registration => !ShouldSkipRegistration<TEntryPoint>(registration))
                    .ToArray();
            }

            return Array.Empty<Registration>();
        }

        private bool ShouldSkipRegistration<TEntryPoint>(Registration registration)
            where TEntryPoint : class
        {
            var implementationType = registration.ImplementationType;
            if (implementationType == null)
            {
                return false;
            }

            if (typeof(TEntryPoint) == typeof(IInitializable)
                && _settings.ExcludedInitializableImplementationTypes.Contains(implementationType))
            {
                return true;
            }

            if (typeof(TEntryPoint) != typeof(IInitializable)
                && typeof(TEntryPoint) != typeof(IStartable))
            {
                return false;
            }

            return _settings.ManagedServiceMarkerTypes.Any(serviceType => serviceType.IsAssignableFrom(implementationType));
        }
    }

    public sealed class RuntimeFlowGlobalVContainerEntryPointsInitializationService :
        RuntimeFlowVContainerEntryPointsInitializationService,
        IGlobalInitializableService
    {
        private static readonly ILogger Logger = NullLogger.Instance;

        public RuntimeFlowGlobalVContainerEntryPointsInitializationService(
            IObjectResolver resolver,
            RuntimeFlowVContainerEntryPointsSettings? settings = null)
            : base(resolver, "global", Logger, settings)
        {
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            return InitializeVContainerEntryPointsAsync(cancellationToken);
        }

        protected override IObjectResolver GetEntryPointResolver(IObjectResolver resolver)
        {
            if (resolver is not IScopedObjectResolver scopedResolver)
            {
                return resolver;
            }

            var current = scopedResolver;
            while (current.Parent is IScopedObjectResolver parentScopedResolver)
            {
                current = parentScopedResolver;
            }

            return current.Parent ?? current;
        }
    }

    public sealed class RuntimeFlowSessionSyncEntryPointsInitializationService :
        RuntimeFlowVContainerEntryPointsInitializationService,
        ISessionInitializableService,
        IRuntimeFlowSessionSyncEntryPointsBootstrapService
    {
        private static readonly ILogger Logger = NullLogger.Instance;

        public RuntimeFlowSessionSyncEntryPointsInitializationService(
            IObjectResolver resolver,
            RuntimeFlowVContainerEntryPointsSettings? settings = null)
            : base(resolver, "session", Logger, settings)
        {
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            return InitializeVContainerEntryPointsAsync(cancellationToken);
        }
    }
}
