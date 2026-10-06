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
            ILogger logger, string name, bool includeUncreated)
        {
            if (!HasAsync(services)) return;
            var plan = Build(scope, services, logger, name, null, includeUncreated);
            foreach (var dependent in plan.Nodes)
            {
                var sync = dependent.Instance is IDisposable
                    ? plan.Tracker.Contains(dependent.Instance)
                    : includeUncreated && dependent.Instance == null && typeof(IDisposable).IsAssignableFrom(dependent.Type);
                if (!sync) continue;
                foreach (var dependency in plan.Nodes)
                {
                    if (!IsAsyncOwned(dependency.Instance, services)
                        || ReferenceEquals(dependent.Instance, dependency.Instance)) continue;
                    if (!Reaches(dependent, dependency, plan.Nodes)) continue;
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
                ValidateBorrowed(scope, services, logger, name, includeUncreated: false);
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
            var plan = Build(scope, services, logger, name, preferred, includeUncreated: false);
            foreach (var node in ConstructionRollback.TeardownOrder(plan.Nodes, plan.Preferred))
            {
                ownership.RefreshProtection();
                if (ownership.IsProtected(node.Instance)) continue;
                if (IsAsyncOwned(node.Instance, services) && node.Instance is IAsyncDisposable asyncDisposable)
                {
                    try { await asyncDisposable.DisposeAsync(); }
                    catch (Exception exception) { Log(logger, name, node.Name, exception); }
                }
                // The actual tracker is the ownership authority. Existing instances and transients
                // never gain synchronous ownership merely by appearing in a graph or cache.
                if (node.Instance != null) ownership.ReleaseSync(node.Instance, node.Name);
            }
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
            foreach (var instance in tracker.Tracked)
            {
                var found = false;
                foreach (var node in nodes) if (ReferenceEquals(node.Instance, instance)) { found = true; break; }
                if (!found) Add(instance.GetType(), instance, null, scope);
            }
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
        private static bool Reaches(ServiceNode from, ServiceNode target, IReadOnlyList<ServiceNode> nodes)
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
                foreach (var edge in dependency.Deps) pending.Push(edge.Target);
                // A copied graph edge may point at an original node, and physical aliases can
                // carry different registration edges. Follow the complete local alias group.
                foreach (var alias in nodes)
                {
                    var sameInstance = dependency.Instance != null && ReferenceEquals(alias.Instance, dependency.Instance);
                    var sameRegistration = dependency.Registration != null
                        && ReferenceEquals(alias.Registration, dependency.Registration);
                    if ((sameInstance || sameRegistration) && !seen.Contains(alias)) pending.Push(alias);
                }
            }
            return false;
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

        private static bool IsAsyncOwned(object? instance, IReadOnlyList<ServiceNode> services)
        {
            if (!(instance is IAsyncDisposable)) return false;
            foreach (var service in services) if (ReferenceEquals(service.Instance, instance)) return true;
            return false;
        }

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
