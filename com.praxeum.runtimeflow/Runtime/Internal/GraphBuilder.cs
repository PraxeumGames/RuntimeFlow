using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
        /// <param name="scope">The resolver whose local registrations become the graph.</param>
        /// <param name="name">Scope name used in messages.</param>
        /// <param name="options">Phases, logger and option values to validate.</param>
        /// <param name="parents">Graphs of the linked parent runs, or null.</param>
        /// <param name="services">Caller-owned capture of constructed nodes, retained even on validation failure.</param>
        public static ServiceGraph Build(
            IObjectResolver scope,
            string name,
            RuntimeFlowOptions options,
            IReadOnlyList<ServiceGraph>? parents,
            List<ServiceNode> services)
        {
            var declaredPhases = options.Phases ?? Array.Empty<string>();
            if (options.DefaultPhase != null && !Contains(declaredPhases, options.DefaultPhase))
            {
                throw new InitGraphException(name,
                    $"RuntimeFlowOptions.DefaultPhase is '{options.DefaultPhase}', but RuntimeFlowOptions.Phases is {Bracket(declaredPhases)}.");
            }
            if (double.IsNaN(options.TimeoutMultiplier) || double.IsInfinity(options.TimeoutMultiplier) || options.TimeoutMultiplier < 0)
            {
                throw new InitGraphException(name,
                    $"RuntimeFlowOptions.TimeoutMultiplier is {Fmt.N(options.TimeoutMultiplier)}; it must be a finite number >= 0 (0 disables all timeouts).");
            }

            ValidateOptions(options, name, declaredPhases);

            var registrations = LocalRegistrations(scope, name);

            // What can be validated without constructing a service is validated first — the lifetimes, the
            // [Init] attributes of concretely registered types, and parent services that injection would
            // recreate or re-inject — so these errors never leave constructed services behind. Cycles,
            // [DependsOn] targets and the attributes of factory products need the constructed graph and are
            // checked afterwards (releasing what was constructed, when the run owns the scope).
            foreach (var registration in registrations)
            {
                if (registration.Lifetime == Lifetime.Singleton || IsSceneComponent(registration)) continue;
                throw new InitGraphException(name,
                    $"{registration.ImplementationType.Name} is registered with Lifetime.{registration.Lifetime} in scope '{name}'; " +
                    "initializable services must be Lifetime.Singleton (a Scoped/Transient service would be re-created uninitialized in child scopes)." +
                    (typeof(UnityEngine.Component).IsAssignableFrom(registration.ImplementationType)
                        ? " For a MonoBehaviour use RegisterComponent(instance), RegisterComponentOnNewGameObject<T>(Lifetime.Singleton) " +
                          "or RegisterComponentInHierarchy<T>(), which is accepted as Scoped as long as no child scope resolves it."
                        : string.Empty));
            }

            foreach (var registration in registrations)
            {
                var type = registration.ImplementationType;
                foreach (var dependsOn in type.GetCustomAttributes<DependsOnAttribute>(true))
                    ValidateDependsOn(dependsOn, type.Name, name);
                if (!type.IsClass || type.IsAbstract) continue;
                var attribute = type.GetCustomAttribute<InitAttribute>(true);
                if (attribute != null) ValidateInitAttribute(attribute, type.Name, name, declaredPhases);
            }

            WarnAboutOpenGenerics(scope, name, options.Logger);

            var externals = Externals(scope, name, parents);
            RejectRecreatedParentServices(scope, name, registrations, externals, options.Logger);

            var index = 0;

            foreach (var registration in registrations)
            {
                object? instance = null;
                Exception? constructionError = null;
                try
                {
                    instance = scope.Resolve(registration);
                }
                catch (Exception exception)
                {
                    constructionError = ConstructionError(registration, name, exception);
                }

                var source = instance?.GetType() ?? registration.ImplementationType;
                var node = new ServiceNode(index++, NodeKind.Service, source.Name, source, name)
                {
                    Instance = instance,
                    Service = instance as IAsyncInitializable,
                    ConstructionError = constructionError,
                    Registration = registration
                };
                services.Add(node);
            }

            return Assemble(scope, name, options, declaredPhases, services, externals, index);
        }

        /// <summary>Recovers already-created graph cleanup evidence after failed construction, without resolving a graph.</summary>
        internal static void CaptureCreatedForRollback(IObjectResolver scope, string name, RuntimeFlowOptions options,
            List<ServiceNode> services)
        {
            var catalog = LocalRegistrations(scope, name);
            var snapshot = VContainerLifetimeTracker.Capture(scope, name);
            // Catalog membership, rather than the runtime interface alone, grants async ownership.
            // Retain catalog order and aliases; read only cache entries that already hold a value.
            foreach (var registration in catalog)
                foreach (var entry in snapshot.Created)
                {
                    if (!ReferenceEquals(entry.Registration, registration)) continue;
                    var alreadyCaptured = false;
                    foreach (var node in services)
                        if (ReferenceEquals(node.Registration, registration)) { alreadyCaptured = true; break; }
                    if (!alreadyCaptured)
                    {
                        var type = entry.Instance.GetType();
                        services.Add(new ServiceNode(services.Count, NodeKind.Service, type.Name, type, name)
                        { Instance = entry.Instance, Service = entry.Instance as IAsyncInitializable, Registration = registration });
                    }
                    break;
                }

            // Unknown or uncached targets cannot contribute a cleanup edge. Do not run graph
            // validation: the entry-point exception remains the primary failure.
            foreach (var node in services)
            {
                var registeredType = node.Registration!.ImplementationType;
                var attributes = new List<DependsOnAttribute>(node.Type.GetCustomAttributes<DependsOnAttribute>(true));
                if (registeredType != node.Type)
                    attributes.AddRange(registeredType.GetCustomAttributes<DependsOnAttribute>(true));
                foreach (var attribute in attributes)
                    foreach (var candidate in services)
                        if (!ReferenceEquals(node, candidate) && attribute.ServiceType != null
                            && attribute.ServiceType.IsAssignableFrom(candidate.Type)) AddEdge(node, candidate, "DependsOn");
            }

            var phases = new List<string>();
            if (options.Phases != null)
                foreach (var phase in options.Phases)
                    if (!string.IsNullOrWhiteSpace(phase) && !Contains(phases, phase)) phases.Add(phase);
            if (phases.Count == 0) return;
            var anyKnownPhase = false;
            foreach (var node in services)
            {
                var declared = InitAttributeOf(node)?.Phase;
                if (declared != null && Contains(phases, declared)) { anyKnownPhase = true; break; }
            }
            if (!anyKnownPhase) return;
            foreach (var node in services)
            {
                var declared = InitAttributeOf(node)?.Phase;
                // Invalid explicit/default phase values have no valid ordering evidence.
                var phase = declared ?? options.DefaultPhase ?? phases[phases.Count - 1];
                if (!Contains(phases, phase)) continue;
                node.Phase = phase;
                node.PhaseIndex = IndexOf(phases, phase);
            }
            var nodes = new List<ServiceNode>(services);
            var index = nodes.Count;
            AddPhaseBarriers(nodes, services, phases, name, ref index);
            // MixedTeardown later recovers reflected injection edges for every created local
            // lifetime vertex, including plain helpers, using the existing injection semantics.
        }

        private static ServiceGraph Assemble(
            IObjectResolver scope,
            string name,
            RuntimeFlowOptions options,
            IReadOnlyList<string> declaredPhases,
            List<ServiceNode> services,
            List<ServiceNode> externals,
            int index)
        {
            Disambiguate(services, externals);
            WarnAboutDuplicates(options.Logger, name, services);

            var all = new List<ServiceNode>(services.Count + externals.Count);
            all.AddRange(services);
            all.AddRange(externals);

            // Phases are declared globally but belong to the scopes that actually label a service: a scope
            // whose services carry no [Init(Phase = …)] runs without barriers instead of being swept into
            // the last phase. The "unmarked lands in the last phase" rule applies from the first label on.
            var phases = services.Any(node => InitAttributeOf(node)?.Phase != null) ? declaredPhases : Array.Empty<string>();

            var edges = new InjectionEdges(scope, all, options.Logger, name);
            var unresolved = new List<UnresolvedEdge>();
            foreach (var node in services)
            {
                AddInjectionEdges(node, node.Registration!.ImplementationType, edges, options.Logger, name,
                    unresolved: unresolved);
                if (edges.RespawnedSceneComponent is ServiceNode component)
                {
                    var origin = node.Deps.FirstOrDefault(edge => ReferenceEquals(edge.Target, component)).Origin
                                 ?? unresolved.First(edge => ReferenceEquals(edge.From, node) && ReferenceEquals(edge.To, component)).Origin;
                    throw ParentSceneDependencyError(name, node.Name, component, origin);
                }
            }

            foreach (var node in services)
                ReadAttributes(node, name, phases, options.DefaultPhase);

            foreach (var node in services)
                AddDependsOnEdges(node, scope, name, all, services, externals);

            var nodes = new List<ServiceNode>(services);
            if (phases.Count > 0) AddPhaseBarriers(nodes, services, phases, name, ref index);

            AddUnresolvedEdges(unresolved, options.Logger, name);
            FailOnFailedInferredExternals(services, name);
            WarnAboutInferredExternals(services, name, options.Logger);

            Link(nodes);
            DetectCycles(nodes, name);

            return new ServiceGraph(name, nodes, services, externals, phases);
        }

        /// <summary>Option checks that concern every graph, validated before anything is constructed.</summary>
        private static void ValidateOptions(RuntimeFlowOptions options, string name, IReadOnlyList<string> phases)
        {
            var grace = options.CancellationGrace;
            if (grace < TimeSpan.Zero && grace != Timeout.InfiniteTimeSpan)
            {
                throw new InitGraphException(name,
                    $"RuntimeFlowOptions.CancellationGrace is {Fmt.S1(grace)}; it must be >= 0 or Timeout.InfiniteTimeSpan (wait forever).");
            }

            for (var i = 0; i < phases.Count; i++)
            {
                if (string.IsNullOrEmpty(phases[i]))
                    throw new InitGraphException(name, $"RuntimeFlowOptions.Phases {Bracket(phases)} contains an empty phase name.");
                for (var j = 0; j < i; j++)
                {
                    if (string.Equals(phases[i], phases[j], StringComparison.Ordinal))
                        throw new InitGraphException(name, $"RuntimeFlowOptions.Phases {Bracket(phases)} lists phase '{phases[i]}' twice; phase names must be unique.");
                }
            }
        }

        /// <summary>
        /// A MonoBehaviour found in the scene hierarchy (<c>RegisterComponentInHierarchy</c>) is always
        /// registered as Scoped, yet every resolution returns the one component that lives in the scene —
        /// it is never re-created per scope, so it is as safe to initialize as a singleton.
        /// </summary>
        internal static bool IsSceneComponent(Registration registration)
            => registration.Lifetime == Lifetime.Scoped && registration.Provider?.GetType().Name == "FindComponentProvider";

        /// <summary>
        /// Refuses parent service dependencies whose resolution would recreate a singleton in this scope
        /// or re-inject a parent scene component. Neither can use the initialized parent node's identity;
        /// validate reflected dependencies before constructing services of this scope.
        /// </summary>
        private static void RejectRecreatedParentServices(
            IObjectResolver scope,
            string name,
            List<Registration> registrations,
            List<ServiceNode> externals,
            ILogger logger)
        {
            if (externals.Count == 0) return;

            var edges = new InjectionEdges(scope, externals, logger, name, validateParentSafety: true);
            foreach (var registration in registrations)
            {
                if (!InjectionEdges.IsReflected(registration)) continue;
                var custom = InjectionEdges.CustomParameters(registration);
                foreach (var point in ConstructorEdges.InjectionPoints(edges.ReflectedType(registration, scope)))
                {
                    if (InjectionEdges.IsComponent(registration) && point.IsConstructorParameter) continue;
                    if (InjectionEdges.Supplied(custom, point) || ConstructorEdges.IsLazy(point.Type)) continue;
                    var targets = edges.Targets(point, scope, registration);
                    var component = edges.RespawnedSceneComponent;
                    if (component == null) continue;

                    var via = string.Empty;
                    foreach (var target in targets)
                    {
                        if (ReferenceEquals(target.Target, component)) via = target.Via;
                    }
                    throw ParentSceneDependencyError(name, registration.ImplementationType.Name, component,
                        point.Origin + (via.Length == 0 ? string.Empty : " via " + via));
                }
            }
        }

        private static InitGraphException ParentSceneDependencyError(string name, string service, ServiceNode component, string origin)
            => new InitGraphException(name,
                $"{service} in scope '{name}' depends on {component.Name} ({origin}), which scope '{component.Scope}' registers " +
                "with RegisterComponentInHierarchy (Lifetime.Scoped). VContainer creates a Scoped registration anew in " +
                $"every scope that resolves it. Resolving it from '{name}' re-injects that scene component with child " +
                "dependencies and disposes it with the child; the graph cannot use its initialized parent identity. " +
                $"Register it in '{component.Scope}' as a single instance instead: RegisterComponent(instance), " +
                "for example RegisterComponent(Object.FindFirstObjectByType<T>(FindObjectsInactive.Include)) " +
                "or FindAnyObjectByType when any instance will do.");

        private static InitGraphException ConstructionError(Registration registration, string name, Exception exception)
        {
            var cause = exception;
            while (cause is TargetInvocationException && cause.InnerException != null) cause = cause.InnerException;

            var text = Sentence(cause.Message);
            if (cause is VContainerException)
            {
                return new InitGraphException(name,
                    $"Could not construct {registration.ImplementationType.Name} in scope '{name}': {text} " +
                    $"Register the missing type in '{name}' or a parent scope.", cause);
            }

            return new InitGraphException(name,
                $"Could not construct {registration.ImplementationType.Name} in scope '{name}': its construction threw " +
                $"{cause.GetType().Name}: {text} See InnerException.", cause);
        }

        private static string Sentence(string message)
        {
            var text = message.TrimEnd();
            return text.EndsWith(".", StringComparison.Ordinal) ? text : text + ".";
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
                            Registration = node.Registration,
                            Source = node,
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
                    // A factory registration only declares its contract type; the instance the parent
                    // already holds (or constructs now, as the child would on first use) names the service.
                    var type = registration.ImplementationType;
                    object? instance = null;
                    InitGraphException? failure = null;
                    try
                    {
                        instance = current.Resolve(registration);
                        type = instance?.GetType() ?? type;
                    }
                    catch (Exception exception)
                    {
                        // The parent cannot construct it either. Without a linked run nobody else reports
                        // it, and a dependent must never start against a service that does not exist.
                        failure = ConstructionError(registration, scopeName, exception);
                    }

                    // Without a linked run nothing is known about initialization: a constructed parent
                    // service counts as initialized (see WarnAboutInferredExternals), a failed one as failed.
                    externals.Add(new ServiceNode(index--, NodeKind.External, type.Name, type, scopeName)
                    {
                        Instance = instance,
                        Registration = registration,
                        State = failure == null ? ServiceState.Completed : ServiceState.Failed,
                        Error = failure
                    });
                }
                current = current.Parent;
                depth++;
            }
            return externals;
        }

        /// <summary>
        /// State an external node inherits from its parent-scope node, read again when the run starts. A
        /// service that degraded there stays degraded here, so this scope's services see it in
        /// <see cref="InitContext.DegradedServices"/> and in <c>Describe()</c>. A service that never
        /// initialized there — skipped by a halt, cancelled, failed, or not run yet — keeps that state: it
        /// is not satisfied, and a service of this scope depending on it fails instead of starting.
        /// </summary>
        internal static ServiceState ExternalState(ServiceNode node) => node.State;

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

        private static InitAttribute? InitAttributeOf(ServiceNode node)
            => node.Type.GetCustomAttribute<InitAttribute>(true)
               ?? node.Registration!.ImplementationType.GetCustomAttribute<InitAttribute>(true);

        private static void ReadAttributes(
            ServiceNode node,
            string scope,
            IReadOnlyList<string> phases,
            string? defaultPhase)
        {
            var attribute = InitAttributeOf(node);
            if (attribute != null)
            {
                node.Optional = attribute.Optional;
                node.UserGated = attribute.UserGated;
                node.TimeoutSeconds = attribute.TimeoutSeconds;
                node.Weight = attribute.Weight;
                ValidateInitAttribute(attribute, node.Name, scope, phases);
            }

            if (phases.Count == 0) return;

            var phase = attribute?.Phase ?? defaultPhase ?? phases[phases.Count - 1];
            node.Phase = phase;
            node.PhaseIndex = IndexOf(phases, phase);
        }

        /// <summary>Checks the values of one [Init] attribute; the phase must be one of <paramref name="phases"/>.</summary>
        private static void ValidateInitAttribute(InitAttribute attribute, string service, string scope, IReadOnlyList<string> phases)
        {
            if (double.IsNaN(attribute.Weight) || double.IsInfinity(attribute.Weight) || attribute.Weight < 0)
            {
                throw new InitGraphException(scope,
                    $"{service} declares Weight = {Fmt.N(attribute.Weight)}; weight must be a finite number >= 0 (0 removes the service from the progress bar).");
            }

            if (double.IsNaN(attribute.TimeoutSeconds) || double.IsInfinity(attribute.TimeoutSeconds) || attribute.TimeoutSeconds < 0)
            {
                throw new InitGraphException(scope,
                    $"{service} declares TimeoutSeconds = {Fmt.N(attribute.TimeoutSeconds)}; it must be a finite number >= 0 (0 means no timeout).");
            }

            if (attribute.UserGated && attribute.TimeoutSeconds > 0)
            {
                throw new InitGraphException(scope,
                    $"{service} is user-gated and declares TimeoutSeconds = {Fmt.N(attribute.TimeoutSeconds)}; " +
                    "user-gated services never time out. Remove one of them.");
            }

            if (attribute.Phase == null) return;
            if (phases.Count == 0)
            {
                throw new InitGraphException(scope,
                    $"{service} declares phase '{attribute.Phase}', but RuntimeFlowOptions.Phases is empty. " +
                    "Declare the ordered phase list in RuntimeFlowOptions.Phases.");
            }
            if (!Contains(phases, attribute.Phase))
            {
                throw new InitGraphException(scope,
                    $"{service} declares phase '{attribute.Phase}', but RuntimeFlowOptions.Phases is {Bracket(phases)}.");
            }
        }

        /// <summary>
        /// Edges of one service from everything VContainer injects into it — constructor parameters and
        /// [Inject] methods, fields and properties — resolved the way VContainer resolves them.
        /// </summary>
        /// <param name="unresolved">When given, the constructor edges of an instance or factory product — parameters
        /// VContainer never resolved — are collected here instead of being added; see <see cref="AddUnresolvedEdges"/>.</param>
        internal static void AddInjectionEdges(ServiceNode node, Type registeredType, InjectionEdges edges, ILogger logger, string scope,
            bool reportAmbiguity = true, List<UnresolvedEdge>? unresolved = null)
        {
            var seen = new HashSet<(MemberInfo Member, int Position)>();
            var custom = InjectionEdges.CustomParameters(node.Registration);

            // VContainer injects [Inject] members only into what it constructs or injects by reflection. An
            // instance (RegisterInstance) or a factory product (Register(resolver => …)) never gets them, so
            // only its constructor parameters count — the documented choice that keeps a decorator's edges.
            var reflected = node.Registration == null || InjectionEdges.IsReflected(node.Registration);
            var component = node.Registration != null && InjectionEdges.IsComponent(node.Registration);
            foreach (var type in InspectedTypes(node, registeredType, reflected))
            {
                var ambiguity = component ? null : ConstructorEdges.Ambiguity(type);
                if (reportAmbiguity && ambiguity != null) logger.Warn($"[RuntimeFlow] {scope}: {ambiguity}");

                foreach (var point in ConstructorEdges.InjectionPoints(type))
                {
                    if (!reflected && !point.IsConstructorParameter) continue;
                    if (component && point.IsConstructorParameter) continue;
                    if (!seen.Add(point.Key)) continue;
                    if (InjectionEdges.Supplied(custom, point)) continue;

                    if (ConstructorEdges.IsLazy(point.Type))
                    {
                        node.LazyParameters.Add($"{Fmt.Type(point.Type)} {point.Name}");
                        continue;
                    }

                    foreach (var (target, via) in edges.Targets(point, edges.Scope, node.Registration))
                    {
                        if (ReferenceEquals(target, node)) continue;
                        var origin = via.Length == 0 ? point.Origin : point.Origin + " via " + via;
                        if (!reflected && unresolved != null) unresolved.Add(new UnresolvedEdge(node, target, origin));
                        else AddEdge(node, target, origin);
                    }
                }
            }
        }

        /// <summary>A constructor edge of an instance or factory product, inferred from a parameter VContainer never resolved.</summary>
        internal readonly struct UnresolvedEdge
        {
            public UnresolvedEdge(ServiceNode from, ServiceNode to, string origin)
            {
                From = from;
                To = to;
                Origin = origin;
            }

            public ServiceNode From { get; }
            public ServiceNode To { get; }
            public string Origin { get; }
        }

        /// <summary>
        /// Adds the constructor edges of instances and factory products once every other edge is known.
        /// They usually describe what the object holds (a decorator and what it wraps) and are kept, in
        /// their original position at the front of the edge list. VContainer never resolved those
        /// parameters, though, so one that would close a cycle of services is evidence of nothing: it is
        /// dropped with a warning instead of failing the graph with a cycle that does not exist. Only service
        /// edges count for that check: an edge that closes a cycle only through a phase barrier is a real
        /// phase-order violation (an early service holding a later-phase one) and is kept, so the cycle
        /// detection reports it as one.
        /// </summary>
        private static void AddUnresolvedEdges(List<UnresolvedEdge> unresolved, ILogger logger, string scope)
        {
            var inserted = new Dictionary<ServiceNode, int>();
            foreach (var edge in unresolved)
            {
                if (edge.From.DepTargets.Contains(edge.To)) continue;
                if (ReachesThroughServices(edge.To, edge.From))
                {
                    logger.Warn($"[RuntimeFlow] {scope}: ignoring the edge {edge.From.Name} -> {edge.To.DisplayName} ({edge.Origin}): " +
                                $"{edge.From.Name} is a registered instance or factory product, so VContainer never resolved that " +
                                "constructor parameter, and the edge would close a cycle. Add [DependsOn] only if the order is real.");
                    continue;
                }
                // Constructor edges were always first; DependsOn and phase edges were appended after them.
                inserted.TryGetValue(edge.From, out var position);
                edge.From.DepTargets.Add(edge.To);
                edge.From.Deps.Insert(position, new Edge(edge.To, edge.Origin));
                inserted[edge.From] = position + 1;
            }
        }

        /// <summary>
        /// True when <paramref name="goal"/> is reachable from <paramref name="start"/> along the service edges
        /// added so far; phase barriers are not walked through.
        /// </summary>
        private static bool ReachesThroughServices(ServiceNode start, ServiceNode goal)
        {
            var pending = new Stack<ServiceNode>();
            var seen = new HashSet<ServiceNode>();
            pending.Push(start);
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                if (ReferenceEquals(node, goal)) return true;
                if (node.Kind != NodeKind.Service || !seen.Add(node)) continue;
                foreach (var edge in node.Deps) pending.Push(edge.Target);
            }
            return false;
        }

        /// <summary>
        /// A parent service inferred without a linked run that its container could not construct: a
        /// dependent fails (or, when optional, degrades) with that construction error instead of starting.
        /// </summary>
        private static void FailOnFailedInferredExternals(List<ServiceNode> services, string scope)
        {
            foreach (var node in services)
            {
                if (node.ConstructionError != null) continue;
                foreach (var edge in node.Deps)
                {
                    var target = edge.Target;
                    if (target.Kind != NodeKind.External || target.Source != null || target.State != ServiceState.Failed) continue;
                    node.ConstructionError = new InitGraphException(scope,
                        $"{node.Name} depends on {target.Name} ({edge.Origin}), which parent scope '{target.Scope}' could not " +
                        $"construct: {target.Error?.Message ?? "unknown error."} {node.Name} cannot start without it.",
                        target.Error);
                    break;
                }
            }
        }

        /// <summary>
        /// Parent services inferred from the parent containers (no linked runs) count as initialized, but
        /// nothing in this run ran their <see cref="IAsyncInitializable.InitializeAsync"/>. Warns once, naming
        /// those this scope actually depends on.
        /// </summary>
        private static void WarnAboutInferredExternals(List<ServiceNode> services, string scope, ILogger logger)
        {
            var names = new List<string>();
            var seen = new HashSet<ServiceNode>();
            foreach (var node in services)
                foreach (var edge in node.Deps)
                {
                    var target = edge.Target;
                    if (target.Kind != NodeKind.External || target.Source != null || target.State != ServiceState.Completed
                        || !(target.Instance is IAsyncInitializable) || !seen.Add(target)) continue;
                    names.Add(target.Name);
                }
            if (names.Count == 0) return;
            logger.Warn($"[RuntimeFlow] {scope}: depends on parent-scope services without a linked parent run " +
                        $"({Fmt.Capped(names, 5)}); they are treated as initialized, but this run cannot know whether their " +
                        "InitializeAsync ran. Pass the parent runs (ScopeRun.Create parents:) or use RuntimeFlowHost.InitializeScopeAsync.");
        }

        private static IEnumerable<Type> InspectedTypes(ServiceNode node, Type registeredType, bool reflected)
        {
            if (reflected)
            {
                // Hierarchy lookup binds its injector to the found runtime subtype. Every other
                // reflected provider binds it to ImplementationType. Analyze that one injector only:
                // separately analyzing its base type would revive hidden or overridden [Inject] members.
                yield return node.Registration?.Provider?.GetType().Name == "FindComponentProvider"
                    ? node.Type : registeredType;
                yield break;
            }
            yield return node.Type;
            if (registeredType != node.Type && registeredType.IsClass && !registeredType.IsAbstract)
                yield return registeredType;
        }

        private static void AddDependsOnEdges(
            ServiceNode node,
            IObjectResolver resolver,
            string scope,
            List<ServiceNode> all,
            List<ServiceNode> services,
            List<ServiceNode> externals)
        {
            var registeredType = node.Registration!.ImplementationType;
            var attributes = new List<DependsOnAttribute>(node.Type.GetCustomAttributes<DependsOnAttribute>(true));
            if (registeredType != node.Type)
                attributes.AddRange(registeredType.GetCustomAttributes<DependsOnAttribute>(true));

            foreach (var attribute in attributes)
            {
                // Factory products are only known once constructed; their attributes are checked here.
                ValidateDependsOn(attribute, node.Name, scope);
                var matched = false;
                foreach (var candidate in all)
                {
                    if (ReferenceEquals(candidate, node) || !attribute.ServiceType.IsAssignableFrom(candidate.Type)) continue;
                    AddEdge(node, candidate, "DependsOn");
                    matched = true;
                }

                if (matched) continue;

                // A service that failed to construct through a factory is only known by its contract
                // type, so the target may well be that very service. A required construction error fails
                // the run and remains the explanation; an optional one cannot excuse an unknown target.
                if (services.Any(s => s.ConstructionError != null && !s.Optional && (s.Type.IsInterface || s.Type.IsAbstract))) continue;

                // The target resolves or is registered in the scope chain, but is not part of any graph:
                // the registration simply forgot to expose IAsyncInitializable. That is a far more common
                // mistake than a missing service, and it deserves the fix rather than the generic text.
                if (TryFindUnexposedInitializable(resolver, scope, attribute.ServiceType, out var unexposed))
                {
                    throw new InitGraphException(scope,
                        $"{node.Name} declares [DependsOn(typeof({attribute.ServiceType.Name}))]; " +
                        $"{unexposed.Name} implements IAsyncInitializable but is not registered as one, " +
                        "so it is never initialized. Register it with RegisterInitializable<T>() or add " +
                        ".As<IAsyncInitializable>() to its registration.");
                }

                throw new InitGraphException(scope,
                    $"{node.Name} declares [DependsOn(typeof({attribute.ServiceType.Name}))], but no initializable service " +
                    $"assignable to {attribute.ServiceType.Name} is registered in scope '{scope}' or its parents. " +
                    "A target must implement IAsyncInitializable and live in the same scope or a parent scope; " +
                    "a parent scope can never depend on a child scope. " +
                    KnownServices(scope, services, externals));
            }
        }

        /// <summary>A [DependsOn] target must be a closed type; anything else is a graph error, not a crash.</summary>
        private static void ValidateDependsOn(DependsOnAttribute attribute, string service, string scope)
        {
            var target = attribute.ServiceType;
            if (target == null)
            {
                throw new InitGraphException(scope,
                    $"{service} declares [DependsOn(null)]; name the service type it waits for, for example [DependsOn(typeof(IAuth))].");
            }
            if (target.ContainsGenericParameters)
            {
                throw new InitGraphException(scope,
                    $"{service} declares [DependsOn(typeof({Fmt.Type(target)}))], an open generic type. VContainer only " +
                    "initializes closed types, so an open generic target can never match a service; name the closed " +
                    "type the service waits for, for example [DependsOn(typeof(IRepository<Player>))].");
            }
        }

        /// <summary>
        /// An open generic registration (<c>Register(typeof(Repo&lt;&gt;), …)</c>) can never be exposed as
        /// <see cref="IAsyncInitializable"/>: VContainer only exposes it under open generic interfaces, so
        /// its closed instances are never discovered or initialized. Warns once per such type; best effort,
        /// since reading the local registrations depends on VContainer's private layout.
        /// </summary>
        private static void WarnAboutOpenGenerics(IObjectResolver scope, string name, ILogger logger)
        {
            IReadOnlyList<Registration> local;
            try { local = VContainerLifetimeTracker.LocalRegistrations(scope, name); }
            catch (Exception) { return; }

            var warned = new HashSet<Type>();
            foreach (var registration in local)
            {
                var type = registration.ImplementationType;
                if (type == null || !type.IsGenericTypeDefinition
                    || Array.IndexOf(type.GetInterfaces(), typeof(IAsyncInitializable)) < 0 || !warned.Add(type)) continue;
                logger.Warn($"[RuntimeFlow] {name}: {Fmt.Type(type)} implements IAsyncInitializable but is registered as an open " +
                            "generic; VContainer cannot expose an open generic as IAsyncInitializable, so none of its closed " +
                            "instances is ever initialized. Register each closed type the game uses instead, for example " +
                            $"RegisterInitializable<{type.Name.Split('`')[0]}<Player>>().");
            }
        }

        private static bool TryFindUnexposedInitializable(
            IObjectResolver resolver,
            string scope,
            Type targetType,
            out Type implementationType)
        {
            if (resolver.TryGetRegistration(targetType, out var direct)
                && direct != null
                && !ExposesInitializable(direct)
                && typeof(IAsyncInitializable).IsAssignableFrom(direct.ImplementationType))
            {
                implementationType = direct.ImplementationType;
                return true;
            }

            var visited = new List<IObjectResolver>();
            IObjectResolver? current = resolver;
            while (current != null && !visited.Contains(current))
            {
                visited.Add(current);
                if (TryFindInScope(current, scope, targetType, out implementationType)) return true;
                current = (current as IScopedObjectResolver)?.Parent;
            }

            if (resolver is IScopedObjectResolver scoped && scoped.Root != null && !visited.Contains(scoped.Root))
            {
                if (TryFindInScope(scoped.Root, scope, targetType, out implementationType)) return true;
            }

            implementationType = typeof(void);
            return false;
        }

        private static bool TryFindInScope(
            IObjectResolver resolver,
            string scope,
            Type targetType,
            out Type implementationType)
        {
            try
            {
                foreach (var registration in VContainerLifetimeTracker.LocalRegistrations(resolver, scope))
                {
                    if (ExposesInitializable(registration)) continue;
                    var candidate = registration.ImplementationType;
                    if (candidate == null || !typeof(IAsyncInitializable).IsAssignableFrom(candidate)) continue;
                    if (!targetType.IsAssignableFrom(candidate)) continue;
                    implementationType = candidate;
                    return true;
                }
            }
            catch (Exception)
            {
                // Best-effort inspection over VContainer's local table; custom resolvers fall back to TryGetRegistration.
            }

            implementationType = typeof(void);
            return false;
        }

        private static bool ExposesInitializable(Registration registration)
        {
            var exposed = registration.InterfaceTypes;
            if (exposed == null) return registration.ImplementationType == typeof(IAsyncInitializable);
            for (var i = 0; i < exposed.Count; i++)
            {
                if (exposed[i] == typeof(IAsyncInitializable)) return true;
            }
            return false;
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
            if (!from.DepTargets.Add(to)) return;
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
            => IndexOf(values, value) >= 0;

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
