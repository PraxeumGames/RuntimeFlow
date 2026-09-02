using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using VContainer;

namespace RuntimeFlow.Internal
{
    /// <summary>The validated initialization graph of one scope.</summary>
    internal sealed class ServiceGraph
    {
        public ServiceGraph(
            string scope,
            IReadOnlyList<ServiceNode> nodes,
            IReadOnlyList<ServiceNode> services,
            IReadOnlyList<ServiceNode> externals,
            IReadOnlyList<string> phases)
        {
            Scope = scope;
            Nodes = nodes;
            Services = services;
            Externals = externals;
            Phases = phases;
        }

        /// <summary>Name of the scope this graph belongs to.</summary>
        public string Scope { get; }

        /// <summary>Services and phase barriers, ordered by node index.</summary>
        public IReadOnlyList<ServiceNode> Nodes { get; }

        /// <summary>Only the real services of this scope, in registration order.</summary>
        public IReadOnlyList<ServiceNode> Services { get; }

        /// <summary>Services of parent scopes; already initialized, never scheduled.</summary>
        public IReadOnlyList<ServiceNode> Externals { get; }

        /// <summary>Declared phase names in order; empty when the run has no barriers.</summary>
        public IReadOnlyList<string> Phases { get; }
    }

    /// <summary>
    /// Discovers the initializable services of a scope through its VContainer registrations and turns
    /// them into a validated DAG of constructor, <see cref="DependsOnAttribute"/> and phase-barrier edges.
    /// </summary>
    internal static class GraphBuilder
    {
        /// <summary>Builds and validates the graph of <paramref name="scope"/>; throws <see cref="InitGraphException"/> on any problem.</summary>
        public static ServiceGraph Build(
            IObjectResolver scope,
            string name,
            RuntimeFlowOptions options,
            IReadOnlyList<ServiceGraph>? parents)
        {
            var declaredPhases = options.Phases ?? Array.Empty<string>();
            if (options.DefaultPhase != null && !Contains(declaredPhases, options.DefaultPhase))
            {
                throw new InitGraphException(name,
                    $"RuntimeFlowOptions.DefaultPhase is '{options.DefaultPhase}', but RuntimeFlowOptions.Phases is {Bracket(declaredPhases)}.");
            }

            var registrations = LocalRegistrations(scope, name);
            var services = new List<ServiceNode>();
            var sources = new List<Type>();
            var index = 0;

            foreach (var registration in registrations)
            {
                if (registration.Lifetime != Lifetime.Singleton)
                {
                    throw new InitGraphException(name,
                        $"{registration.ImplementationType.Name} is registered with Lifetime.{registration.Lifetime} in scope '{name}'; " +
                        "initializable services must be Lifetime.Singleton (a Scoped/Transient service would be re-created uninitialized in child scopes).");
                }

                object? instance = null;
                Exception? constructionError = null;
                try
                {
                    instance = scope.Resolve(registration);
                }
                catch (Exception exception)
                {
                    constructionError = new InitGraphException(name,
                        $"Could not construct {registration.ImplementationType.Name} in scope '{name}': {exception.Message}. " +
                        $"Register the missing type in '{name}' or a parent scope.", exception);
                }

                var source = instance?.GetType() ?? registration.ImplementationType;
                var node = new ServiceNode(index++, NodeKind.Service, source.Name, source, name)
                {
                    Instance = instance,
                    Service = instance as IAsyncInitializable,
                    ConstructionError = constructionError
                };
                services.Add(node);
                sources.Add(registration.ImplementationType);
            }

            var externals = Externals(scope, name, parents);
            Disambiguate(services, externals);
            WarnAboutDuplicates(options.Logger, name, services);

            var all = new List<ServiceNode>(services.Count + externals.Count);
            all.AddRange(services);
            all.AddRange(externals);

            // Phases are declared globally but belong to the scopes that actually label a service: a scope
            // whose services carry no [Init(Phase = …)] runs without barriers instead of being swept into
            // the last phase. The "unmarked lands in the last phase" rule applies from the first label on.
            var phases = AnyPhaseDeclared(services, sources) ? declaredPhases : Array.Empty<string>();

            for (var i = 0; i < services.Count; i++)
                ReadAttributes(services[i], sources[i], name, phases, options.DefaultPhase);

            for (var i = 0; i < services.Count; i++)
                AddConstructorEdges(services[i], sources[i], all);

            for (var i = 0; i < services.Count; i++)
                AddDependsOnEdges(services[i], sources[i], name, all, services, externals);

            var nodes = new List<ServiceNode>(services);
            if (phases.Count > 0) AddPhaseBarriers(nodes, services, phases, name, ref index);

            Link(nodes);
            DetectCycles(nodes, name);

            return new ServiceGraph(name, nodes, services, externals, phases);
        }

