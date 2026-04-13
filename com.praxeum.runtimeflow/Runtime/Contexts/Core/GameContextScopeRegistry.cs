using System;
using System.Collections.Generic;

namespace RuntimeFlow.Contexts
{
    internal sealed class GameContextScopeRegistry
    {
        private readonly Dictionary<Type, GameContextType> _declaredScopeTypes = new();
        private readonly Dictionary<Type, ScopeLifecycleState> _scopeStates = new();
        private readonly object _scopeStateSync = new();

        public void DeclareScope(Type scopeType, GameContextType scope)
        {
            if (scopeType == null) throw new ArgumentNullException(nameof(scopeType));

            if (_declaredScopeTypes.TryGetValue(scopeType, out var existingScope))
            {
                var scopeTypeName = scopeType.FullName ?? scopeType.Name;
                if (existingScope == scope)
                {
                    throw new ScopeRegistrationException("GBSR3001",
                        $"GBSR3001: Scope type '{scopeTypeName}' is already declared for '{existingScope}'.");
                }

                throw new ScopeRegistrationException("GBSR3002",
                    $"GBSR3002: Scope type '{scopeTypeName}' is already declared for '{existingScope}' and cannot be declared for '{scope}'.");
            }

            _declaredScopeTypes.Add(scopeType, scope);
            SetScopeState(scopeType, ScopeLifecycleState.NotLoaded);
        }

        public bool TryResolveScopeType(Type scopeType, out GameContextType scope)
        {
            if (scopeType == null) throw new ArgumentNullException(nameof(scopeType));
            return _declaredScopeTypes.TryGetValue(scopeType, out scope);
        }

        public bool TryGetDeclaredScope(Type scopeType, out GameContextType scope)
        {
            return _declaredScopeTypes.TryGetValue(scopeType, out scope);
        }

        public GameContextType GetDeclaredScopeOrDefault(Type scopeType, GameContextType fallbackScope)
        {
            if (scopeType == null) throw new ArgumentNullException(nameof(scopeType));
            return _declaredScopeTypes.TryGetValue(scopeType, out var scope)
                ? scope
                : fallbackScope;
        }

        public void SetScopeState(Type scopeType, ScopeLifecycleState state)
        {
            if (scopeType == null) throw new ArgumentNullException(nameof(scopeType));

            lock (_scopeStateSync)
            {
                _scopeStates[scopeType] = state;
            }
        }

        public ScopeLifecycleState GetScopeState(Type scopeType)
        {
            if (scopeType == null) throw new ArgumentNullException(nameof(scopeType));

            lock (_scopeStateSync)
            {
                return _scopeStates.TryGetValue(scopeType, out var state) ? state : ScopeLifecycleState.NotLoaded;
            }
        }

        public void SetScopeStateIfTracked(GameContextType scope, ScopeLifecycleState state, Type? explicitScopeKey = null)
        {
            if (explicitScopeKey != null)
            {
                SetScopeState(explicitScopeKey, state);
                return;
            }

            foreach (var key in FindDeclaredScopeKeys(scope))
                SetScopeState(key, state);
        }

        public Type? FindDeclaredScopeKey(GameContextType scope)
        {
            foreach (var kvp in _declaredScopeTypes)
            {
                if (kvp.Value == scope)
                    return kvp.Key;
            }

            return null;
        }

        public IEnumerable<Type> FindDeclaredScopeKeys(GameContextType scope)
        {
            foreach (var kvp in _declaredScopeTypes)
            {
                if (kvp.Value == scope)
                    yield return kvp.Key;
            }
        }

        public void ResetScopeStates()
        {
            lock (_scopeStateSync)
            {
                _scopeStates.Clear();
            }
        }
    }
}
