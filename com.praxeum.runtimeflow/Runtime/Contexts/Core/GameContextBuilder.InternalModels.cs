using System;
using System.Collections.Generic;
using System.Linq;
using VContainer;

namespace RuntimeFlow.Contexts
{
    public partial class GameContextBuilder
    {
        private static ScopeProfile GetOrCreateProfile(IDictionary<Type, ScopeProfile> profiles, Type scopeKey)
        {
            if (!profiles.TryGetValue(scopeKey, out var profile))
            {
                profile = new ScopeProfile();
                profiles[scopeKey] = profile;
            }

            return profile;
        }

        private static bool TryGetScopeProfile(
            IReadOnlyDictionary<Type, ScopeProfile> profiles,
            Type scopeKey,
            out ScopeProfile profile)
        {
            return profiles.TryGetValue(scopeKey, out profile!);
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
