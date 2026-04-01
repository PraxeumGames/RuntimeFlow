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

    public sealed class RuntimeFlowLegacyEntryPointsBridgeOptions
    {
        private readonly HashSet<Type> _managedServiceMarkerTypes = new()
        {
            typeof(IGlobalInitializableService),
            typeof(ISessionInitializableService)
        };

        private readonly HashSet<Type> _excludedInitializableImplementationTypes = new();

        public RuntimeFlowLegacyEntryPointsBridgeOptions AddManagedServiceMarker(Type serviceType)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (!serviceType.IsInterface)
                throw new ArgumentException("Managed marker type must be an interface.", nameof(serviceType));

            _managedServiceMarkerTypes.Add(serviceType);
            return this;
        }

        public RuntimeFlowLegacyEntryPointsBridgeOptions AddManagedServiceMarker<TService>()
        {
            return AddManagedServiceMarker(typeof(TService));
        }

        public RuntimeFlowLegacyEntryPointsBridgeOptions ExcludeInitializable(Type implementationType)
        {
            if (implementationType == null) throw new ArgumentNullException(nameof(implementationType));
            _excludedInitializableImplementationTypes.Add(implementationType);
            return this;
        }

        public RuntimeFlowLegacyEntryPointsBridgeOptions ExcludeInitializable<TImplementation>()
        {
            return ExcludeInitializable(typeof(TImplementation));
        }

        internal RuntimeFlowLegacyEntryPointsBridgeSettings BuildSettings()
        {
            return new RuntimeFlowLegacyEntryPointsBridgeSettings(
                _managedServiceMarkerTypes.ToArray(),
                _excludedInitializableImplementationTypes.ToArray());
        }
    }

    public sealed class RuntimeFlowLegacyInitializablesBootstrapOptions
    {
        private readonly List<Type> _requiredInitializableTypes = new();

        public IReadOnlyCollection<Type> RequiredInitializableTypes => _requiredInitializableTypes;
        public Action<IObjectResolver>? AfterInitializablesInitialized { get; set; }

        public RuntimeFlowLegacyInitializablesBootstrapOptions AddRequiredInitializable(Type initializableType)
        {
            if (initializableType == null) throw new ArgumentNullException(nameof(initializableType));
            if (!typeof(IInitializable).IsAssignableFrom(initializableType))
            {
                throw new ArgumentException(
                    $"Type '{initializableType.FullName}' must implement '{typeof(IInitializable).FullName}'.",
                    nameof(initializableType));
            }

            if (_requiredInitializableTypes.Contains(initializableType))
                return this;

            _requiredInitializableTypes.Add(initializableType);
            return this;
        }

        public RuntimeFlowLegacyInitializablesBootstrapOptions AddRequiredInitializable<TInitializable>()
            where TInitializable : class, IInitializable
        {
            return AddRequiredInitializable(typeof(TInitializable));
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

    public sealed class RuntimeFlowLegacyEntryPointsBridgeSettings
    {
        internal static readonly RuntimeFlowLegacyEntryPointsBridgeSettings Default = new(
            new[]
            {
                typeof(IGlobalInitializableService),
                typeof(ISessionInitializableService)
            },
            Array.Empty<Type>());

        public RuntimeFlowLegacyEntryPointsBridgeSettings(
            IReadOnlyCollection<Type> managedServiceMarkerTypes,
            IReadOnlyCollection<Type> excludedInitializableImplementationTypes)
        {
            ManagedServiceMarkerTypes = managedServiceMarkerTypes ?? throw new ArgumentNullException(nameof(managedServiceMarkerTypes));
            ExcludedInitializableImplementationTypes = excludedInitializableImplementationTypes
                ?? throw new ArgumentNullException(nameof(excludedInitializableImplementationTypes));
        }

        public IReadOnlyCollection<Type> ManagedServiceMarkerTypes { get; }
        public IReadOnlyCollection<Type> ExcludedInitializableImplementationTypes { get; }
    }

    public interface ISessionLegacyEntryPointsInitializationService : ISessionInitializableService
    {
    }

    public interface ILegacyInitializablesBootstrapService : ISessionInitializableService
    {
    }

    public static class RuntimeFlowLegacyEntryPointResolver
    {
        public static IInitializable ResolveRequiredInitializable(
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

            if (resolver.TryGetRegistration(initializableType, out var directRegistration)
                && directRegistration != null)
            {
                if (resolver.Resolve(directRegistration) is IInitializable directInstance)
                {
                    return directInstance;
                }

                throw new InvalidOperationException(
                    $"Resolved legacy initializable '{initializableType.FullName}' does not implement '{typeof(IInitializable).FullName}'.");
            }

            var collectionType = typeof(IReadOnlyList<IInitializable>);
            if (!resolver.TryGetRegistration(collectionType, out var registration)
                || registration?.Provider is not IEnumerable registrations)
            {
                throw new InvalidOperationException(
                    $"Legacy IInitializable registrations are not available while resolving '{initializableType.FullName}'.");
            }

            (logger ?? NullLogger.Instance).LogWarning(
                "Falling back to legacy IInitializable sweep lookup for {Type}.",
                initializableType.FullName);

            var entryPointRegistration = registrations
                .Cast<object>()
                .OfType<Registration>()
                .FirstOrDefault(x => x.ImplementationType == initializableType);

            if (entryPointRegistration == null)
            {
                throw new InvalidOperationException(
                    $"Required legacy initializable '{initializableType.FullName}' is not registered.");
            }

            if (resolver.Resolve(entryPointRegistration) is not IInitializable instance)
            {
                throw new InvalidOperationException(
                    $"Resolved legacy initializable '{initializableType.FullName}' does not implement '{typeof(IInitializable).FullName}'.");
            }

            return instance;
        }

        public static T ResolveRequiredInitializable<T>(
            IObjectResolver resolver,
            ILogger? logger = null)
            where T : class, IInitializable
        {
            return (T)ResolveRequiredInitializable(resolver, typeof(T), logger);
        }
    }

    public sealed class RuntimeFlowLegacyInitializablesBootstrapService : ILegacyInitializablesBootstrapService
    {
        private static readonly ILogger Logger = NullLogger.Instance;
        private readonly IObjectResolver _resolver;
        private readonly RuntimeFlowLegacyInitializablesBootstrapOptions _options;

        public RuntimeFlowLegacyInitializablesBootstrapService(
            IObjectResolver resolver,
            RuntimeFlowLegacyInitializablesBootstrapOptions options)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var initializableType in _options.RequiredInitializableTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var initializable = RuntimeFlowLegacyEntryPointResolver.ResolveRequiredInitializable(
                    _resolver,
                    initializableType,
                    Logger);
                initializable.Initialize();
            }

            _options.AfterInitializablesInitialized?.Invoke(_resolver);
            return Task.CompletedTask;
        }
    }

    public abstract class RuntimeFlowLegacyVContainerEntryPointsInitializationService
    {
        private readonly IObjectResolver _resolver;
        private readonly string _scopeName;
        private readonly ILogger _logger;
        private readonly RuntimeFlowLegacyEntryPointsBridgeSettings _settings;

        protected RuntimeFlowLegacyVContainerEntryPointsInitializationService(
            IObjectResolver resolver,
            string scopeName,
            ILogger logger,
            RuntimeFlowLegacyEntryPointsBridgeSettings? settings = null)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _scopeName = scopeName ?? throw new ArgumentNullException(nameof(scopeName));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? RuntimeFlowLegacyEntryPointsBridgeSettings.Default;
        }

        protected Task InitializeLegacyEntryPointsAsync(CancellationToken cancellationToken)
        {
            var resolver = GetEntryPointResolver(_resolver);
            var initializableRegistrations = GetScopeLocalRegistrations<IInitializable>(resolver);
            var startableRegistrations = GetScopeLocalRegistrations<IStartable>(resolver);
            _logger.LogInformation(
                "Resolving legacy VContainer entry points for {Scope} scope. Resolver={ResolverType}, Initializables={InitializableRegistrations}, Startables={StartableRegistrations}",
                _scopeName,
                resolver.GetType().FullName ?? resolver.GetType().Name,
                DescribeRegistrations(initializableRegistrations),
                DescribeRegistrations(startableRegistrations));

            _logger.LogInformation(
                "Initializing legacy VContainer entry points for {Scope} scope. Initializables={Initializables}, Startables={Startables}",
                _scopeName,
                initializableRegistrations.Count,
                startableRegistrations.Count);

            foreach (var registration in initializableRegistrations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (resolver.Resolve(registration) is not IInitializable initializable)
                {
                    throw new InvalidOperationException(
                        $"Resolved legacy entry point '{registration.ImplementationType?.FullName ?? registration.ImplementationType?.Name ?? "<unknown>"}' " +
                        $"does not implement '{typeof(IInitializable).FullName}'.");
                }

                initializable.Initialize();
            }

            foreach (var registration in startableRegistrations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (resolver.Resolve(registration) is not IStartable startable)
                {
                    throw new InvalidOperationException(
                        $"Resolved legacy entry point '{registration.ImplementationType?.FullName ?? registration.ImplementationType?.Name ?? "<unknown>"}' " +
                        $"does not implement '{typeof(IStartable).FullName}'.");
                }

                startable.Start();
            }

            return Task.CompletedTask;
        }

        protected virtual IObjectResolver GetEntryPointResolver(IObjectResolver resolver) => resolver;

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

    public sealed class RuntimeFlowGlobalLegacyVContainerEntryPointsInitializationService :
        RuntimeFlowLegacyVContainerEntryPointsInitializationService,
        IGlobalInitializableService
    {
        private static readonly ILogger Logger = NullLogger.Instance;

        public RuntimeFlowGlobalLegacyVContainerEntryPointsInitializationService(
            IObjectResolver resolver,
            RuntimeFlowLegacyEntryPointsBridgeSettings? settings = null)
            : base(resolver, "global", Logger, settings)
        {
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            return InitializeLegacyEntryPointsAsync(cancellationToken);
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

    public sealed class RuntimeFlowSessionLegacyVContainerEntryPointsInitializationService :
        RuntimeFlowLegacyVContainerEntryPointsInitializationService,
        ISessionLegacyEntryPointsInitializationService
    {
        private static readonly ILogger Logger = NullLogger.Instance;

        public RuntimeFlowSessionLegacyVContainerEntryPointsInitializationService(
            IObjectResolver resolver,
            RuntimeFlowLegacyEntryPointsBridgeSettings? settings = null)
            : base(resolver, "session", Logger, settings)
        {
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            return InitializeLegacyEntryPointsAsync(cancellationToken);
        }
    }

    public static class RuntimeFlowInstallerModules
    {
        public static void RegisterGlobalInfrastructure(
            IContainerBuilder builder,
            RuntimeFlowGlobalInstallerOptions? options = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            options ??= new RuntimeFlowGlobalInstallerOptions();

            if (options.EnableLazySingletons)
                options.ConfigureLazySingletons?.Invoke(builder);

            if (options.RegisterPipelineProvider)
            {
                builder.Register<RuntimeFlowPipelineProvider>(Lifetime.Singleton)
                    .AsImplementedInterfaces();
            }
        }

        public static void RegisterSessionInfrastructure(
            IContainerBuilder builder,
            RuntimeFlowSessionInstallerOptions? options = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            options ??= new RuntimeFlowSessionInstallerOptions();

            if (options.EnableLazySingletons)
                options.ConfigureLazySingletons?.Invoke(builder);

            if (options.RegisterBuildCallbackResolverWarmup)
            {
                if (options.BuildCallbackResolverAction != null)
                {
                    builder.RegisterBuildCallback(options.BuildCallbackResolverAction);
                }
                else if (options.BuildCallbackResolverType != null)
                {
                    builder.RegisterBuildCallback(resolver => resolver.Resolve(options.BuildCallbackResolverType));
                }
            }
        }

        public static void RegisterLegacyEntryPointsBridge(
            IContainerBuilder builder,
            RuntimeFlowLegacyEntryPointsBridgeOptions? options = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));

            var settings = (options ?? new RuntimeFlowLegacyEntryPointsBridgeOptions()).BuildSettings();
            builder.RegisterInstance(settings);

            builder.Register<RuntimeFlowSessionLegacyVContainerEntryPointsInitializationService>(Lifetime.Singleton)
                .AsSelf()
                .AsImplementedInterfaces();
        }

        public static void RegisterLegacyInitializablesBootstrap(
            IContainerBuilder builder,
            RuntimeFlowLegacyInitializablesBootstrapOptions? options = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));

            options ??= new RuntimeFlowLegacyInitializablesBootstrapOptions();
            builder.RegisterInstance(options);
            builder.Register<RuntimeFlowLegacyInitializablesBootstrapService>(Lifetime.Singleton)
                .AsSelf()
                .AsImplementedInterfaces();
        }

        public static void RegisterGlobalLegacyEntryPointsBridge(
            IContainerBuilder builder,
            RuntimeFlowLegacyEntryPointsBridgeOptions? options = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));

            var settings = (options ?? new RuntimeFlowLegacyEntryPointsBridgeOptions()).BuildSettings();
            builder.RegisterInstance(settings);

            builder.Register<RuntimeFlowGlobalLegacyVContainerEntryPointsInitializationService>(Lifetime.Singleton)
                .AsSelf()
                .AsImplementedInterfaces();
        }

        public static void RegisterLoadingAndRestartInfrastructure(
            IContainerBuilder builder,
            RuntimeFlowLoadingRestartInstallerOptions? options = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            options ??= new RuntimeFlowLoadingRestartInstallerOptions();

            var registerLoadingState = options.ResolveRegisterLoadingState(fallbackValue: false);
            var registerRestartHandler = options.ResolveRegisterRestartHandler(fallbackValue: false);
            if (!registerLoadingState && !registerRestartHandler)
                return;

            if (registerLoadingState)
            {
                var implementationType = options.ResolveLoadingStateImplementationType(null);
                if (implementationType == null)
                    throw new InvalidOperationException(
                        "Loading state implementation type must be provided when RegisterLoadingState is enabled.");

                if (!typeof(IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>)
                    .IsAssignableFrom(implementationType))
                {
                    throw new InvalidOperationException(
                        $"Loading state implementation type '{implementationType.FullName}' must implement '{typeof(IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>).FullName}'.");
                }

                var loadingServiceType = options.ResolveLoadingStateServiceType(
                    typeof(IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>));
                if (loadingServiceType != null && !loadingServiceType.IsAssignableFrom(implementationType))
                {
                    throw new InvalidOperationException(
                        $"Loading state implementation type '{implementationType.FullName}' is not assignable to '{loadingServiceType.FullName}'.");
                }

                RegisterType(builder, implementationType, loadingServiceType == null
                    ? null
                    : new[] { loadingServiceType, typeof(IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>) });
            }

            if (registerRestartHandler)
            {
                var implementationType = options.ResolveRestartHandlerImplementationType(null);
                if (implementationType == null)
                    throw new InvalidOperationException(
                        "Restart handler implementation type must be provided when RegisterRestartHandler is enabled.");

                var serviceTypes = options.ResolveRestartHandlerServiceTypes(null);
                RegisterType(builder, implementationType, serviceTypes);
            }

            var additional = options.ResolveAdditionalRegistrations(null);
            if (additional == null)
                return;

            foreach (var registration in additional)
            {
                RegisterType(builder, registration.ImplementationType, registration.ServiceTypes);
            }
        }

        private static void RegisterType(
            IContainerBuilder builder,
            Type implementationType,
            IReadOnlyCollection<Type>? serviceTypes)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (implementationType == null) throw new ArgumentNullException(nameof(implementationType));

            var registrationBuilder = builder.Register(implementationType, Lifetime.Singleton).AsSelf();
            if (serviceTypes == null)
            {
                registrationBuilder.AsImplementedInterfaces();
                return;
            }

            var hasServiceType = false;
            foreach (var serviceType in serviceTypes.Where(type => type != null).Distinct())
            {
                if (!serviceType.IsAssignableFrom(implementationType))
                {
                    throw new InvalidOperationException(
                        $"Type '{implementationType.FullName}' cannot be exposed as '{serviceType.FullName}'.");
                }

                hasServiceType = true;
                registrationBuilder.As(serviceType);
            }

            if (!hasServiceType)
            {
                registrationBuilder.AsImplementedInterfaces();
            }
        }
    }
}
