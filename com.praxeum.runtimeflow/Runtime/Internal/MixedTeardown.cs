using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VContainer;

namespace RuntimeFlow.Internal
{
    /// <summary>Joins the graph's async ownership with VContainer's local synchronous ownership.</summary>
    internal static class MixedTeardown
    {
        internal static void ValidateBorrowed(IObjectResolver scope, IReadOnlyList<ServiceNode> services,
            ILogger logger, string name, bool includeUncreated, bool tolerateAdapterFailure = false)
        {
            if (!HasAsync(services)) return;
            Plan plan;
            try { plan = Build(scope, services, logger, name, null, includeUncreated); }
            catch (Exception exception) when (tolerateAdapterFailure)
            {
                // Teardown must still release the graph's async services; the boundary was already
                // accepted when the run was created and before it started.
                AdapterFailed(logger, name, exception, "the borrowed ownership boundary cannot be re-checked; " +
                    "releasing the graph's asynchronous services in dependency order");
                return;
            }
            var asyncOwned = AsyncOwned(services);
            var reachability = new Reachability(plan.Nodes);
            foreach (var dependent in plan.Nodes)
            {
                var sync = dependent.Instance is IDisposable
                    ? plan.Tracker.Contains(dependent.Instance)
                    : includeUncreated && dependent.Instance == null && typeof(IDisposable).IsAssignableFrom(dependent.Type);
                if (!sync) continue;
                foreach (var dependency in plan.Nodes)
                {
                    if (!IsAsyncOwned(dependency.Instance, asyncOwned)
                        || ReferenceEquals(dependent.Instance, dependency.Instance)) continue;
                    if (!reachability.Reaches(dependent, dependency)) continue;
                    throw new InitGraphException(name,
                        $"Caller-owned scope '{name}' cannot release {dependency.Name} asynchronously while " +
                        $"synchronous dependent {dependent.Name} remains owned by its container. Create the run with " +
                        "ownsScope: true, or keep both services under the caller's teardown ownership. " +
                        "Do not create new synchronous dependents after a borrowed run is created.");
                }
            }
        }

        internal static async Task ReleaseAsync(IObjectResolver scope, IReadOnlyList<ServiceNode> services,
            ILogger logger, string name, bool ownsScope, IReadOnlyList<ServiceNode>? preferred = null,
            OwnedCleanup? ownership = null)
        {
            if (!ownsScope)
            {
                // This check precedes every user cleanup: late factory products must not invalidate
                // the boundary accepted before initialization and then lose their live dependencies.
                ValidateBorrowed(scope, services, logger, name, includeUncreated: false, tolerateAdapterFailure: true);
                foreach (var node in ConstructionRollback.TeardownOrder(services, preferred))
                    if (node.Instance is IAsyncDisposable disposable)
                        try { await disposable.DisposeAsync(); }
                        catch (Exception exception) { Log(logger, name, node.Name, exception); }
                return;
            }

            ownership ??= new OwnedCleanup(scope, logger, name);
            // Protection cannot depend on successful lifetime planning: a graph error may have
            // escaped before its parent edge was attached, and planning can itself fail.
            ownership.RefreshProtection();
            IReadOnlyList<ServiceNode> nodes;
            IReadOnlyList<ServiceNode>? order;
            try
            {
                var plan = Build(scope, services, logger, name, preferred, includeUncreated: false);
                nodes = plan.Nodes;
                order = plan.Preferred;
            }
            catch (Exception exception)
            {
                // The tracker is still readable (ownership was captured), so synchronous ownership keeps
                // its policy; only the plain helpers between graph services lose their ordering edges.
                AdapterFailed(logger, name, exception, "ordering the graph services by their own edges only");
                nodes = services;
                order = preferred;
            }
            var asyncOwned = AsyncOwned(services);
            foreach (var node in ConstructionRollback.TeardownOrder(nodes, order))
            {
                ownership.RefreshProtection();
                if (ownership.IsProtected(node.Instance)) continue;
                if (IsAsyncOwned(node.Instance, asyncOwned) && node.Instance is IAsyncDisposable asyncDisposable)
                {
                    try { await asyncDisposable.DisposeAsync(); }
                    catch (Exception exception) { Log(logger, name, node.Name, exception); }
                }
                // The actual tracker is the ownership authority. Existing instances and transients
                // never gain synchronous ownership merely by appearing in a graph or cache.
                if (node.Instance != null) ownership.ReleaseSync(node.Instance, node.Name);
            }
        }

