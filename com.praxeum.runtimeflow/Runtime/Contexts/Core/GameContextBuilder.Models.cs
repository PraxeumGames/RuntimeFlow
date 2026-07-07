using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using VContainer;

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
        public ServiceInitializerBinding(
            Type serviceType,
            Type implementationType,
            IReadOnlyCollection<Type> dependencies,
            Type? resolveServiceType = null,
            Registration? registration = null)
        {
            ServiceType = serviceType;
            ImplementationType = implementationType;
            Dependencies = dependencies;
            ResolveServiceType = resolveServiceType ?? serviceType;
            Registration = registration;
        }

        public Type ServiceType { get; }
        public Type ImplementationType { get; }
        public IReadOnlyCollection<Type> Dependencies { get; }
        public Type ResolveServiceType { get; }
        public Registration? Registration { get; }
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

    internal sealed class ScopeStartupPlan
    {
        public ScopeStartupPlan(
            VContainerEntryPointsStartupPlan? entryPoints,
            IReadOnlyList<GlobalBootstrapOperationBinding> globalBootstrapOperations,
            IReadOnlyList<ServiceInitializerBinding> asyncInitializers)
        {
            EntryPoints = entryPoints;
            GlobalBootstrapOperations = globalBootstrapOperations;
            AsyncInitializers = asyncInitializers;
        }

        public VContainerEntryPointsStartupPlan? EntryPoints { get; }

        public IReadOnlyList<GlobalBootstrapOperationBinding> GlobalBootstrapOperations { get; }

        public IReadOnlyList<ServiceInitializerBinding> AsyncInitializers { get; }

        public int TotalServiceCount => (EntryPoints == null ? 0 : 1) + GlobalBootstrapOperations.Count + AsyncInitializers.Count;
    }

    internal sealed class GlobalBootstrapOperationBinding
    {
        public GlobalBootstrapOperationBinding(Type implementationType, Registration registration)
        {
            ImplementationType = implementationType ?? throw new ArgumentNullException(nameof(implementationType));
            Registration = registration ?? throw new ArgumentNullException(nameof(registration));
        }

        public Type ImplementationType { get; }
        public Registration Registration { get; }
    }

    internal sealed class VContainerEntryPointsStartupPlan
    {
        public VContainerEntryPointsStartupPlan(
            GameContextType scope,
            string scopeName,
            IObjectResolver scopeResolver,
            IObjectResolver entryPointResolver,
            RuntimeFlowVContainerEntryPointsSettings settings,
            IReadOnlyList<Registration> initializableRegistrations,
            IReadOnlyList<Registration> startableRegistrations,
            IReadOnlyCollection<Type> completedDependencyMarkers,
            bool useSessionStageOrder)
        {
            Scope = scope;
            ScopeName = scopeName;
            ScopeResolver = scopeResolver;
            EntryPointResolver = entryPointResolver;
            Settings = settings;
            InitializableRegistrations = initializableRegistrations;
            StartableRegistrations = startableRegistrations;
            CompletedDependencyMarkers = completedDependencyMarkers;
            UseSessionStageOrder = useSessionStageOrder;
        }

        public GameContextType Scope { get; }
        public string ScopeName { get; }
        public IObjectResolver ScopeResolver { get; }
        public IObjectResolver EntryPointResolver { get; }
        public RuntimeFlowVContainerEntryPointsSettings Settings { get; }
        public IReadOnlyList<Registration> InitializableRegistrations { get; }
        public IReadOnlyList<Registration> StartableRegistrations { get; }
        public IReadOnlyCollection<Type> CompletedDependencyMarkers { get; }
        public bool UseSessionStageOrder { get; }
        public Type ProgressServiceType => typeof(RuntimeFlowVContainerEntryPointsStartupPhase);
    }

    internal sealed class RuntimeFlowVContainerEntryPointsStartupPhase
    {
        private RuntimeFlowVContainerEntryPointsStartupPhase()
        {
        }
    }

    internal static class RuntimeFlowCompiledInitializationGraph
    {
        private const string GeneratedGraphTypeName = "RuntimeFlow.Contexts.Generated.CompiledInitializationGraph";
        private static readonly Lazy<GraphSnapshot> Snapshot = new(LoadSnapshot);

        internal static string RuleVersion => Snapshot.Value.RuleVersion;

        internal static IReadOnlyList<Node> Nodes => Snapshot.Value.Nodes;

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

        private sealed class GraphSnapshot
        {
            public GraphSnapshot(string ruleVersion, IReadOnlyList<Node> nodes)
            {
                RuleVersion = ruleVersion;
                Nodes = nodes;
            }

            public string RuleVersion { get; }
            public IReadOnlyList<Node> Nodes { get; }
        }

        private static GraphSnapshot LoadSnapshot()
        {
            var graphTypes = AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(assembly => assembly.GetType(GeneratedGraphTypeName, throwOnError: false))
                .Where(type => type != null)
                .Select(type => type!)
                .ToArray();

            if (graphTypes.Length == 0)
                return new GraphSnapshot(string.Empty, Array.Empty<Node>());

            var ruleVersion = string.Empty;
            var nodes = new List<Node>();
            foreach (var graphType in graphTypes)
            {
                var currentRuleVersion = ReadRuleVersion(graphType);
                if (string.IsNullOrEmpty(currentRuleVersion))
                    continue;

                if (string.IsNullOrEmpty(ruleVersion))
                    ruleVersion = currentRuleVersion;
                else if (!string.Equals(ruleVersion, currentRuleVersion, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Compiled initialization graph rule version mismatch between generated graph assemblies. " +
                        $"Expected '{ruleVersion}', actual '{currentRuleVersion}' from '{graphType.Assembly.FullName}'.");

                nodes.AddRange(ReadNodes(graphType));
            }

            return string.IsNullOrEmpty(ruleVersion)
                ? new GraphSnapshot(string.Empty, Array.Empty<Node>())
                : new GraphSnapshot(ruleVersion, nodes);
        }

        private static string ReadRuleVersion(Type graphType)
        {
            var ruleVersionField = graphType.GetField(
                "RuleVersion",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return ruleVersionField?.GetRawConstantValue() as string
                   ?? ruleVersionField?.GetValue(null) as string
                   ?? string.Empty;
        }

        private static IEnumerable<Node> ReadNodes(Type graphType)
        {
            var nodesField = graphType.GetField(
                "Nodes",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (nodesField?.GetValue(null) is not System.Collections.IEnumerable generatedNodes)
                yield break;

            foreach (var generatedNode in generatedNodes)
            {
                if (generatedNode == null)
                    continue;

                var nodeType = generatedNode.GetType();
                yield return new Node(
                    ReadNodeProperty<Type>(nodeType, generatedNode, "ServiceType"),
                    ReadNodeProperty<Type>(nodeType, generatedNode, "ImplementationType"),
                    ReadNodeProperty<GameContextType>(nodeType, generatedNode, "Scope"),
                    ReadNodeProperty<Type[]>(nodeType, generatedNode, "Dependencies"));
            }
        }

        private static T ReadNodeProperty<T>(Type nodeType, object node, string propertyName)
        {
            var property = nodeType.GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.GetValue(node) is T value)
                return value;

            throw new InvalidOperationException(
                $"Generated compiled initialization graph node '{nodeType.FullName}' does not expose '{propertyName}'.");
        }
    }
}
