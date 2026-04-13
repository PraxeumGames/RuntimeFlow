using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.Contexts
{
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

        protected string ScopeName => _scopeName;
        protected ILogger Logger => _logger;

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

        protected virtual void InitializeInitializables(
            IObjectResolver resolver,
            IReadOnlyList<Registration> initializableRegistrations,
            CancellationToken cancellationToken)
        {
            RuntimeFlowVContainerInitializableRunner.InitializeSequential(
                resolver,
                initializableRegistrations,
                cancellationToken);
        }

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
}
