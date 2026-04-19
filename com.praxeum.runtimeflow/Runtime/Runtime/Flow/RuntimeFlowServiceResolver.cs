using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.Contexts
{
    public static class RuntimeFlowServiceResolver
    {
        public static bool TryResolveFromScene<TService>(
            Component sceneReferences,
            out TService service)
            where TService : class
        {
            if (sceneReferences == null)
            {
                service = default!; // callers must check the bool return before using service
                return false;
            }

            var scope = sceneReferences.GetComponentInChildren<LifetimeScope>(true);
            if (scope == null || scope.Container == null)
            {
                service = default!; // callers must check the bool return before using service
                return false;
            }

            return TryResolveFromResolver(scope.Container, out service);
        }

        public static bool TryResolveFromContext<TService>(
            IRuntimeFlowContext context,
            out TService service)
            where TService : class
        {
            if (context == null)
            {
                service = default!; // callers must check the bool return before using service
                return false;
            }

            return context.TryResolveSessionService(out service!);
        }

        public static bool TryResolveFromContext<TService>(
            IGameContext? context,
            out TService service)
            where TService : class
        {
            return TryResolveFromResolver(context?.Resolver, out service);
        }

        public static bool TryResolveFromResolver<TService>(
            IObjectResolver? resolver,
            out TService service)
            where TService : class
        {
            if (resolver == null)
            {
                service = default!; // callers must check the bool return before using service
                return false;
            }

            return resolver.TryResolve(out service!);
        }
    }
}
