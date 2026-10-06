using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RuntimeFlow.Internal;
using VContainer;

namespace RuntimeFlow
{
    /// <summary>
    /// The initialization graph of a single VContainer scope: build it once with <see cref="Create"/>,
    /// run it with <see cref="RunAsync"/>, poll it with <see cref="GetStatus"/>, tear it down with
    /// <see cref="DisposeAsync"/>. Usable standalone on any resolver, with or without a host.
    /// </summary>
    public sealed class ScopeRun : IAsyncDisposable
    {
        private readonly IObjectResolver _scope;
        private readonly ServiceGraph _graph;
        private readonly Scheduler _scheduler;
        private readonly RuntimeFlowOptions _options;
        private readonly bool _ownsScope;
        private Task? _disposal;

        private ScopeRun(IObjectResolver scope, ServiceGraph graph, RuntimeFlowOptions options, bool ownsScope)
        {
            _scope = scope;
            _graph = graph;
            _options = options;
            _ownsScope = ownsScope;
            _scheduler = new Scheduler(graph, options);
            Name = graph.Scope;
        }

        /// <summary>
        /// Builds and validates the graph of <paramref name="scope"/> synchronously; every service is
        /// constructed here, so a resolution problem surfaces before any initialization starts.
        /// </summary>
        /// <param name="scope">The resolver whose registrations are scanned; only local ones are scheduled.</param>
        /// <param name="name">Name used in messages and status, for example "global" or "session".</param>
        /// <param name="options">Shared options; <see cref="RuntimeFlowOptions.Logger"/> must not be null.</param>
        /// <param name="parents">Runs of the ancestor scopes, whose services become pre-completed external nodes.
        /// Pass the host's runs (as <see cref="RuntimeFlowHost.InitializeScopeAsync"/> does) to propagate
        /// parent degradation: a parent service that degraded stays degraded here. A standalone
        /// <c>Create</c> on a child resolver only sees the parent containers, so its externals always
        /// count as initialized.</param>
        /// <param name="ownsScope">When true <see cref="DisposeAsync"/> also disposes the resolver — and so does a
        /// failing <c>Create</c>, since no run is left to own it. When false the resolver and its services stay
        /// the caller's, also when <c>Create</c> fails.</param>
        /// <exception cref="InitGraphException">The graph is invalid: a cycle, an unknown target, a non-singleton
        /// service. When the run was going to own the scope, services constructed before the error was found are
        /// released sequentially (<see cref="IAsyncDisposable"/> ones through <c>DisposeAsync</c>) and then
        /// the scope is disposed. This synchronous API starts cleanup before throwing but may return before
        /// cleanup finishes; use <see cref="CreateAsync"/> to await the complete rollback.
        /// A caller-owned scope and its services are left alone.</exception>
        public static ScopeRun Create(
            IObjectResolver scope,
            string name,
            RuntimeFlowOptions options,
            IReadOnlyList<ScopeRun>? parents = null,
            bool ownsScope = false)
            => CreateCore(scope, name, options, parents, ownsScope, disposesServices: true, out _);

        /// <summary>
        /// Builds the graph synchronously on success. On owned construction failure, waits for every
        /// asynchronous service cleanup and resolver disposal before rethrowing the original error.
        /// Ownership transfers when this call starts; a failed owned resolver must never be reused.
        /// </summary>
        public static Task<ScopeRun> CreateAsync(
            IObjectResolver scope,
            string name,
            RuntimeFlowOptions options,
            IReadOnlyList<ScopeRun>? parents = null,
            bool ownsScope = false)
            => CreateAsync(scope, name, options, parents, ownsScope, disposesServices: true);

        internal static async Task<ScopeRun> CreateAsync(
            IObjectResolver scope, string name, RuntimeFlowOptions options,
            IReadOnlyList<ScopeRun>? parents, bool ownsScope, bool disposesServices)
        {
            Task? rollback = null;
            try
            {
                return CreateCore(scope, name, options, parents, ownsScope, disposesServices, out rollback);
            }
            catch
            {
                if (rollback != null) await rollback;
                throw;
            }
        }