        /// <summary>
        /// Takes owned cleanup of <paramref name="scope"/>, or returns null (logging why) when the
        /// VContainer lifetime adapter cannot read the scope; the caller then uses
        /// <see cref="ReleaseUntrackedAsync"/> and plain scope disposal instead of leaking it.
        /// </summary>
        internal static OwnedCleanup? TryOwn(IObjectResolver scope, ILogger logger, string name,
            IReadOnlyList<object>? protectedInstances = null, Func<bool>? refreshProtected = null)
        {
            try { return new OwnedCleanup(scope, logger, name, protectedInstances, refreshProtected); }
            catch (Exception exception)
            {
                AdapterFailed(logger, name, exception, "falling back to plain teardown: the graph's asynchronous " +
                    "services in dependency order, then the container's own disposal");
                return null;
            }
        }

        /// <summary>
        /// Fallback teardown without the lifetime adapter: disposes the graph's <see cref="IAsyncDisposable"/>
        /// services after their dependents, then, when <paramref name="scope"/> is given, disposes it so
        /// VContainer releases every <see cref="IDisposable"/> it tracks. Protected ancestor instances
        /// are skipped; when their evidence cannot be refreshed, nothing further is released. A service
        /// implementing both interfaces is therefore called twice, <c>DisposeAsync</c> then <c>Dispose</c>;
        /// without the adapter nothing can tell which instances the scope tracks.
        /// </summary>
        internal static async Task ReleaseUntrackedAsync(IObjectResolver? scope, IReadOnlyList<ServiceNode> services,
            ILogger logger, string name, IReadOnlyList<ServiceNode>? preferred = null,
            IReadOnlyList<object>? protectedInstances = null, Func<bool>? refreshProtected = null)
        {
            var safe = true;
            try
            {
                foreach (var node in ConstructionRollback.TeardownOrder(services, preferred))
                {
                    if (!(node.Instance is IAsyncDisposable disposable)) continue;
                    if (!Refresh()) { safe = false; break; }
                    if (Contains(protectedInstances, disposable)) continue;
                    try { await disposable.DisposeAsync(); }
                    catch (Exception exception) { Log(logger, name, node.Name, exception); }
                }
            }
            catch (Exception exception) { Log(logger, name, "constructed services", exception); }
            finally
            {
                if (scope != null)
                {
                    if (safe && Refresh()) ScopeDisposal.Dispose(scope, logger, name);
                    else Log(logger, name, "scope", new InitGraphException(name,
                        "Cannot safely release a failed child scope: ancestor instance ownership could not be inspected."));
                }
            }

            bool Refresh()
            {
                if (refreshProtected == null) return true;
                try { return refreshProtected(); }
                catch { return false; }
            }
        }

        private static bool Contains(IReadOnlyList<object>? instances, object instance)
        {
            if (instances == null) return false;
            foreach (var candidate in instances) if (ReferenceEquals(candidate, instance)) return true;
            return false;
        }

        /// <summary>Logs a lifetime adapter failure once per teardown, with the fallback taken.</summary>
        internal static void AdapterFailed(ILogger logger, string name, Exception exception, string fallback)
        {
            try { logger.Error($"[RuntimeFlow] {name}: the VContainer lifetime adapter failed " +
                $"({exception.GetType().Name}: {exception.Message}); {fallback}.", exception); }
            catch { }
        }

