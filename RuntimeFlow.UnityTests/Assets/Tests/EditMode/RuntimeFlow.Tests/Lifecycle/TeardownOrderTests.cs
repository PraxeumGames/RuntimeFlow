using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using RuntimeFlow.Internal;

namespace RuntimeFlow.Tests.Lifecycle
{
    /// <summary>
    /// <see cref="ConstructionRollback.TeardownOrder"/> on synthetic graphs: it must scale to large scopes
    /// and produce exactly the order of the straightforward pairwise planner it replaced.
    /// </summary>
    [TestFixture]
    public sealed class TeardownOrderTests
    {
        private static ServiceNode Service(int index, object? instance)
            => new ServiceNode(index, NodeKind.Service, "s" + index, typeof(object), "test") { Instance = instance };

        private static void Depend(ServiceNode from, ServiceNode to)
        {
            if (from.DepTargets.Add(to)) from.Deps.Add(new Edge(to, "test"));
        }

        [TestCase(500, 0)]
        [TestCase(2000, 0)]
        [TestCase(500, 5)]
        public void ALongChainIsOrderedQuickly(int count, int phases)
        {
            var services = new List<ServiceNode>();
            for (var i = 0; i < count; i++)
            {
                services.Add(Service(i, new object()));
                if (i > 0) Depend(services[i], services[i - 1]);
            }
            // Optional barrier chain: every service of phase k waits for barrier k-1, which waits for phase k-1.
            if (phases > 0)
            {
                var perPhase = count / phases;
                ServiceNode? previous = null;
                for (var p = 0; p < phases; p++)
                {
                    var barrier = new ServiceNode(count + p, NodeKind.Barrier, "phase:" + p, typeof(void), "test");
                    if (previous != null) Depend(barrier, previous);
                    for (var i = p * perPhase; i < (p + 1) * perPhase; i++)
                    {
                        Depend(barrier, services[i]);
                        if (previous != null) Depend(services[i], previous);
                    }
                    previous = barrier;
                }
            }
            var preferred = new List<ServiceNode>(services); // Completion order reversed would be the usual input.

            var clock = Stopwatch.StartNew();
            var order = ConstructionRollback.TeardownOrder(services, preferred);
            clock.Stop();

            Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)), $"ordering {count} services took {clock.Elapsed}");
            Assert.That(order.Count, Is.EqualTo(count));
            for (var i = 0; i < count; i++)
                Assert.That(order[i], Is.SameAs(services[count - 1 - i]), "every dependent is released before its dependency");
        }

        [Test]
        public void MatchesThePairwiseReferenceOnRandomGraphs()
        {
            for (var seed = 1; seed <= 400; seed++)
            {
                var random = new Random(seed);
                var (services, preferred) = RandomGraph(random);

                var expected = ReferenceOrder(services, preferred);
                var actual = ConstructionRollback.TeardownOrder(services, preferred);

                Assert.That(actual.Select(n => n.Index), Is.EqualTo(expected.Select(n => n.Index)), $"seed {seed}");
                for (var i = 0; i < actual.Count; i++)
                    Assert.That(actual[i], Is.SameAs(expected[i]), $"seed {seed}, position {i}");
            }
        }

        private static (List<ServiceNode> Services, List<ServiceNode>? Preferred) RandomGraph(Random random)
        {
            var count = random.Next(1, 22);
            var acyclic = random.Next(2) == 0;
            var services = new List<ServiceNode>();
            var instances = new List<object>();
            for (var i = 0; i < count; i++)
            {
                object? instance;
                var roll = random.NextDouble();
                if (roll < 0.15) instance = null;
                else if (roll < 0.35 && instances.Count > 0) instance = instances[random.Next(instances.Count)];
                else instances.Add(instance = new object());
                services.Add(Service(i, instance));
            }

            var barriers = new List<ServiceNode>();
            for (var b = random.Next(0, 4); b > 0; b--)
                barriers.Add(new ServiceNode(100 + barriers.Count, NodeKind.Barrier, "phase:" + barriers.Count, typeof(void), "test"));
            var externals = new List<ServiceNode>();
            for (var e = random.Next(0, 3); e > 0; e--)
            {
                var external = new ServiceNode(-1 - externals.Count, NodeKind.External, "x" + externals.Count, typeof(object), "parent")
                { Instance = random.Next(2) == 0 && instances.Count > 0 ? instances[random.Next(instances.Count)] : new object() };
                // An external's own edges must never be followed.
                if (services.Count > 0) Depend(external, services[random.Next(services.Count)]);
                externals.Add(external);
            }

            // Copies stand in for teardown-plan nodes that point at original graph nodes by instance.
            var originals = new List<ServiceNode>();
            foreach (var service in services)
                if (service.Instance != null && random.NextDouble() < 0.2)
                    originals.Add(new ServiceNode(200 + originals.Count, NodeKind.Service, "o" + originals.Count, typeof(object), "test")
                    { Instance = service.Instance });

            var targets = new List<ServiceNode>();
            targets.AddRange(services);
            targets.AddRange(barriers);
            targets.AddRange(externals);
            targets.AddRange(originals);
            var density = random.NextDouble() * 0.3;
            foreach (var node in services.Concat(barriers).Concat(originals))
                foreach (var target in targets)
                {
                    if (ReferenceEquals(node, target) || random.NextDouble() >= density) continue;
                    if (acyclic && node.Kind == NodeKind.Service && target.Kind == NodeKind.Service && target.Index >= node.Index) continue;
                    Depend(node, target);
                }

            // Rarely the same node is listed twice, which must not change tie-breaking.
            if (services.Count > 1 && random.NextDouble() < 0.1)
                services.Insert(random.Next(services.Count), services[random.Next(services.Count)]);

            List<ServiceNode>? preferred = null;
            if (random.Next(3) > 0)
            {
                preferred = new List<ServiceNode>();
                foreach (var service in services.OrderBy(_ => random.Next()))
                    if (random.NextDouble() < 0.7) preferred.Add(service);
                if (preferred.Count > 0 && random.NextDouble() < 0.3) preferred.Add(preferred[0]);
                if (originals.Count > 0 && random.NextDouble() < 0.3) preferred.Add(originals[0]);
            }
            return (services, preferred);
        }

        // ------------------------------------------------------------------ reference: the former planner

        private static List<ServiceNode> ReferenceOrder(IReadOnlyList<ServiceNode> services, IReadOnlyList<ServiceNode>? preferred)
        {
            var priority = new Dictionary<ServiceNode, int>();
            if (preferred != null)
                for (var i = 0; i < preferred.Count; i++)
                    if (!priority.ContainsKey(preferred[i])) priority.Add(preferred[i], priority.Count);
            for (var i = services.Count - 1; i >= 0; i--)
                if (!priority.ContainsKey(services[i])) priority.Add(services[i], priority.Count);

            var remaining = ReferenceComponents(services);
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
                    if (ReferenceHasDependent(remaining[i], remaining)) continue;
                    var rank = priority[remaining[i][0]];
                    if (rank >= earliest) continue;
                    next = i;
                    earliest = rank;
                }
                var chosen = remaining[next];
                foreach (var node in chosen)
                {
                    if (node.Instance == null || released.Any(r => ReferenceEquals(r, node.Instance))) continue;
                    released.Add(node.Instance);
                    order.Add(node);
                }
                remaining.RemoveAt(next);
            }
            return order;
        }

        private static List<List<ServiceNode>> ReferenceComponents(IReadOnlyList<ServiceNode> services)
        {
            var links = new List<int>[services.Count];
            for (var i = 0; i < services.Count; i++)
            {
                links[i] = new List<int>();
                for (var j = 0; j < services.Count; j++)
                {
                    if (i == j) continue;
                    var alias = services[i].Instance != null && ReferenceEquals(services[i].Instance, services[j].Instance);
                    if (alias || ConstructionRollback.DependsOn(services[i], services[j])) links[i].Add(j);
                }
            }

            var components = new List<List<ServiceNode>>();
            var indices = Enumerable.Repeat(-1, services.Count).ToArray();
            var low = new int[services.Count];
            var active = new bool[services.Count];
            var stack = new Stack<int>();
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

        private static bool ReferenceHasDependent(List<ServiceNode> component, List<List<ServiceNode>> remaining)
        {
            foreach (var candidate in remaining)
            {
                if (ReferenceEquals(candidate, component)) continue;
                foreach (var dependent in candidate)
                    foreach (var target in component)
                        if (ConstructionRollback.DependsOn(dependent, target)) return true;
            }
            return false;
        }
    }
}