        private static List<Registration> LocalRegistrations(IObjectResolver scope, string name)
        {
            var result = new List<Registration>();
            if (!scope.TryGetRegistration(typeof(IReadOnlyList<IAsyncInitializable>), out var registration))
                return result;

            if (!(registration.Provider is IEnumerable<Registration> local))
            {
                throw new InitGraphException(name,
                    $"Unsupported VContainer version: cannot enumerate the initializable registrations of scope '{name}'.");
            }

            result.AddRange(local);
            return result;
        }

        private static List<ServiceNode> Externals(IObjectResolver scope, string name, IReadOnlyList<ServiceGraph>? parents)
        {
            var externals = new List<ServiceNode>();
            var index = -1;

            if (parents != null && parents.Count > 0)
            {
                foreach (var parent in parents)
                {
                    foreach (var node in parent.Services)
                    {
                        externals.Add(new ServiceNode(index--, NodeKind.External, node.Name, node.Type, parent.Scope)
                        {
                            Instance = node.Instance,
                            State = ExternalState(node)
                        });
                    }
                }
                return externals;
            }

            var current = (scope as IScopedObjectResolver)?.Parent;
            var depth = 0;
            while (current != null)
            {
                var scopeName = "parent" + (depth == 0 ? string.Empty : "^" + depth.ToString(CultureInfo.InvariantCulture));
                foreach (var registration in LocalRegistrations(current, name))
                {
                    externals.Add(new ServiceNode(index--, NodeKind.External,
                        registration.ImplementationType.Name, registration.ImplementationType, scopeName)
                    {
                        State = ServiceState.Completed
                    });
                }
                current = current.Parent;
                depth++;
            }
            return externals;
        }

        /// <summary>
        /// State an external node inherits from its parent-scope node: a service that degraded there stays
        /// degraded here, so this scope's services see it in <see cref="InitContext.DegradedServices"/> and
        /// in <c>Describe()</c>. Everything else counts as initialized — externals are never scheduled.
        /// </summary>
        private static ServiceState ExternalState(ServiceNode node)
            => node.State == ServiceState.Degraded ? ServiceState.Degraded : ServiceState.Completed;

        private static void Disambiguate(List<ServiceNode> services, List<ServiceNode> externals)
        {
            var groups = new Dictionary<string, List<ServiceNode>>(StringComparer.Ordinal);
            foreach (var node in services.Concat(externals))
            {
                if (!groups.TryGetValue(node.Name, out var group))
                    groups[node.Name] = group = new List<ServiceNode>();
                group.Add(node);
            }

            foreach (var group in groups.Values)
            {
                var distinct = new HashSet<Type>();
                foreach (var node in group) distinct.Add(node.Type);
                if (distinct.Count <= 1) continue;
                foreach (var node in group) node.Name = node.Type.FullName ?? node.Type.Name;
            }
        }

        private static void WarnAboutDuplicates(ILogger logger, string scope, List<ServiceNode> services)
        {
            var counts = new Dictionary<Type, int>();
            foreach (var node in services)
                counts[node.Type] = counts.TryGetValue(node.Type, out var count) ? count + 1 : 1;

            foreach (var node in services)
            {
                if (!counts.TryGetValue(node.Type, out var count) || count < 2) continue;
                counts.Remove(node.Type);
                logger.Warn($"[RuntimeFlow] {scope}: {node.Name} is registered as IAsyncInitializable " +
                            $"{count.ToString(CultureInfo.InvariantCulture)} times; each registration will be initialized.");
            }
        }

        private static bool AnyPhaseDeclared(List<ServiceNode> services, List<Type> sources)
        {
            for (var i = 0; i < services.Count; i++)
            {
                if (InitAttributeOf(services[i], sources[i])?.Phase != null) return true;
            }
            return false;
        }

        private static InitAttribute? InitAttributeOf(ServiceNode node, Type registeredType)
            => node.Type.GetCustomAttribute<InitAttribute>(true)
               ?? registeredType.GetCustomAttribute<InitAttribute>(true);