        private static Plan Build(IObjectResolver scope, IReadOnlyList<ServiceNode> services, ILogger logger,
            string name, IReadOnlyList<ServiceNode>? preferred, bool includeUncreated)
        {
            var tracker = VContainerLifetimeTracker.Capture(scope, name);
            var nodes = new List<ServiceNode>();
            var aliases = new Dictionary<ServiceNode, ServiceNode>();
            var registrations = new Dictionary<Registration, ServiceNode>();
            var requesters = new Dictionary<ServiceNode, IObjectResolver>();
            foreach (var service in services)
            {
                var node = Add(service.Type, service.Instance, service.Registration, scope);
                node.Name = service.Name;
                node.Deps.AddRange(service.Deps);
                foreach (var edge in node.Deps) node.DepTargets.Add(edge.Target);
                aliases.Add(service, node);
                if (node.Registration != null && !registrations.ContainsKey(node.Registration))
                    registrations.Add(node.Registration, node);
            }
            foreach (var entry in tracker.Created)
            {
                if (registrations.TryGetValue(entry.Registration, out var known))
                { requesters[known] = entry.Resolver; continue; }
                var node = Add(entry.Instance.GetType(), entry.Instance, entry.Registration, entry.Resolver);
                registrations.Add(entry.Registration, node);
            }
            var present = new HashSet<object>(ConstructionRollback.InstanceIdentity.Instance);
            foreach (var node in nodes) if (node.Instance != null) present.Add(node.Instance);
            foreach (var instance in tracker.Tracked)
                if (present.Add(instance)) Add(instance.GetType(), instance, null, scope);
            if (includeUncreated)
                foreach (var registration in VContainerLifetimeTracker.LocalRegistrations(scope, name))
                {
                    if (registrations.ContainsKey(registration) || registration.Lifetime == Lifetime.Transient
                        || !InjectionEdges.IsReflected(registration)
                        || !typeof(IDisposable).IsAssignableFrom(registration.ImplementationType)) continue;
                    registrations.Add(registration,
                        Add(registration.ImplementationType, null, registration, scope));
                }

            // The normal graph carries explicit and phase dependencies. Add the same reflected
            // injection semantics for plain tracked registrations without mutating that graph.
            var edgeReaders = new Dictionary<IObjectResolver, InjectionEdges>();
            foreach (var node in nodes)
            {
                var requester = requesters[node];
                if (!edgeReaders.TryGetValue(requester, out var edges))
                {
                    edges = new InjectionEdges(requester, nodes, logger, name);
                    edgeReaders.Add(requester, edges);
                }
                GraphBuilder.AddInjectionEdges(node, node.Registration?.ImplementationType ?? node.Type, edges, logger, name, reportAmbiguity: false);
            }
            var priority = new List<ServiceNode>();
            if (preferred != null)
                foreach (var service in preferred) if (aliases.TryGetValue(service, out var node)) priority.Add(node);
            return new Plan(tracker, nodes, priority);

            ServiceNode Add(Type type, object? instance, Registration? registration, IObjectResolver requester)
            {
                var node = new ServiceNode(nodes.Count, NodeKind.Service, type.Name, type, name)
                { Instance = instance, Registration = registration };
                nodes.Add(node);
                requesters.Add(node, requester);
                return node;
            }
        }

        // Validation must follow every local lifetime vertex, including non-disposable helpers.
        // The ordering planner deliberately keeps its direct component edges; only the borrowed
        // ownership boundary needs transitive reachability to an asynchronously released resource.
        // Each dependent's closure is walked once and cached, instead of once per candidate dependency.
        private sealed class Reachability
        {
            private readonly Dictionary<object, List<ServiceNode>> _byInstance =
                new Dictionary<object, List<ServiceNode>>(ConstructionRollback.InstanceIdentity.Instance);
            private readonly Dictionary<object, List<ServiceNode>> _byRegistration =
                new Dictionary<object, List<ServiceNode>>(ConstructionRollback.InstanceIdentity.Instance);
            private readonly Dictionary<ServiceNode, Closure> _closures = new Dictionary<ServiceNode, Closure>();

            public Reachability(IReadOnlyList<ServiceNode> nodes)
            {
                foreach (var node in nodes)
                {
                    if (node.Instance != null) Group(_byInstance, node.Instance, node);
                    if (node.Registration != null) Group(_byRegistration, node.Registration, node);
                }
            }

            public bool Reaches(ServiceNode from, ServiceNode target)
            {
                if (!_closures.TryGetValue(from, out var closure)) _closures.Add(from, closure = Walk(from));
                return closure.Nodes.Contains(target)
                       || (target.Instance != null && closure.Instances.Contains(target.Instance));
            }

            private Closure Walk(ServiceNode from)
            {
                var closure = new Closure();
                var pending = new Stack<ServiceNode>();
                foreach (var edge in from.Deps) pending.Push(edge.Target);
                while (pending.Count > 0)
                {
                    var dependency = pending.Pop();
                    if (dependency.Kind == NodeKind.External || !closure.Nodes.Add(dependency)) continue;
                    if (dependency.Instance != null) closure.Instances.Add(dependency.Instance);
                    foreach (var edge in dependency.Deps) pending.Push(edge.Target);
                    // A copied graph edge may point at an original node, and physical aliases can
                    // carry different registration edges. Follow the complete local alias group.
                    if (dependency.Instance != null && _byInstance.TryGetValue(dependency.Instance, out var aliases))
                        foreach (var alias in aliases) if (!closure.Nodes.Contains(alias)) pending.Push(alias);
                    if (dependency.Registration != null && _byRegistration.TryGetValue(dependency.Registration, out var shared))
                        foreach (var alias in shared) if (!closure.Nodes.Contains(alias)) pending.Push(alias);
                }
                return closure;
            }

            private static void Group(Dictionary<object, List<ServiceNode>> groups, object key, ServiceNode node)
            {
                if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = new List<ServiceNode>());
                group.Add(node);
            }

