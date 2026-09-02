using System;
using System.Collections.Generic;
using System.Linq;
using RuntimeFlow.Initialization.Graph;

namespace RuntimeFlow.Initialization.Planning
{
    /// <summary>
    /// The single topological planner of the framework: layers nodes by their in-plan
    /// dependencies (Kahn). Determinism: declaration order first, then ordinal name.
    /// Cycle detection reuses <see cref="DependencyCycleDetector"/> for a precise path message.
    /// Every topological ordering in the framework (scope waves, auto-service construction,
    /// DescribeStartupPlan) goes through here so plan and execution cannot diverge.
    /// </summary>
    internal static class LoadGraphTopology
    {
        /// <summary>
        /// Builds dependency layers over <paramref name="nodes"/>.
        /// Dependencies pointing outside the node set (e.g. already-initialized seeded
        /// services or parent-scope services) are treated as satisfied.
        /// Frontier semantics: every layer is evaluated against the placed set as it stood
        /// at the start of the sweep — a node becoming ready mid-sweep lands in the NEXT
        /// layer, never in the current one.
        /// </summary>
        public static IReadOnlyList<IReadOnlyList<LoadGraphNode>> BuildLayers(
            IReadOnlyList<LoadGraphNode> nodes)
        {
            if (nodes.Count == 0)
                return Array.Empty<IReadOnlyList<LoadGraphNode>>();

            var keys = new HashSet<Type>();
            foreach (var node in nodes) keys.Add(node.Key);

            var pending = new Queue<LoadGraphNode>(nodes);
            var placedKeys = new HashSet<Type>();
            var remaining = nodes.Count;
            var guard = nodes.Count + 1;
            var layers = new List<IReadOnlyList<LoadGraphNode>>();

            while (remaining > 0)
            {
                if (guard-- <= 0)
                    ThrowCycle(pending);

                // Snapshot the placed frontier before evaluating anything in this sweep.
                var frontier = new HashSet<Type>(placedKeys);
                var current = new List<LoadGraphNode>(pending.Count);
                var deferred = new List<LoadGraphNode>(pending.Count);
                while (pending.Count > 0)
                {
                    var candidate = pending.Dequeue();
                    if (IsSatisfied(candidate, keys, frontier)) current.Add(candidate);
                    else deferred.Add(candidate);
                }

                if (current.Count == 0)
                {
                    foreach (var d in deferred) pending.Enqueue(d);
                    ThrowCycle(pending);
                }

                foreach (var node in current)
                {
                    placedKeys.Add(node.Key);
                    remaining--;
                }
                foreach (var d in deferred) pending.Enqueue(d);

                layers.Add(current);
            }

            return layers;
        }

        private static bool IsSatisfied(LoadGraphNode node, HashSet<Type> keys, HashSet<Type> placedKeys)
        {
            foreach (var dep in node.Dependencies)
                if (keys.Contains(dep) && !placedKeys.Contains(dep))
                    return false;
            return true;
        }

        private static void ThrowCycle(Queue<LoadGraphNode> stalled)
        {
            var graph = new Dictionary<Type, IReadOnlyCollection<Type>>();
            foreach (var node in stalled)
            {
                var deps = new List<Type>();
                foreach (var dep in node.Dependencies)
                    if (stalled.Any(s => s.Key == dep)) deps.Add(dep);
                graph[node.Key] = deps;
            }
            var cyclePath = DependencyCycleDetector.DetectCyclePath(graph);
            var unresolvedNames = string.Join(", ", stalled.Select(n => n.DisplayName).Distinct());
            var cycleDesc = cyclePath != null
                ? $"Cycle: {string.Join(" -> ", cyclePath.Select(t => t.Name))}. "
                : string.Empty;
            throw new InvalidOperationException(
                $"Load graph contains a dependency cycle. {cycleDesc}Remaining nodes: {unresolvedNames}");
        }
    }
}
