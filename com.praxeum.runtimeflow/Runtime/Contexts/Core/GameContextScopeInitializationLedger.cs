using System;
using System.Collections.Generic;

namespace RuntimeFlow.Contexts
{
    internal sealed class GameContextScopeInitializationLedger
    {
        private readonly Dictionary<(GameContextType Scope, Type? ScopeKey), List<Type>> _scopeInitializationOrder = new();

        public void RecordInitializedService(GameContextType scope, Type? scopeKey, Type serviceType)
        {
            var key = (scope, scopeKey);
            if (!_scopeInitializationOrder.TryGetValue(key, out var initOrder))
            {
                initOrder = new List<Type>();
                _scopeInitializationOrder[key] = initOrder;
            }

            if (!initOrder.Contains(serviceType))
            {
                initOrder.Add(serviceType);
            }
        }

        public List<Type>? GetInitializationOrder(GameContextType scope, Type? scopeKey)
        {
            _scopeInitializationOrder.TryGetValue((scope, scopeKey), out var initOrder);
            return initOrder;
        }

        public void SetInitializationOrder(GameContextType scope, Type? scopeKey, List<Type> initializationOrder)
        {
            if (initializationOrder == null) throw new ArgumentNullException(nameof(initializationOrder));
            _scopeInitializationOrder[(scope, scopeKey)] = initializationOrder;
        }

        public void RemoveScope(GameContextType scope, Type? scopeKey)
        {
            _scopeInitializationOrder.Remove((scope, scopeKey));
        }
    }
}
