using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VContainer;

namespace RuntimeFlow.Internal
{
    /// <summary>Owns a failed graph until its asynchronous services and resolver have been released.</summary>
    internal static class ConstructionRollback
    {
        public static async Task ReleaseAsync(IObjectResolver scope, List<ServiceNode> services, ILogger logger, string name,
            IReadOnlyList<object>? protectedInstances = null, bool ancestorEvidenceComplete = true,
            Func<bool>? refreshProtected = null)
        {
            if (!ancestorEvidenceComplete)
            {
                MixedTeardown.Log(logger, name, "scope", new InitGraphException(name,
                    "Cannot safely release a failed child scope: ancestor instance ownership could not be inspected."));
                return;
            }
            MixedTeardown.OwnedCleanup? ownership = null;
            try
            {
                ownership = new MixedTeardown.OwnedCleanup(scope, logger, name, protectedInstances, refreshProtected);
                await MixedTeardown.ReleaseAsync(scope, services, logger, name, ownsScope: true, ownership: ownership);
            }
            catch (Exception exception) { MixedTeardown.Log(logger, name, "constructed services", exception); }
            finally
            {
                if (ownership != null)
                    try { ownership.DrainScope(); }
                    catch (Exception exception) { MixedTeardown.Log(logger, name, "scope", exception); }
            }
        }

        /// <summary>
        /// Orders physical instances by discovered dependencies, using caller priority only among
        /// independent components or within an invalid cycle. Aliases retain all registration edges.
        /// </summary>
        internal static List<ServiceNode> TeardownOrder(IReadOnlyList<ServiceNode> services,
            IReadOnlyList<ServiceNode>? preferred = null)
        {
            var priority = new Dictionary<ServiceNode, int>();
            if (preferred != null)
                for (var i = 0; i < preferred.Count; i++)
                    if (!priority.ContainsKey(preferred[i])) priority.Add(preferred[i], priority.Count);
            for (var i = services.Count - 1; i >= 0; i--)
                if (!priority.ContainsKey(services[i])) priority.Add(services[i], priority.Count);

            // Registration and completion order can disagree with dependency lifetime. Collapse
            // strongly connected services and aliases before ordering their acyclic component graph.
            var remaining = Components(services);
            foreach (var component in remaining)
                component.Sort((left, right) => priority[left].CompareTo(priority[right]));
            var order = new List<ServiceNode>();
            var released = new List<object>();
            while (remaining.Count > 0)
            {
                var next = -1;
                var earliest = int.MaxValue;
                for (var i = 0; i < remaining.Count; i++)
                {
                    if (HasDependent(remaining[i], remaining)) continue;
                    var rank = priority[remaining[i][0]];
                    if (rank >= earliest) continue;
                    next = i;
                    earliest = rank;
                }
                var component = remaining[next]; // The component graph is always acyclic.
                foreach (var node in component)
                {
                    if (node.Instance == null || ContainsIdentity(released, node.Instance)) continue;
                    released.Add(node.Instance);
                    order.Add(node);
                }
                remaining.RemoveAt(next);
            }
            return order;
        }

        private static List<List<ServiceNode>> Components(IReadOnlyList<ServiceNode> services)
        {
            var links = new List<int>[services.Count];
            for (var i = 0; i < services.Count; i++)
            {
                links[i] = new List<int>();
                for (var j = 0; j < services.Count; j++)
                {
                    if (i == j) continue;
                    // Aliases are one cleanup vertex: all of their discovered edges must be retained.
                    var alias = services[i].Instance != null
                        && ReferenceEquals(services[i].Instance, services[j].Instance);
                    if (alias || DependsOn(services[i], services[j])) links[i].Add(j);
                }
            }

            var components = new List<List<ServiceNode>>();
            var indices = new int[services.Count];
            var low = new int[services.Count];
            var active = new bool[services.Count];
            var stack = new Stack<int>();
            for (var i = 0; i < indices.Length; i++) indices[i] = -1;
            var index = 0;
            void Visit(int current)
            {
                indices[current] = low[current] = index++;
                stack.Push(current);
                active[current] = true;
                foreach (var target in links[current])
                {
                    if (indices[target] < 0)
                    {
                        Visit(target);
                        low[current] = Math.Min(low[current], low[target]);
                    }
                    else if (active[target]) low[current] = Math.Min(low[current], indices[target]);
                }
                if (low[current] != indices[current]) return;
                var component = new List<ServiceNode>();
                int member;
                do
                {
                    member = stack.Pop();
                    active[member] = false;
                    component.Add(services[member]);
                } while (member != current);
                components.Add(component);
            }
            for (var i = 0; i < services.Count; i++) if (indices[i] < 0) Visit(i);
            return components;
        }

        private static bool HasDependent(List<ServiceNode> component, List<List<ServiceNode>> remaining)
        {
            foreach (var candidate in remaining)
            {
                if (ReferenceEquals(candidate, component)) continue;
                foreach (var dependent in candidate)
                    foreach (var target in component)
                        if (DependsOn(dependent, target)) return true;
            }
            return false;
        }

        internal static bool DependsOn(ServiceNode from, ServiceNode target)
        {
            var pending = new Stack<ServiceNode>();
            var seen = new HashSet<ServiceNode>();
            foreach (var edge in from.Deps) pending.Push(edge.Target);
            while (pending.Count > 0)
            {
                var dependency = pending.Pop();
                if (dependency.Kind == NodeKind.External || !seen.Add(dependency)) continue;
                if (ReferenceEquals(dependency, target)
                    || (target.Instance != null && ReferenceEquals(dependency.Instance, target.Instance))) return true;
                // Synthetic barriers have no disposable instance. Their local service prerequisites
                // remain semantic teardown dependencies, including earlier phase barrier chains.
                if (dependency.Kind != NodeKind.Barrier) continue;
                foreach (var edge in dependency.Deps) pending.Push(edge.Target);
            }
            return false;
        }

        private static bool ContainsIdentity(List<object> instances, object instance)
        {
            foreach (var candidate in instances) if (ReferenceEquals(candidate, instance)) return true;
            return false;
        }

    }
}