            private sealed class Closure
            {
                public readonly HashSet<ServiceNode> Nodes = new HashSet<ServiceNode>();
                public readonly HashSet<object> Instances = new HashSet<object>(ConstructionRollback.InstanceIdentity.Instance);
            }
        }

        /// <summary>One owned teardown's tracker access, protection boundary and physical sync-cleanup ledger.</summary>
        internal sealed class OwnedCleanup
        {
            private readonly IObjectResolver _scope;
            private readonly ILogger _logger;
            private readonly string _name;
            private readonly VContainerLifetimeTracker _tracker;
            private readonly IReadOnlyList<object>? _protected;
            private readonly Func<bool>? _refreshProtected;
            private readonly List<object> _syncReleased = new List<object>();
            private bool _safe = true;

            internal OwnedCleanup(IObjectResolver scope, ILogger logger, string name,
                IReadOnlyList<object>? protectedInstances = null, Func<bool>? refreshProtected = null)
            {
                _scope = scope;
                _logger = logger;
                _name = name;
                _protected = protectedInstances;
                _refreshProtected = refreshProtected;
                _tracker = VContainerLifetimeTracker.Capture(scope, name);
            }

            internal bool IsProtected(object? instance)
            {
                if (instance == null || _protected == null) return false;
                foreach (var ancestor in _protected) if (ReferenceEquals(instance, ancestor)) return true;
                return false;
            }

            internal void RefreshProtection()
            {
                try
                {
                    if (!_safe || (_refreshProtected != null && !_refreshProtected()))
                        throw new InitGraphException(_name,
                            "Cannot safely continue failed-scope cleanup: ancestor instance ownership could not be inspected.");
                    if (_protected != null)
                        foreach (var instance in _protected) _tracker.Detach(instance);
                }
                catch { _safe = false; throw; }
            }

            internal void ReleaseSync(object instance, string name, bool popped = false)
            {
                RefreshProtection();
                // A residual entry was already popped, but other registration aliases must also
                // be removed before user code. Membership alone never creates async ownership.
                var owned = _tracker.Detach(instance) || popped;
                if (!owned || IsProtected(instance)) return;
                foreach (var released in _syncReleased) if (ReferenceEquals(instance, released)) return;
                if (!(instance is IDisposable disposable)) return;
                _syncReleased.Add(instance); // Throwing callbacks and reentrant aliases still count once.
                try { disposable.Dispose(); }
                catch (Exception exception) { Log(_logger, _name, name, exception, synchronous: true); }
                finally { RefreshProtection(); }
            }

            internal void DrainScope()
            {
                // VContainer's raw drain calls arbitrary Dispose callbacks in one loop. Pop each
                // entry ourselves so a callback cannot sneak a newly tracked ancestor or an
                // already-released physical alias into the next unguarded callback.
                while (true)
                {
                    RefreshProtection();
                    if (!_tracker.TryPop(out var instance)) break;
                    ReleaseSync(instance!, instance!.GetType().Name, popped: true);
                }
                // No user disposable remains in either local tracker. The resolver now performs
                // its normal diagnostics/cache clearing without another service callback.
                ScopeDisposal.Dispose(_scope, _logger, _name);
            }
        }

        private static bool HasAsync(IReadOnlyList<ServiceNode> services)
        {
            foreach (var service in services) if (service.Instance is IAsyncDisposable) return true;
            return false;
        }

        private static HashSet<object> AsyncOwned(IReadOnlyList<ServiceNode> services)
        {
            var owned = new HashSet<object>(ConstructionRollback.InstanceIdentity.Instance);
            foreach (var service in services) if (service.Instance is IAsyncDisposable disposable) owned.Add(disposable);
            return owned;
        }

        private static bool IsAsyncOwned(object? instance, HashSet<object> asyncOwned)
            => instance is IAsyncDisposable && asyncOwned.Contains(instance);

        internal static void Log(ILogger logger, string scope, string service, Exception exception, bool synchronous = false)
        {
            var mode = synchronous ? " synchronously" : string.Empty;
            try { logger.Error($"[RuntimeFlow] {scope}: disposing {service}{mode} threw {exception.GetType().Name}; continuing teardown.", exception); }
            catch { }
        }

        private sealed class Plan
        {
            public Plan(VContainerLifetimeTracker tracker, List<ServiceNode> nodes, List<ServiceNode> preferred)
            { Tracker = tracker; Nodes = nodes; Preferred = preferred; }
            public VContainerLifetimeTracker Tracker { get; }
            public List<ServiceNode> Nodes { get; }
            public List<ServiceNode> Preferred { get; }
        }
    }
}
