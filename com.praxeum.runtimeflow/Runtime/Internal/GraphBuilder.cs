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
        /// <param name="ownsServices">
        /// True when the run will own the scope: a graph error found after construction then releases the
        /// services it constructed. A caller-owned container keeps its singletons untouched.
        /// </param>
        public static ServiceGraph Build(
            IObjectResolver scope,
            string name,
            RuntimeFlowOptions options,
            IReadOnlyList<ServiceGraph>? parents,
            bool ownsServices)
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
                if (!type.IsClass || type.IsAbstract) continue;
                var attribute = type.GetCustomAttribute<InitAttribute>(true);
                if (attribute != null) ValidateInitAttribute(attribute, type.Name, name, declaredPhases);
            }

            var externals = Externals(scope, name, parents);
            RejectRecreatedParentServices(scope, name, registrations, externals, options.Logger);

            var services = new List<ServiceNode>();
            var sources = new List<Type>();
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
                sources.Add(registration.ImplementationType);
            }

            try
            {
                return Assemble(scope, name, options, declaredPhases, services, externals, sources, index);
            }
            catch (Exception)
            {
                // No run will ever own these services: release what the run would have released — unless
                // they belong to a container the caller owns, whose singletons are not ours to dispose.
                if (ownsServices) ReleaseConstructed(services, options.Logger, name);
                throw;
            }
        }

        private static ServiceGraph Assemble(
            IObjectResolver scope,
            string name,
            RuntimeFlowOptions options,
            IReadOnlyList<string> declaredPhases,
            List<ServiceNode> services,
            List<ServiceNode> externals,
            List<Type> sources,
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
            var phases = AnyPhaseDeclared(services, sources) ? declaredPhases : Array.Empty<string>();

            for (var i = 0; i < services.Count; i++)
                ReadAttributes(services[i], sources[i], name, phases, options.DefaultPhase);

            var edges = new InjectionEdges(scope, all, options.Logger, name);
            for (var i = 0; i < services.Count; i++)
                AddInjectionEdges(services[i], sources[i], edges, options.Logger, name);

            for (var i = 0; i < services.Count; i++)
                AddDependsOnEdges(services[i], sources[i], scope, name, all, services, externals);

            var nodes = new List<ServiceNode>(services);
            if (phases.Count > 0) AddPhaseBarriers(nodes, services, phases, name, ref index);

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

            var edges = new InjectionEdges(scope, externals, logger, name);
            foreach (var registration in registrations)
            {
                if (!InjectionEdges.IsReflected(registration)) continue;
                var custom = InjectionEdges.CustomParameters(registration);
                foreach (var point in ConstructorEdges.InjectionPoints(registration.ImplementationType))
                {
                    if (InjectionEdges.Supplied(custom, point) || ConstructorEdges.IsLazy(point.Type)) continue;
                    var targets = edges.Targets(point, scope, registration);
                    var component = edges.RespawnedSceneComponent;
                    if (component == null) continue;

                    var via = string.Empty;
                    foreach (var target in targets)
                    {
                        if (ReferenceEquals(target.Target, component)) via = target.Via;
                    }
                    throw new InitGraphException(name,
                        $"{registration.ImplementationType.Name} in scope '{name}' depends on {component.Name} " +
                        $"({point.Origin}{(via.Length == 0 ? string.Empty : " via " + via)}), which scope '{component.Scope}' registers " +
                        "with RegisterComponentInHierarchy (Lifetime.Scoped). VContainer creates a Scoped registration anew in " +
                        $"every scope that resolves it, so '{name}' would inject that scene component again with its own " +
                        $"dependencies and dispose it with itself. Register it in '{component.Scope}' as a single instance " +
                        "instead: RegisterComponent(instance), for example RegisterComponent(Object.FindObjectOfType<T>(true)).");
                }
            }
        }

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

        /// <summary>
        /// Releases the services of a graph that failed validation: <see cref="IAsyncDisposable"/> ones are
        /// disposed here (VContainer only knows <see cref="IDisposable"/>), in reverse construction order.
        /// </summary>
        private static void ReleaseConstructed(List<ServiceNode> services, ILogger logger, string name)
        {
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (!(services[i].Instance is IAsyncDisposable disposable)) continue;
                var service = services[i].Name;
                try
                {
                    var pending = disposable.DisposeAsync();
                    if (!pending.IsCompleted) _ = ObserveDisposal(pending.AsTask(), logger, name, service);
                    else if (pending.IsFaulted) _ = ObserveDisposal(pending.AsTask(), logger, name, service);
                }
                catch (Exception exception)
                {
                    logger.Error($"[RuntimeFlow] {name}: disposing {service} threw {exception.GetType().Name}; continuing teardown.", exception);
                }
            }
        }

        private static async Task ObserveDisposal(Task disposal, ILogger logger, string name, string service)
        {
            try { await disposal; }
            catch (Exception exception)
            {
                logger.Error($"[RuntimeFlow] {name}: disposing {service} threw {exception.GetType().Name}; continuing teardown.", exception);
            }
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
                    try
                    {
                        instance = current.Resolve(registration);
                        type = instance?.GetType() ?? type;
                    }
                    catch (Exception)
                    {
                        // The parent cannot construct it either; its own run reports that.
                    }

                    externals.Add(new ServiceNode(index--, NodeKind.External, type.Name, type, scopeName)
                    {
                        Instance = instance,
                        Registration = registration,
                        State = ServiceState.Completed
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
        private static void AddInjectionEdges(ServiceNode node, Type registeredType, InjectionEdges edges, ILogger logger, string scope)
        {
            var seen = new HashSet<(MemberInfo Member, int Position)>();
            var custom = InjectionEdges.CustomParameters(node.Registration);

            // VContainer injects [Inject] members only into what it constructs or injects by reflection. An
            // instance (RegisterInstance) or a factory product (Register(resolver => …)) never gets them, so
            // only its constructor parameters count — the documented choice that keeps a decorator's edges.
            var reflected = node.Registration == null || InjectionEdges.IsReflected(node.Registration);
            foreach (var type in InspectedTypes(node, registeredType))
            {
                var ambiguity = ConstructorEdges.Ambiguity(type);
                if (ambiguity != null) logger.Warn($"[RuntimeFlow] {scope}: {ambiguity}");

                foreach (var point in ConstructorEdges.InjectionPoints(type))
                {
                    if (!reflected && !point.IsConstructorParameter) continue;
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
                        AddEdge(node, target, via.Length == 0 ? point.Origin : point.Origin + " via " + via);
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
            IObjectResolver resolver,
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

                if (matched) continue;

                // A service that failed to construct through a factory is only known by its contract
                // type, so the target may well be that very service: its construction error, which fails
                // the run, is the real explanation — a derived graph error would only hide it.
                if (services.Any(s => s.ConstructionError != null && (s.Type.IsInterface || s.Type.IsAbstract))) continue;

                // The target resolves but is not part of any graph: the registration simply forgot to
                // expose IAsyncInitializable. That is a far more common mistake than a missing service,
                // and it deserves the fix rather than the generic "nothing is registered" text.
                if (resolver.TryGetRegistration(attribute.ServiceType, out var registration)
                    && registration != null
                    && typeof(IAsyncInitializable).IsAssignableFrom(registration.ImplementationType))
                {
                    throw new InitGraphException(scope,
                        $"{node.Name} declares [DependsOn(typeof({attribute.ServiceType.Name}))]; " +
                        $"{registration.ImplementationType.Name} implements IAsyncInitializable but is not registered as one, " +
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
