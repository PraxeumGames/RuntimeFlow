using System;
using System.Collections.Generic;
using System.Linq;

namespace RuntimeFlow.Contexts
{
    internal sealed class ScopeProfile
    {
        public List<Action<IGameContext>> Registrations { get; } = new();
        public List<ServiceDescriptor> Services { get; } = new();
    }

    internal sealed class DeferredScopedRegistration
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

    internal sealed class DeferredDecoration
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

    internal sealed class ServiceDescriptor
    {
        public ServiceDescriptor(Type serviceType, Type implementationType)
        {
            ServiceType = serviceType;
            ImplementationType = implementationType;
        }

        public Type ServiceType { get; }
        public Type ImplementationType { get; }
    }

    internal sealed class ServiceConstructionBinding
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

    internal sealed class ServiceInitializerBinding
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

    internal sealed class ScopeActivationParticipantBinding
    {
        public ScopeActivationParticipantBinding(Type serviceType, Type implementationType)
        {
            ServiceType = serviceType;
            ImplementationType = implementationType;
        }

        public Type ServiceType { get; }
        public Type ImplementationType { get; }
    }

    internal sealed class ScopeActivationExecutionPlan
    {
        public ScopeActivationExecutionPlan(IReadOnlyList<ScopeActivationParticipantBinding> enterOrder)
        {
            EnterOrder = enterOrder;
            ExitOrder = enterOrder.Reverse().ToArray();
        }

        public IReadOnlyList<ScopeActivationParticipantBinding> EnterOrder { get; }
        public IReadOnlyList<ScopeActivationParticipantBinding> ExitOrder { get; }
    }

    internal static class RuntimeFlowCompiledInitializationGraph
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
