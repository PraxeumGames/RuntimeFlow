using System;
using System.Collections.Generic;
using System.Linq;
using VContainer;

namespace RuntimeFlow.Contexts
{
    public partial class GameContextBuilder
    {
        private sealed class GameContextScopeProfileStore
        {
            private readonly List<Action<IGameContext>> _globalRegistrations = new();
            private readonly List<Action<IGameContext>> _sessionRegistrations = new();
            private readonly Dictionary<Type, ScopeProfile> _sceneProfiles = new();
            private readonly Dictionary<Type, ScopeProfile> _moduleProfiles = new();

            public IReadOnlyCollection<Action<IGameContext>> GlobalRegistrations => _globalRegistrations;
            public IReadOnlyCollection<Action<IGameContext>> SessionRegistrations => _sessionRegistrations;
            public bool HasGlobalRegistrations => _globalRegistrations.Count > 0;

            public bool HasSceneProfile(Type scopeKey)
            {
                if (scopeKey == null) throw new ArgumentNullException(nameof(scopeKey));
                return _sceneProfiles.ContainsKey(scopeKey);
            }

            public bool HasModuleProfile(Type scopeKey)
            {
                if (scopeKey == null) throw new ArgumentNullException(nameof(scopeKey));
                return _moduleProfiles.ContainsKey(scopeKey);
            }

            public ScopeProfile GetSceneProfile(Type scopeKey)
            {
                if (scopeKey == null) throw new ArgumentNullException(nameof(scopeKey));
                return _sceneProfiles[scopeKey];
            }

            public ScopeProfile GetModuleProfile(Type scopeKey)
            {
                if (scopeKey == null) throw new ArgumentNullException(nameof(scopeKey));
                return _moduleProfiles[scopeKey];
            }

            public bool TryGetSceneProfile(Type scopeKey, out ScopeProfile profile)
            {
                if (scopeKey == null) throw new ArgumentNullException(nameof(scopeKey));
                return _sceneProfiles.TryGetValue(scopeKey, out profile!);
            }

            public bool TryGetModuleProfile(Type scopeKey, out ScopeProfile profile)
            {
                if (scopeKey == null) throw new ArgumentNullException(nameof(scopeKey));
                return _moduleProfiles.TryGetValue(scopeKey, out profile!);
            }

            public void BindScopedRegistration(
                GameContextType scope,
                Type? scopeKey,
                Action<IGameContext> registration)
            {
                if (registration == null) throw new ArgumentNullException(nameof(registration));

                switch (scope)
                {
                    case GameContextType.Global:
                        _globalRegistrations.Add(registration);
                        break;
                    case GameContextType.Session:
                        _sessionRegistrations.Add(registration);
                        break;
                    case GameContextType.Scene:
                        if (scopeKey == null)
                            throw new ArgumentNullException(nameof(scopeKey), "Scene scope key is required. Use the typed ConfigureScene<TScope>() API.");
                        GetOrCreateProfile(_sceneProfiles, scopeKey).Registrations.Add(registration);
                        break;
                    case GameContextType.Module:
                        if (scopeKey == null)
                            throw new ArgumentNullException(nameof(scopeKey), "Module scope key is required. Use the typed ConfigureModule<TScope>() API.");
                        GetOrCreateProfile(_moduleProfiles, scopeKey).Registrations.Add(registration);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported scope.");
                }
            }

            private static ScopeProfile GetOrCreateProfile(IDictionary<Type, ScopeProfile> profiles, Type scopeKey)
            {
                if (!profiles.TryGetValue(scopeKey, out var profile))
                {
                    profile = new ScopeProfile();
                    profiles[scopeKey] = profile;
                }

                return profile;
            }
        }

        private sealed class GameContextScopeRegistry
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

        private sealed class GameContextDeferredRegistrationQueue
        {
            private readonly List<DeferredScopedRegistration> _deferredScopedRegistrations = new();
            private readonly List<DeferredDecoration> _deferredDecorations = new();

            public void DeferScopedRegistration(
                GameContextType scope,
                Type? scopeKey,
                Action<IGameContext> registration)
            {
                if (registration == null) throw new ArgumentNullException(nameof(registration));
                _deferredScopedRegistrations.Add(new DeferredScopedRegistration(scope, scopeKey, registration));
            }

            public void DeferDecoration(
                GameContextType scope,
                Type? scopeKey,
                Type serviceType,
                Type decoratorType)
            {
                _deferredDecorations.Add(new DeferredDecoration(scope, scopeKey, serviceType, decoratorType));
            }

