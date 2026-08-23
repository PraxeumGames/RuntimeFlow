using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RuntimeFlow.Contexts
{
    internal static class InitializationGraphRules
    {
        internal const string Version = "compiled-explicit-dependencies-v4";

        /// <summary>
        /// Checks if a constructor parameter type represents an async initialization dependency.
        /// Qualifies: interfaces that extend <see cref="IAsyncInitializableService"/> (via the
        /// contract catalog) and closed <c>ContentSource&lt;TData&gt;</c> / <c>IContentSource&lt;TData&gt;</c>
        /// types — a content source injected into a constructor is an explicit data-flow edge:
        /// the consumer initializes after the source loads.
        /// </summary>
        internal static bool IsAsyncDependencyType(Type serviceType)
        {
            if (InitializationContractCatalog.IsConstructorDependencyType(serviceType))
                return true;
            return IsClosedContentSourceType(serviceType);
        }

        /// <summary>Detects closed <c>ContentSource&lt;TData&gt;</c> / <c>IContentSource&lt;TData&gt;</c> types.</summary>
        internal static bool IsClosedContentSourceType(Type type)
        {
            if (!type.IsGenericType)
                return false;
            var definition = type.GetGenericTypeDefinition();
            return definition == typeof(RuntimeFlow.Content.ContentSource<>)
                   || definition == typeof(RuntimeFlow.Content.IContentSource<>);
        }

        /// <summary>
        /// Checks if a type declared via <see cref="DependsOnAttribute"/> is a valid initialization dependency.
        /// Accepts both interfaces and concrete classes that implement <see cref="IAsyncInitializableService"/>,
        /// plus RuntimeFlow-owned startup phase completion markers.
        /// This allows <c>[DependsOn(typeof(MetaClientRunner))]</c> without requiring a marker interface.
        /// </summary>
        internal static bool IsExplicitDependencyType(Type serviceType)
        {
            return InitializationContractCatalog.IsExplicitDependencyType(serviceType)
                   || IsEntryPointCompletionDependencyType(serviceType);
        }

        internal static bool IsEntryPointCompletionDependencyType(Type serviceType)
        {
            return serviceType == typeof(RuntimeFlowVContainerEntryPointsStartupPhase)
                   || serviceType == typeof(IRuntimeFlowSessionSyncEntryPointsBootstrapService);
        }

        internal static IReadOnlyCollection<Type> ResolveConstructorDependencies(Type implementationType)
        {
            var constructorDeps = ResolveFromConstructor(implementationType);
            var attributeDeps = ResolveFromAttributes(implementationType);

            return constructorDeps.Concat(attributeDeps)
                .Distinct()
                .ToArray();
        }

        private static IEnumerable<Type> ResolveFromConstructor(Type implementationType)
        {
            var constructor = SelectConstructor(implementationType);
            if (constructor == null)
                return Enumerable.Empty<Type>();

            return constructor.GetParameters()
                .Select(parameter => parameter.ParameterType)
                .Where(IsAsyncDependencyType);
        }

        private static IEnumerable<Type> ResolveFromAttributes(Type implementationType)
        {
            return implementationType.GetCustomAttributes<DependsOnAttribute>()
                .Select(attr => attr.ServiceType)
                .Where(IsExplicitDependencyType);
        }

        internal static ConstructorInfo? SelectConstructor(Type implementationType)
        {
            if (implementationType == null) throw new ArgumentNullException(nameof(implementationType));
            var constructors = implementationType.GetConstructors();
            if (constructors.Length == 0)
                return null;

            var injectConstructor = constructors.FirstOrDefault(constructor =>
                constructor.GetCustomAttributes(inherit: true).Any(attribute => attribute is VContainer.InjectAttribute));

            return injectConstructor ?? constructors
                .OrderByDescending(constructor => constructor.GetParameters().Length)
                .First();
        }
    }
}