        /// <summary>Releases only graph registrations already created by a failed container build or entry point.</summary>
        internal static async Task ReleaseCreatedGraphAsync(IObjectResolver scope, string name, RuntimeFlowOptions options,
            IReadOnlyList<ScopeRun>? parents = null)
        {
            var services = new List<ServiceNode>();
            var protectedInstances = new List<object>();
            // The host still owns the construction reservation while this helper is awaited.
            // All secondary inspection/cleanup errors are contained to preserve its original error.
            try
            {
                var complete = CaptureParentInstances(scope, parents, protectedInstances, options, name);
                CaptureCreatedGraphForRollback(scope, name, options, services);
                complete &= CaptureParentInstances(scope, parents, protectedInstances, options, name);
                await ConstructionRollback.ReleaseAsync(scope, services, options.Logger, name, protectedInstances, complete,
                    () => CaptureParentInstances(scope, parents, protectedInstances, options, name));
            }
            catch (Exception exception) { LogSecondary("releasing the failed container", exception); }

            void LogSecondary(string operation, Exception exception)
            {
                try { options.Logger.Error($"[RuntimeFlow] {name}: {operation} threw {exception.GetType().Name}; " +
                    "preserving the original construction failure.", exception); }
                catch { }
            }
        }

        private static void CaptureCreatedGraphForRollback(IObjectResolver scope, string name,
            RuntimeFlowOptions options, List<ServiceNode> services)
        {
            // Eager container callbacks can create graph services before graph preflight starts.
            // Merge only already-created local registrations, retaining partially captured edges.
            try { GraphBuilder.CaptureCreatedForRollback(scope, name, options, services); }
            catch (Exception exception)
            {
                try { options.Logger.Error($"[RuntimeFlow] {name}: capturing already-created graph services threw " +
                    $"{exception.GetType().Name}; preserving the original construction failure.", exception); }
                catch { }
            }
        }

        // An owned failed child must not release physical instances belonging to a live ancestor,
        // even when a rejected factory alias placed those instances in the child's own tracker.
        // Capture before construction and again after failure: inferred externals may be created
        // while GraphBuilder is discovering the graph. Never resolve or inspect a faulted Lazy.Value.
        private static bool CaptureParentInstances(IObjectResolver scope, IReadOnlyList<ScopeRun>? parents,
            List<object> instances, RuntimeFlowOptions options, string name)
        {
            var complete = true;
            var observed = new List<IObjectResolver>();
            try
            {
                if (parents != null)
                    foreach (var parent in parents)
                    {
                        foreach (var node in parent._graph.Services) Add(node.Instance);
                        foreach (var node in parent._graph.Externals) Add(node.Instance);
                        Observe(parent._scope);
                    }
                var scoped = scope as IScopedObjectResolver;
                IObjectResolver? ancestor = scoped?.Parent;
                var chain = new List<IObjectResolver>();
                while (ancestor != null)
                {
                    var repeated = false;
                    foreach (var earlier in chain) if (ReferenceEquals(earlier, ancestor)) { repeated = true; break; }
                    if (repeated) break;
                    chain.Add(ancestor);
                    Observe(ancestor);
                    ancestor = (ancestor as IScopedObjectResolver)?.Parent;
                }
                if (scoped != null && !ReferenceEquals(scoped.Root, scope)) Observe(scoped.Root);
            }
            catch (Exception exception) { Failed(exception); }
            return complete;

            void Add(object? instance)
            {
                if (instance == null) return;
                foreach (var known in instances) if (ReferenceEquals(known, instance)) return;
                instances.Add(instance);
            }
            bool Observed(IObjectResolver owner)
            {
                foreach (var known in observed) if (ReferenceEquals(known, owner)) return true;
                return false;
            }
            void Observe(IObjectResolver owner)
            {
                if (Observed(owner)) return;
                observed.Add(owner);
                try
                {
                    var snapshot = VContainerLifetimeTracker.Capture(owner, name);
                    foreach (var entry in snapshot.Created) Add(entry.Instance);
                    foreach (var instance in snapshot.Tracked) Add(instance);
                }
                catch (Exception exception) { Failed(exception); }
            }
            void Failed(Exception exception)
            {
                complete = false;
                try { options.Logger.Error($"[RuntimeFlow] {name}: cannot inspect ancestor disposal ownership; " +
                    "failed construction cleanup will preserve the original error and stop before unsafe service release.", exception); }
                catch { }
            }
        }

        private static void ValidateCreateArguments(IObjectResolver scope, string name, RuntimeFlowOptions options)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (options == null) throw new ArgumentNullException(nameof(options));
        }

