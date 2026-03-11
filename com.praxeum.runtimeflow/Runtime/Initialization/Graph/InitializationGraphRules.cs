using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RuntimeFlow.Contexts
{
    internal static class InitializationGraphRules
    {
        internal const string Version = "compiled-constructor-v2";

        internal static bool IsAsyncDependencyType(Type serviceType)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            return serviceType != typeof(IGlobalInitializableService)
                   && serviceType != typeof(ISessionInitializableService)
                   && serviceType != typeof(ISceneInitializableService)
                   && serviceType != typeof(IModuleInitializableService)
                   && serviceType != typeof(IAsyncInitializableService)
                   && serviceType.IsInterface
                   && typeof(IAsyncInitializableService).IsAssignableFrom(serviceType);
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
                .Where(IsAsyncDependencyType);
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
