using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
            // An unreadable lifetime adapter must not leak the failed container: fall back to the graph's
            // own async services and VContainer's plain disposal of everything it tracks.
            var ownership = MixedTeardown.TryOwn(scope, logger, name, protectedInstances, refreshProtected);
            if (ownership == null)
            {
                await MixedTeardown.ReleaseUntrackedAsync(scope, services, logger, name, null,
                    protectedInstances, refreshProtected);
                return;
            }
            try
            {
                await MixedTeardown.ReleaseAsync(scope, services, logger, name, ownsScope: true, ownership: ownership);
            }
            catch (Exception exception) { MixedTeardown.Log(logger, name, "constructed services", exception); }
            finally
            {
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
            // Each service's dependency set is computed once, so ordering stays near-linear in the edges.
            var dependencies = DirectDependencies(services);
            var components = Components(services, dependencies);
            foreach (var component in components)
                component.Sort((left, right) => priority[services[left]].CompareTo(priority[services[right]]));

            var componentOf = new int[services.Count];
            for (var c = 0; c < components.Count; c++)
                foreach (var member in components[c]) componentOf[member] = c;

            // A component is released once no remaining component depends on it; among those the
            // earliest priority wins (ties keep component discovery order, as a linear scan did).
            var targets = new List<int>[components.Count];
            var dependents = new int[components.Count];
            for (var c = 0; c < components.Count; c++)
            {
                var distinct = new HashSet<int>();
                foreach (var member in components[c])
                    foreach (var dependency in dependencies[member])
                    {
                        var target = componentOf[dependency];
                        if (target != c && distinct.Add(target)) dependents[target]++;
                    }
                targets[c] = new List<int>(distinct);
            }
            var ready = new SortedSet<(int Rank, int Component)>();
            for (var c = 0; c < components.Count; c++)
                if (dependents[c] == 0) ready.Add((priority[services[components[c][0]]], c));

            var order = new List<ServiceNode>();
            var released = new HashSet<object>(InstanceIdentity.Instance);
            while (ready.Count > 0)
            {
                var next = ready.Min; // The component graph is always acyclic.
                ready.Remove(next);
                foreach (var member in components[next.Component])
                {
                    var node = services[member];
                    if (node.Instance == null || !released.Add(node.Instance)) continue;
                    order.Add(node);
                }
                foreach (var target in targets[next.Component])
                    if (--dependents[target] == 0) ready.Add((priority[services[components[target][0]]], target));
            }
            return order;
        }

        /// <summary>
        /// For every service, the indices of the services it depends on in the sense of
        /// <see cref="DependsOn"/>: one walk per service through synthetic barriers, matched by node
        /// identity or by physical instance identity. May contain the service's own index.
        /// </summary>
        private static List<int>[] DirectDependencies(IReadOnlyList<ServiceNode> services)
        {
            var byNode = new Dictionary<ServiceNode, List<int>>();
            var byInstance = new Dictionary<object, List<int>>(InstanceIdentity.Instance);
            for (var i = 0; i < services.Count; i++)
            {
                Group(byNode, services[i], i);
                if (services[i].Instance != null) Group(byInstance, services[i].Instance!, i);
            }

            var result = new List<int>[services.Count];
            var pending = new Stack<ServiceNode>();
            var seen = new HashSet<ServiceNode>();
            var found = new HashSet<int>();
            for (var i = 0; i < services.Count; i++)
            {
                pending.Clear();
                seen.Clear();
                found.Clear();
                foreach (var edge in services[i].Deps) pending.Push(edge.Target);
                while (pending.Count > 0)
                {
                    var dependency = pending.Pop();
                    if (dependency.Kind == NodeKind.External || !seen.Add(dependency)) continue;
                    if (byNode.TryGetValue(dependency, out var same)) found.UnionWith(same);
                    if (dependency.Instance != null && byInstance.TryGetValue(dependency.Instance, out var aliases))
                        found.UnionWith(aliases);
                    // Same traversal as DependsOn: only synthetic barriers are walked through.
                    if (dependency.Kind != NodeKind.Barrier) continue;
                    foreach (var edge in dependency.Deps) pending.Push(edge.Target);
                }
                var sorted = new List<int>(found);
                sorted.Sort();
                result[i] = sorted;
            }
            return result;
        }

        private static void Group<TKey>(Dictionary<TKey, List<int>> groups, TKey key, int index) where TKey : notnull
        {
            if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = new List<int>());
            group.Add(index);
        }

        /// <summary>Strongly connected services (dependencies and physical aliases), as service indices.</summary>
        private static List<List<int>> Components(IReadOnlyList<ServiceNode> services, List<int>[] dependencies)
        {
            var byInstance = new Dictionary<object, List<int>>(InstanceIdentity.Instance);
            for (var i = 0; i < services.Count; i++)
                if (services[i].Instance != null) Group(byInstance, services[i].Instance!, i);

            var links = new List<int>[services.Count];
            var linked = new HashSet<int>();
            for (var i = 0; i < services.Count; i++)
            {
                linked.Clear();
                foreach (var j in dependencies[i]) if (j != i) linked.Add(j);
                // Aliases are one cleanup vertex: all of their discovered edges must be retained.
                if (services[i].Instance != null)
                    foreach (var j in byInstance[services[i].Instance!]) if (j != i) linked.Add(j);
                var sorted = new List<int>(linked);
                sorted.Sort();
                links[i] = sorted;
            }

            // Tarjan's algorithm with an explicit call stack, so a long dependency chain cannot
            // overflow the thread stack. Components come out in the same order as the recursive form.
            var components = new List<List<int>>();
            var indices = new int[services.Count];
            var low = new int[services.Count];
            var active = new bool[services.Count];
            var stack = new Stack<int>();
            var calls = new Stack<(int Node, int Next)>();
            for (var i = 0; i < indices.Length; i++) indices[i] = -1;
            var index = 0;
            for (var root = 0; root < services.Count; root++)
            {
                if (indices[root] >= 0) continue;
                Enter(root);
                while (calls.Count > 0)
                {
                    var (current, next) = calls.Pop();
                    if (next < links[current].Count)
                    {
                        calls.Push((current, next + 1));
                        var target = links[current][next];
                        if (indices[target] < 0) Enter(target);
                        else if (active[target]) low[current] = Math.Min(low[current], indices[target]);
                        continue;
                    }
                    if (low[current] == indices[current])
                    {
                        var component = new List<int>();
                        int member;
                        do
                        {
                            member = stack.Pop();
                            active[member] = false;
                            component.Add(member);
                        } while (member != current);
                        components.Add(component);
                    }
                    if (calls.Count > 0)
                    {
                        var caller = calls.Peek().Node;
                        low[caller] = Math.Min(low[caller], low[current]);
                    }
                }
            }
            return components;

            void Enter(int node)
            {
                indices[node] = low[node] = index++;
                stack.Push(node);
                active[node] = true;
                calls.Push((node, 0));
            }
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

        /// <summary>Reference identity for physical instances, whatever their own Equals says.</summary>
        internal sealed class InstanceIdentity : IEqualityComparer<object>
        {
            public static readonly InstanceIdentity Instance = new InstanceIdentity();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