            public void Flush(Action<GameContextType, Type?, Action<IGameContext>> bindScopedRegistration)
            {
                if (bindScopedRegistration == null) throw new ArgumentNullException(nameof(bindScopedRegistration));
                if (_deferredScopedRegistrations.Count == 0 && _deferredDecorations.Count == 0)
                    return;

                foreach (var deferred in _deferredScopedRegistrations)
                {
                    bindScopedRegistration(deferred.Scope, deferred.ScopeKey, deferred.Registration);
                }

                _deferredScopedRegistrations.Clear();

                foreach (var decoration in _deferredDecorations)
                {
                    var serviceType = decoration.ServiceType;
                    var decoratorType = decoration.DecoratorType;
                    bindScopedRegistration(decoration.Scope, decoration.ScopeKey, context =>
                    {
                        if (context is GameContext gameContext)
                            gameContext.Decorate(serviceType, decoratorType);
                        else
                            context.ConfigureContainer(builder =>
                            {
                                builder.Register(decoratorType, Lifetime.Singleton).As(serviceType);
                            });
                    });
                }

                _deferredDecorations.Clear();
            }
        }

        private sealed class GameContextScopeInitializationLedger
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

        private sealed class GameContextLazyInitializationRegistry
        {
            private readonly Dictionary<Type, (GameContext Context, GameContextType Scope, Type? ScopeKey)> _lazyServiceBindings = new();
            private readonly HashSet<Type> _initializedLazyServices = new();

            public bool IsInitialized(Type serviceType)
            {
                return _initializedLazyServices.Contains(serviceType);
            }

            public bool TryGetBinding(
                Type serviceType,
                out (GameContext Context, GameContextType Scope, Type? ScopeKey) binding)
            {
                return _lazyServiceBindings.TryGetValue(serviceType, out binding);
            }

            public void RegisterLazyBinding(Type serviceType, GameContext context, GameContextType scope, Type? scopeKey)
            {
                _lazyServiceBindings[serviceType] = (context, scope, scopeKey);
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

        private sealed class ScopeProfile
        {
            public List<Action<IGameContext>> Registrations { get; } = new();
            public List<ServiceDescriptor> Services { get; } = new();
        }

        private sealed class DeferredScopedRegistration
        {
            public DeferredScopedRegistration(GameContextType scope, Type? scopeKey, Action<IGameContext> registration)
            {
                Scope = scope;
                ScopeKey = scopeKey;
                Registration = registration;
            }

            public GameContextType Scope { get; }
            public Type? ScopeKey { get; }
            public Action<IGameContext> Registration { get; }
        }

        private sealed class DeferredDecoration
        {
            public DeferredDecoration(GameContextType scope, Type? scopeKey, Type serviceType, Type decoratorType)
            {
                Scope = scope;
                ScopeKey = scopeKey;
                ServiceType = serviceType;
                DecoratorType = decoratorType;
            }

            public GameContextType Scope { get; }
            public Type? ScopeKey { get; }
            public Type ServiceType { get; }
            public Type DecoratorType { get; }
        }

        private sealed class ServiceDescriptor
        {
            public ServiceDescriptor(Type serviceType, Type implementationType)
            {
                ServiceType = serviceType;
                ImplementationType = implementationType;
            }

            public Type ServiceType { get; }
            public Type ImplementationType { get; }
        }

        private sealed class ServiceConstructionBinding
        {
            public ServiceConstructionBinding(Type serviceType, Type implementationType, IReadOnlyCollection<Type> dependencies)
            {
                ServiceType = serviceType;
                ImplementationType = implementationType;
                Dependencies = dependencies;
            }

            public Type ServiceType { get; }
            public Type ImplementationType { get; }
            public IReadOnlyCollection<Type> Dependencies { get; }
        }

        private sealed class ServiceInitializerBinding
        {
            public ServiceInitializerBinding(Type serviceType, Type implementationType, IReadOnlyCollection<Type> dependencies)
            {
                ServiceType = serviceType;
                ImplementationType = implementationType;
                Dependencies = dependencies;
            }

            public Type ServiceType { get; }
            public Type ImplementationType { get; }
            public IReadOnlyCollection<Type> Dependencies { get; }
        }

        private sealed class ScopeActivationParticipantBinding
        {
            public ScopeActivationParticipantBinding(Type serviceType, Type implementationType)
            {
                ServiceType = serviceType;
                ImplementationType = implementationType;
            }

            public Type ServiceType { get; }
            public Type ImplementationType { get; }
        }

        private sealed class ScopeActivationExecutionPlan
        {
            public ScopeActivationExecutionPlan(IReadOnlyList<ScopeActivationParticipantBinding> enterOrder)
            {
                EnterOrder = enterOrder;
                ExitOrder = enterOrder.Reverse().ToArray();
            }

            public IReadOnlyList<ScopeActivationParticipantBinding> EnterOrder { get; }
            public IReadOnlyList<ScopeActivationParticipantBinding> ExitOrder { get; }
        }

        private static class RuntimeFlowCompiledInitializationGraph
        {
            internal const string RuleVersion = "";

            internal sealed class Node
            {
                internal Node(Type serviceType, Type implementationType, GameContextType scope, Type[] dependencies)
                {
                    ServiceType = serviceType;
                    ImplementationType = implementationType;
                    Scope = scope;
                    Dependencies = dependencies;
                }

                internal Type ServiceType { get; }
                internal Type ImplementationType { get; }
                internal GameContextType Scope { get; }
                internal Type[] Dependencies { get; }
            }

            internal static readonly Node[] Nodes = Array.Empty<Node>();
        }

    }
}
