using System;
using System.Collections.Generic;
using System.Linq;

namespace RuntimeFlow.Initialization.Graph
{
    /// <summary>
    /// Detects and traces dependency cycles in the service initialization graph
    /// using DFS with three-color marking (white/grey/black).
    /// </summary>
    internal static class DependencyCycleDetector
    {
        /// <summary>
        /// Finds a specific cycle path in the dependency graph formed by pending services.
        /// Returns the cycle as a list of types (A → B → C → A) or null if no cycle is found.
        /// </summary>
        internal static IReadOnlyList<Type>? DetectCyclePath(
            IReadOnlyDictionary<Type, IReadOnlyCollection<Type>> dependencyGraph)
        {
            var white = new HashSet<Type>(dependencyGraph.Keys); // unvisited
            var grey = new HashSet<Type>(); // in current DFS path
            var parent = new Dictionary<Type, Type>(); // for path reconstruction

            foreach (var node in dependencyGraph.Keys)
            {
                if (!white.Contains(node))
                    continue;

                var cycleEnd = Dfs(node, dependencyGraph, white, grey, parent);
                if (cycleEnd != null)
                    return ReconstructCycle(cycleEnd.Value.from, cycleEnd.Value.to, parent);
            }

            return null;
        }

        private static (Type from, Type to)? Dfs(
            Type current,
            IReadOnlyDictionary<Type, IReadOnlyCollection<Type>> graph,
            HashSet<Type> white,
            HashSet<Type> grey,
            Dictionary<Type, Type> parent)
        {
            white.Remove(current);
            grey.Add(current);

            if (graph.TryGetValue(current, out var dependencies))
            {
                foreach (var dep in dependencies)
                {
                    // Only consider dependencies that are in the pending graph
                    if (!graph.ContainsKey(dep))
                        continue;

                    if (grey.Contains(dep))
                    {
                        // Back edge found — cycle detected
                        parent[dep] = current;
                        return (current, dep);
                    }

                    if (!white.Contains(dep))
                        continue;

                    parent[dep] = current;
                    var result = Dfs(dep, graph, white, grey, parent);
                    if (result != null)
                        return result;
                }
            }

            grey.Remove(current);
            return null;
        }

        private static IReadOnlyList<Type> ReconstructCycle(Type from, Type cycleStart, Dictionary<Type, Type> parent)
        {
            var path = new List<Type> { cycleStart };
            var current = from;

            while (current != cycleStart)
            {
                path.Add(current);
                if (!parent.TryGetValue(current, out current))
                    break;
            }

            path.Add(cycleStart); // close the cycle
            path.Reverse();
            return path;
        }
    }
}