        private static ScopeRun CreateCore(IObjectResolver scope, string name, RuntimeFlowOptions options,
            IReadOnlyList<ScopeRun>? parents, bool ownsScope, bool disposesServices, out Task? rollback)
        {
            rollback = null;
            ValidateCreateArguments(scope, name, options);
            var constructed = new List<ServiceNode>();
            var protectedInstances = new List<object>();
            var ancestorEvidenceComplete = !ownsScope || CaptureParentInstances(scope, parents, protectedInstances, options, name);
            try
            {
                ServiceGraph[]? parentGraphs = null;
                if (parents != null && parents.Count > 0)
                {
                    parentGraphs = new ServiceGraph[parents.Count];
                    for (var i = 0; i < parents.Count; i++) parentGraphs[i] = parents[i]._graph;
                }
                var graph = GraphBuilder.Build(scope, name, options, parentGraphs, constructed);
                if (!ownsScope && disposesServices)
                    MixedTeardown.ValidateBorrowed(scope, graph.Services, options.Logger, name, includeUncreated: true);
                return new ScopeRun(scope, graph, options, ownsScope) { DisposesServices = disposesServices };
            }
            catch
            {
                // Sync callers receive the original graph error immediately; async callers await
                // this same rollback task before rethrowing it.
                if (ownsScope)
                {
                    CaptureCreatedGraphForRollback(scope, name, options, constructed);
                    ancestorEvidenceComplete &= CaptureParentInstances(scope, parents, protectedInstances, options, name);
                    rollback = ConstructionRollback.ReleaseAsync(scope, constructed, options.Logger, name,
                        protectedInstances, ancestorEvidenceComplete,
                        () => CaptureParentInstances(scope, parents, protectedInstances, options, name));
                }
                throw;
            }
        }

        /// <summary>Name of the scope, as used in messages and status snapshots.</summary>
        public string Name { get; }

        /// <summary>The immutable graph, exposed internally for diagnostics that need node identity.</summary>
        internal ServiceGraph Graph => _graph;

        /// <summary>State of the run.</summary>
        public RunState State => _disposal != null ? RunState.Disposed : _scheduler.State;

        /// <summary>Number of restarts the owner has reported for this scope.</summary>
        public int RestartCount
        {
            get => _scheduler.RestartCount;
            internal set => _scheduler.RestartCount = value;
        }

        /// <summary>
        /// Initializes every service of the scope. Completes with <see cref="StartupOutcome.Completed"/> or
        /// <see cref="StartupOutcome.Halted"/>; throws <see cref="RuntimeFlowException"/> when a required
        /// service fails and <see cref="OperationCanceledException"/> when the caller cancels.
        /// </summary>
        /// <param name="isRestart">Surfaced to services as <see cref="InitContext.IsRestart"/>.</param>
        /// <param name="generation">Surfaced to services as <see cref="InitContext.Generation"/>.</param>
        /// <param name="cancellationToken">Cancels the run and every service token.</param>
        public Task<StartupResult> RunAsync(bool isRestart = false, int generation = 0, CancellationToken cancellationToken = default)
        {
            if (_disposal != null) throw new ObjectDisposedException(nameof(ScopeRun));
            if (!_ownsScope && DisposesServices)
                MixedTeardown.ValidateBorrowed(_scope, _graph.Services, _options.Logger, Name, includeUncreated: true);
            return _scheduler.RunAsync(isRestart, generation, cancellationToken);
        }

        /// <summary>
        /// Cancels the run and every service token, then waits for the run to settle; never throws. The wait
        /// covers the services still in flight and is bounded by <see cref="RuntimeFlowOptions.CancellationGrace"/>
        /// — unbounded when the grace is <see cref="Timeout.InfiniteTimeSpan"/>: then a service that ignores its
        /// token keeps this task (and every teardown awaiting it) pending forever.
        /// </summary>
        public Task CancelAsync() => _scheduler.CancelAsync();

        /// <summary>
        /// Stops starting services without cancelling anything; used by the host when it accepted a
        /// restart that will replace this run right after the caller's stack unwinds.
        /// </summary>
        internal void Freeze() => _scheduler.Freeze();

        /// <summary>True when <paramref name="token"/> is cancelled by this run's teardown (a service or run token).</summary>
        internal bool OwnsToken(CancellationToken token) => _disposal == null && _scheduler.OwnsToken(token);

        /// <summary>
        /// When false, <see cref="DisposeAsync"/> tears down only the run (tokens, contexts) and leaves the
        /// services alone: they belong to a container the caller owns (<see cref="RuntimeFlowHost.From"/>).
        /// </summary>
        internal bool DisposesServices { get; set; } = true;

        /// <summary>Invoked once when <see cref="DisposeAsync"/> starts, before any teardown await.</summary>
        internal Action<ScopeRun>? Disposing { get; set; }

        /// <summary>Joins owned descendants after the cancellation yield, before releasing this run's services.</summary>
        internal Func<ScopeRun, Task>? DisposeDescendants { get; set; }

        /// <summary>Invoked once after teardown finishes, before disposal awaiters are released.</summary>
        internal Action<ScopeRun>? Disposed { get; set; }

