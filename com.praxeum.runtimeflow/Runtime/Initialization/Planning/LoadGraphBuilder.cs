using System;
using System.Collections.Generic;
using System.Linq;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Initialization.Planning
{
    /// <summary>
    /// Builds a <see cref="LoadGraphPlan"/> for one scope from the existing initializer
    /// discovery. Produces the same node set that execution sees (entry points, global
    /// bootstrap operations, async initializers) so plan inspection and runtime waves cannot
    /// diverge. Cross-scope dependencies are validated here once, uniformly.
    /// </summary>
    internal static class LoadGraphBuilder
    {
        public static LoadGraphPlan BuildPlan(
            GameContextType scope,
            GameContext context,
            IReadOnlyList<ServiceInitializerBinding> initializers,
            IReadOnlyList<GlobalBootstrapOperationBinding> globalOps,
            VContainerEntryPointsStartupPlan? entryPoints,
            ISet<Type> seededInitialized)
        {
            var nodes = new List<LoadGraphNode>();

            if (entryPoints != null)
                nodes.Add(new LoadGraphNode(
                    entryPoints.ProgressServiceType,
                    "entry-points",
                    LoadGraphNodeKind.EntryPoint,
                    dependencies: Array.Empty<Type>()));

            foreach (var op in globalOps)
                nodes.Add(new LoadGraphNode(
                    op.ImplementationType,
                    op.ImplementationType.Name,
                    LoadGraphNodeKind.GlobalBootstrapOperation,
                    depsAfterEntryPoints(op.ImplementationType),
                    weight: 1));

            foreach (var init in initializers)
                nodes.Add(new LoadGraphNode(
                    init.ServiceType,
                    init.ServiceType.Name,
                    LoadGraphNodeKind.Initializer,
                    init.Dependencies));

            // The entry-points node must precede everything in the scope plan.
            if (entryPoints != null)
            {
                var entryKey = entryPoints.ProgressServiceType;
                for (var i = 0; i < nodes.Count; i++)
                {
                    var node = nodes[i];
                    if (node.Kind == LoadGraphNodeKind.EntryPoint) continue;
                    if (!node.HasDependencyOn(entryKey))
                        nodes[i] = WithDependency(node, entryKey);
                }
            }

            ValidateUnknownDependencies(scope, context, nodes, seededInitialized);
            var layers = LoadGraphTopology.BuildLayers(nodes);

            var byKey = new Dictionary<Type, LoadGraphNode>();
            foreach (var node in layers.SelectMany(l => l))
                byKey[node.Key] = node;

            return new LoadGraphPlan(
                layers.SelectMany(l => l).ToList(),
                layers,
                byKey);
        }

        private static IReadOnlyCollection<Type> depsAfterEntryPoints(Type implType)
        {
            return InitializationGraphRules.ResolveConstructorDependencies(implType);
        }

        private static LoadGraphNode WithDependency(LoadGraphNode node, Type dependencyKey)
        {
            var merged = new List<Type>(node.Dependencies.Count + 1);
            bool has = false;
            foreach (var dep in node.Dependencies)
            {
                if (dep == dependencyKey) has = true;
                merged.Add(dep);
            }
            if (!has) merged.Add(dependencyKey);
            return new LoadGraphNode(node.Key, node.DisplayName, node.Kind, merged, node.Weight);
        }

        private static void ValidateUnknownDependencies(
            GameContextType scope,
            GameContext context,
            List<LoadGraphNode> nodes,
            ISet<Type> seededInitialized)
        {
            var known = new HashSet<Type>(nodes.Select(n => n.Key));
            foreach (var node in nodes)
            {
                foreach (var dep in node.Dependencies)
                {
                    if (known.Contains(dep)) continue;
                    if (seededInitialized.Contains(dep)) continue;
                    if (IsAvailableForResolution(context, dep)) continue;
                    throw new InvalidOperationException(
                        $"Load graph error in scope {scope}: '{node.DisplayName}' depends on '{dep.Name}', but no such service is registered or initialized in this or any parent scope.");
                }
            }
        }

        internal static bool IsAvailableForResolution(GameContext context, Type dependency)
        {
            var current = context;
            while (current != null)
            {
                if (current.IsRegistered(dependency))
                    return true;
                current = current.Parent as GameContext;
            }
            return false;
        }
    }
}