        private static void ReadAttributes(
            ServiceNode node,
            Type registeredType,
            string scope,
            IReadOnlyList<string> phases,
            string? defaultPhase)
        {
            var attribute = InitAttributeOf(node, registeredType);
            if (attribute != null)
            {
                node.Optional = attribute.Optional;
                node.UserGated = attribute.UserGated;
                node.TimeoutSeconds = attribute.TimeoutSeconds;
                node.Weight = attribute.Weight;

                if (attribute.UserGated && attribute.TimeoutSeconds > 0)
                {
                    throw new InitGraphException(scope,
                        $"{node.Name} is user-gated and declares TimeoutSeconds = {Fmt.N(attribute.TimeoutSeconds)}; " +
                        "user-gated services never time out. Remove one of them.");
                }

                if (attribute.Phase != null)
                {
                    if (phases.Count == 0)
                    {
                        throw new InitGraphException(scope,
                            $"{node.Name} declares phase '{attribute.Phase}', but RuntimeFlowOptions.Phases is empty. " +
                            "Declare the ordered phase list in RuntimeFlowOptions.Phases.");
                    }
                    if (!Contains(phases, attribute.Phase))
                    {
                        throw new InitGraphException(scope,
                            $"{node.Name} declares phase '{attribute.Phase}', but RuntimeFlowOptions.Phases is {Bracket(phases)}.");
                    }
                }
            }

            if (phases.Count == 0) return;

            var phase = attribute?.Phase ?? defaultPhase ?? phases[phases.Count - 1];
            node.Phase = phase;
            node.PhaseIndex = IndexOf(phases, phase);
        }

