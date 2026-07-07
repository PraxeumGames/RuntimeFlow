using System;
using System.Collections.Generic;

namespace RuntimeFlow.Contexts
{
    internal sealed class GameContextLazyInitializationRegistry
    {
        private readonly Dictionary<Type, (GameContext Context, GameContextType Scope, Type? ScopeKey, ServiceInitializerBinding Initializer)> _lazyServiceBindings = new();
        private readonly HashSet<Type> _initializedLazyServices = new();

        public bool IsInitialized(Type serviceType)
        {
            return _initializedLazyServices.Contains(serviceType);
        }

        public bool TryGetBinding(
            Type serviceType,
            out (GameContext Context, GameContextType Scope, Type? ScopeKey, ServiceInitializerBinding Initializer) binding)
        {
            return _lazyServiceBindings.TryGetValue(serviceType, out binding);
        }

        public void RegisterLazyBinding(ServiceInitializerBinding initializer, GameContext context, GameContextType scope, Type? scopeKey)
        {
            _lazyServiceBindings[initializer.ServiceType] = (context, scope, scopeKey, initializer);
        }

        public void MarkInitialized(Type serviceType)
        {
            _initializedLazyServices.Add(serviceType);
        }

        public void Clear()
        {
            _lazyServiceBindings.Clear();
            _initializedLazyServices.Clear();
        }
    }
}
