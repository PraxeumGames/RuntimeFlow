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
    internal static class RuntimeFlowVContainerEntryPointPhaseRunner
    {
        internal static Task InitializeInitializablesAsync(
            VContainerEntryPointsStartupPlan plan,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            return InitializeInitializablesAsync(
                plan.ScopeResolver,
                plan.EntryPointResolver,
                plan.ScopeName,
                logger,
                plan.Settings,
                plan.UseSessionStageOrder,
                cancellationToken,
                plan.InitializableRegistrations);
        }

        internal static Task InitializeInitializablesAsync(
            IObjectResolver scopeResolver,
            IObjectResolver entryPointResolver,
            string scopeName,
            ILogger logger,
            RuntimeFlowVContainerEntryPointsSettings settings,
            bool useSessionStageOrder,
            CancellationToken cancellationToken,
            IReadOnlyList<Registration>? initializableRegistrations = null)
        {
            if (scopeResolver == null) throw new ArgumentNullException(nameof(scopeResolver));
            if (entryPointResolver == null) throw new ArgumentNullException(nameof(entryPointResolver));
            if (scopeName == null) throw new ArgumentNullException(nameof(scopeName));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            InitializePrioritizedInitializables(scopeResolver, settings, logger, cancellationToken);

            initializableRegistrations ??= GetScopeLocalRegistrations<IInitializable>(entryPointResolver, settings);
            logger.LogInformation(
                "Resolving VContainer initializables for {Scope} scope. Resolver={ResolverType}, Initializables={InitializableRegistrations}",
                scopeName,
                entryPointResolver.GetType().FullName ?? entryPointResolver.GetType().Name,
                DescribeRegistrations(initializableRegistrations));

            logger.LogInformation(
                "Initializing VContainer entry points for {Scope} scope. Initializables={Initializables}",
                scopeName,
                initializableRegistrations.Count);

            if (useSessionStageOrder)
            {
                RuntimeFlowVContainerInitializableRunner.InitializeSessionByStages(
                    entryPointResolver,
                    initializableRegistrations,
                    scopeName,
                    logger,
                    cancellationToken);
            }
            else
            {
                RuntimeFlowVContainerInitializableRunner.InitializeSequential(
                    entryPointResolver,
                    initializableRegistrations,
                    cancellationToken);
            }

            return Task.CompletedTask;
        }

        internal static Task StartStartablesAsync(
            VContainerEntryPointsStartupPlan plan,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            return StartStartablesAsync(
                plan.EntryPointResolver,
                plan.ScopeName,
                logger,
                plan.Settings,
                cancellationToken,
                plan.StartableRegistrations);
        }

        internal static Task StartStartablesAsync(
            IObjectResolver entryPointResolver,
            string scopeName,
            ILogger logger,
            RuntimeFlowVContainerEntryPointsSettings settings,
            CancellationToken cancellationToken,
            IReadOnlyList<Registration>? startableRegistrations = null)
        {
            if (entryPointResolver == null) throw new ArgumentNullException(nameof(entryPointResolver));
            if (scopeName == null) throw new ArgumentNullException(nameof(scopeName));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            startableRegistrations ??= GetScopeLocalRegistrations<IStartable>(entryPointResolver, settings);
            logger.LogInformation(
                "Resolving VContainer startables for {Scope} scope. Resolver={ResolverType}, Startables={StartableRegistrations}",
                scopeName,
                entryPointResolver.GetType().FullName ?? entryPointResolver.GetType().Name,
                DescribeRegistrations(startableRegistrations));

            logger.LogInformation(
                "Starting VContainer entry points for {Scope} scope. Startables={Startables}",
                scopeName,
                startableRegistrations.Count);

            foreach (var registration in startableRegistrations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entryPointResolver.Resolve(registration) is not IStartable startable)
                {
                    throw new InvalidOperationException(
                        $"Resolved entry point '{registration.ImplementationType?.FullName ?? registration.ImplementationType?.Name ?? "<unknown>"}' " +
                        $"does not implement '{typeof(IStartable).FullName}'.");
                }

                startable.Start();
            }

            return Task.CompletedTask;
        }

        internal static IObjectResolver ResolveEntryPointResolver(GameContextType scope, IObjectResolver resolver)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            if (scope != GameContextType.Global)
                return resolver;

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

        internal static IReadOnlyList<Registration> GetScopeLocalRegistrations<TEntryPoint>(
            IObjectResolver resolver,
            RuntimeFlowVContainerEntryPointsSettings settings)
            where TEntryPoint : class
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

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
                    .Where(registration => !ShouldSkipRegistration<TEntryPoint>(registration, settings))
                    .ToArray();
            }

            return Array.Empty<Registration>();
        }

        private static void InitializePrioritizedInitializables(
            IObjectResolver scopeResolver,
            RuntimeFlowVContainerEntryPointsSettings settings,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            foreach (var initializableType in settings.PrioritizedInitializableImplementationTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var initializable = RuntimeFlowPrioritizedInitializableResolver.ResolvePrioritizedInitializable(
                    scopeResolver,
                    initializableType,
                    logger);
                initializable.Initialize();
            }

            settings.AfterPrioritizedInitializablesInitialized?.Invoke(scopeResolver);
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

        private static bool ShouldSkipRegistration<TEntryPoint>(
            Registration registration,
            RuntimeFlowVContainerEntryPointsSettings settings)
            where TEntryPoint : class
        {
            var implementationType = registration.ImplementationType;
            if (implementationType == null)
            {
                return false;
            }

            if (typeof(TEntryPoint) == typeof(IInitializable)
                && settings.ExcludedInitializableImplementationTypes.Contains(implementationType))
            {
                return true;
            }

            return false;
        }
    }
}