        /// <summary>Immutable snapshot of the run and its services.</summary>
        public RuntimeFlowStatus GetStatus() => _scheduler.GetStatus();

        /// <summary>Human-readable rendering of the graph: services, flags, edges and their origins.</summary>
        public string Describe() => GraphDescriber.Describe(_graph);

        /// <summary>
        /// Cancels the run, disposes each <see cref="IAsyncDisposable"/> instance after its dependents,
        /// using reverse completion order for independent services,
        /// then disposes the resolver when this run owns it. Teardown logs cleanup failures instead of throwing,
        /// and every step runs even when an earlier cleanup failed. Every constructed service is disposed, including
        /// ones that never started (mirroring how VContainer disposes every <see cref="IDisposable"/>
        /// registration): construction alone takes ownership. A service that timed out but is still inside
        /// its <c>InitializeAsync</c> is waited for (bounded by <see cref="RuntimeFlowOptions.CancellationGrace"/>)
        /// before it is disposed. Nothing new starts once this is called, and the tokens are cancelled only
        /// after a yield, so a service may dispose its own run from inside its <c>InitializeAsync</c>.
        /// Concurrent, repeated and re-entrant calls share one teardown and all complete when it does. With
        /// an infinite grace a service that ignores its token keeps the teardown pending forever.
        /// </summary>
        /// <exception cref="InitGraphException">A caller-owned scope acquired a synchronous dependent of a
        /// graph-owned asynchronous service after validation. Teardown stops before releasing any service,
        /// and the caller must release its synchronous dependents before those asynchronous services.</exception>
        public ValueTask DisposeAsync()
        {
            if (_disposal == null)
            {
                // Published before any user callback runs, so a re-entrant call joins this teardown.
                var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposal = done.Task;
                _ = RunDisposalAsync(done);
            }
            return new ValueTask(_disposal);
        }

        private async Task RunDisposalAsync(TaskCompletionSource<bool> done)
        {
            try
            {
                await DisposeCoreAsync();
            }
            catch (InitGraphException exception)
            {
                // A late borrowed ownership violation is actionable and must remain visible to
                // disposal awaiters; no affected service has been released at this point.
                done.TrySetException(exception);
            }
            catch (Exception exception)
            {
                _options.Logger.Error($"[RuntimeFlow] {Name}: teardown threw {exception.GetType().Name}; continuing.", exception);
            }
            finally
            {
                try { Disposed?.Invoke(this); }
                catch (Exception exception)
                {
                    _options.Logger.Error($"[RuntimeFlow] {Name}: a disposal completion callback threw {exception.GetType().Name}; continuing.", exception);
                }
                done.TrySetResult(true);
            }
        }

        private async Task DisposeCoreAsync()
        {
            // Nothing of this run starts any more; its tokens are cancelled after the caller's stack unwound.
            _scheduler.Freeze();

            try { Disposing?.Invoke(this); }
            catch (Exception exception)
            {
                _options.Logger.Error($"[RuntimeFlow] {Name}: a disposal callback threw {exception.GetType().Name}; continuing teardown.", exception);
            }

            await Task.Yield();
            try
            {
                if (DisposeDescendants != null) await DisposeDescendants(this);
            }
            catch (Exception exception)
            {
                _options.Logger.Error($"[RuntimeFlow] {Name}: disposing descendant runs threw {exception.GetType().Name}; continuing teardown.", exception);
            }
            await _scheduler.CancelAsync();
            await _scheduler.WaitForAbandonedAsync();

            MixedTeardown.OwnedCleanup? ownership = null;
            try
            {
                if (_ownsScope) ownership = new MixedTeardown.OwnedCleanup(_scope, _options.Logger, Name);
                if (DisposesServices)
                    await MixedTeardown.ReleaseAsync(_scope, _graph.Services, _options.Logger, Name,
                        _ownsScope, TeardownOrder(), ownership);
            }
            finally
            {
                try { _scheduler.DisposeTokens(); }
                catch (Exception exception)
                {
                    _options.Logger.Error($"[RuntimeFlow] {Name}: releasing the run's tokens threw {exception.GetType().Name}; continuing teardown.", exception);
                }
                // Keep the planned and residual calls in the same physical cleanup ledger.
                // Successful local registration ownership retains its established policy.
                if (ownership != null) ownership.DrainScope();
            }
        }

        private List<ServiceNode> TeardownOrder()
        {
            var preferred = new List<ServiceNode>();
            var completed = _scheduler.CompletionOrder;
            for (var i = completed.Count - 1; i >= 0; i--)
                preferred.Add(completed[i]);
            return ConstructionRollback.TeardownOrder(_graph.Services, preferred);
        }

    }
}