        private static void AddConstructorEdges(ServiceNode node, Type registeredType, List<ServiceNode> all)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in InspectedTypes(node, registeredType))
            {
                foreach (var parameter in ConstructorEdges.Parameters(type))
                {
                    var parameterType = parameter.ParameterType;
                    if (!seen.Add(parameterType.FullName + " " + parameter.Name)) continue;

                    if (ConstructorEdges.IsLazy(parameterType))
                    {
                        node.LazyParameters.Add($"{Fmt.Type(parameterType)} {parameter.Name}");
                        continue;
                    }
                    if (ConstructorEdges.IsIgnored(parameterType)) continue;

                    var target = ConstructorEdges.ElementType(parameterType) ?? parameterType;
                    if (ConstructorEdges.IsIgnored(target)) continue;

                    var origin = $"ctor: {Fmt.Type(parameterType)} {parameter.Name}";
                    foreach (var candidate in all)
                    {
                        if (!ReferenceEquals(candidate, node) && target.IsAssignableFrom(candidate.Type))
                            AddEdge(node, candidate, origin);
                    }
                }
            }
        }

        private static IEnumerable<Type> InspectedTypes(ServiceNode node, Type registeredType)
        {
            yield return node.Type;
            if (registeredType != node.Type && registeredType.IsClass && !registeredType.IsAbstract)
                yield return registeredType;
        }

        private static void AddDependsOnEdges(
            ServiceNode node,
            Type registeredType,
            string scope,
            List<ServiceNode> all,
            List<ServiceNode> services,
            List<ServiceNode> externals)
        {
            var attributes = new List<DependsOnAttribute>(node.Type.GetCustomAttributes<DependsOnAttribute>(true));
            if (registeredType != node.Type)
                attributes.AddRange(registeredType.GetCustomAttributes<DependsOnAttribute>(true));

            foreach (var attribute in attributes)
            {
                var matched = false;
                foreach (var candidate in all)
                {
                    if (ReferenceEquals(candidate, node) || !attribute.ServiceType.IsAssignableFrom(candidate.Type)) continue;
                    AddEdge(node, candidate, "DependsOn");
                    matched = true;
                }

                if (!matched)
                {
                    throw new InitGraphException(scope,
                        $"{node.Name} declares [DependsOn(typeof({attribute.ServiceType.Name}))], but no initializable service " +
                        $"assignable to {attribute.ServiceType.Name} is registered in scope '{scope}' or its parents. " +
                        "A target must implement IAsyncInitializable and live in the same scope or a parent scope; " +
                        "a parent scope can never depend on a child scope. " +
                        KnownServices(scope, services, externals));
                }
            }
        }

        private static string KnownServices(string scope, List<ServiceNode> services, List<ServiceNode> externals)
        {
            var groups = new List<string>();
            var own = services.Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (own.Count > 0) groups.Add($"{scope}: {string.Join(", ", own)}");

            foreach (var group in externals.GroupBy(n => n.Scope, StringComparer.Ordinal))
            {
                var names = group.Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
                if (names.Count > 0) groups.Add($"{group.Key}: {string.Join(", ", names)}");
            }

            return groups.Count == 0
                ? "Known services — none."
                : "Known services — " + string.Join("; ", groups) + ".";
        }

        private static void AddPhaseBarriers(
            List<ServiceNode> nodes,
            List<ServiceNode> services,
            IReadOnlyList<string> phases,
            string scope,
            ref int index)
        {
            var barriers = new ServiceNode[phases.Count];
            for (var i = 0; i < phases.Count; i++)
            {
                barriers[i] = new ServiceNode(index++, NodeKind.Barrier, "phase:" + phases[i], typeof(void), scope)
                {
                    Phase = phases[i],
                    PhaseIndex = i,
                    Weight = 0
                };
                nodes.Add(barriers[i]);
            }

            for (var i = 0; i < phases.Count; i++)
            {
                if (i > 0) AddEdge(barriers[i], barriers[i - 1], "phase barrier");
                foreach (var service in services)
                {
                    if (service.PhaseIndex != i) continue;
                    AddEdge(barriers[i], service, "phase barrier");
                    if (i > 0) AddEdge(service, barriers[i - 1], "phase barrier");
                }
            }
        }

        private static void AddEdge(ServiceNode from, ServiceNode to, string origin)
        {
            foreach (var edge in from.Deps)
            {
                if (ReferenceEquals(edge.Target, to)) return;
            }
            from.Deps.Add(new Edge(to, origin));
        }

        private static void Link(List<ServiceNode> nodes)
        {
            foreach (var node in nodes)
            {
                node.PendingDeps = 0;
                foreach (var edge in node.Deps)
                {
                    if (edge.Target.Kind == NodeKind.External) continue;
                    node.PendingDeps++;
                    edge.Target.Dependents.Add(node);
                }
            }
        }

        private static void DetectCycles(List<ServiceNode> nodes, string scope)
        {
            var remaining = new Dictionary<ServiceNode, int>();
            var ready = new Queue<ServiceNode>();
            foreach (var node in nodes)
            {
                remaining[node] = node.PendingDeps;
                if (node.PendingDeps == 0) ready.Enqueue(node);
            }

            var settled = 0;
            while (ready.Count > 0)
            {
                var node = ready.Dequeue();
                settled++;
                foreach (var dependent in node.Dependents)
                {
                    if (--remaining[dependent] == 0) ready.Enqueue(dependent);
                }
            }

            if (settled == nodes.Count) return;

            var leftover = new HashSet<ServiceNode>(nodes.Where(n => remaining[n] > 0));
            throw new InitGraphException(scope, CycleMessage(scope, leftover));
        }

        private static string CycleMessage(string scope, HashSet<ServiceNode> leftover)
        {
            var path = new List<ServiceNode>();
            var seen = new Dictionary<ServiceNode, int>();
            var current = leftover.OrderBy(n => n.Index).First();

            while (!seen.ContainsKey(current))
            {
                seen[current] = path.Count;
                path.Add(current);
                ServiceNode? next = null;
                foreach (var edge in current.Deps)
                {
                    if (leftover.Contains(edge.Target)) { next = edge.Target; break; }
                }
                if (next == null) break;
                current = next;
            }

            var start = seen.TryGetValue(current, out var at) ? at : 0;
            var cycle = path.Skip(start).ToList();
            cycle.Add(cycle[0]);

            var text = new StringBuilder();
            text.Append("Initialization graph of scope '").Append(scope).Append("' has a cycle: ");
            text.Append(string.Join(" -> ", cycle.Select(n => n.DisplayName))).Append(". Edges: ");

            var edges = new List<string>();
            string? annotation = null;
            for (var i = 0; i + 1 < cycle.Count; i++)
            {
                var from = cycle[i];
                var to = cycle[i + 1];
                var origin = from.Deps.First(e => ReferenceEquals(e.Target, to)).Origin;
                edges.Add($"{from.DisplayName} -> {to.DisplayName} ({origin})");

                if (annotation == null && from.Kind == NodeKind.Service && to.Kind == NodeKind.Service
                    && from.PhaseIndex >= 0 && to.PhaseIndex > from.PhaseIndex)
                {
                    annotation = $" ({from.Name} in phase '{from.Phase}' depends on {to.Name} in phase '{to.Phase}'; " +
                                 "a service cannot depend on a later phase)";
                }
            }

            text.Append(string.Join(", ", edges)).Append('.');
            if (annotation != null) text.Append(annotation).Append('.');
            text.Append(" Remove one dependency or take it lazily (Func<T>/ILazy<T> parameters are not edges).");
            return text.ToString();
        }

        private static bool Contains(IReadOnlyList<string> values, string value)
        {
            for (var i = 0; i < values.Count; i++)
            {
                if (string.Equals(values[i], value, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static int IndexOf(IReadOnlyList<string> values, string value)
        {
            for (var i = 0; i < values.Count; i++)
            {
                if (string.Equals(values[i], value, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        private static string Bracket(IReadOnlyList<string> values) => "[" + string.Join(", ", values) + "]";
    }
}
