using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
}
