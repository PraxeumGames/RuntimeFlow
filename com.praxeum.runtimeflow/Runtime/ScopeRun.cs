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
        /// released (<see cref="IAsyncDisposable"/> ones through <c>DisposeAsync</c>) and the scope is disposed;
        /// a caller-owned scope and its services are left alone.</exception>
        public static ScopeRun Create(
            IObjectResolver scope,
            string name,
            RuntimeFlowOptions options,
            IReadOnlyList<ScopeRun>? parents = null,
            bool ownsScope = false)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (options == null) throw new ArgumentNullException(nameof(options));

            ServiceGraph[]? parentGraphs = null;
            if (parents != null && parents.Count > 0)
            {
                parentGraphs = new ServiceGraph[parents.Count];
                for (var i = 0; i < parents.Count; i++) parentGraphs[i] = parents[i]._graph;
            }

            ServiceGraph graph;
            try
            {
                graph = GraphBuilder.Build(scope, name, options, parentGraphs, ownsScope);
            }
            catch (Exception)
            {
                // The graph builder already released the services it constructed for an owned scope; that
                // scope is released too, since no run exists that could dispose it later.
                if (ownsScope) ScopeDisposal.Dispose(scope, options.Logger, name);
                throw;
            }
            return new ScopeRun(scope, graph, options, ownsScope);
        }

        /// <summary>Name of the scope, as used in messages and status snapshots.</summary>
        public string Name { get; }

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

        /// <summary>Invoked once after teardown finishes, before disposal awaiters are released.</summary>
        internal Action<ScopeRun>? Disposed { get; set; }

        /// <summary>Immutable snapshot of the run and its services.</summary>
        public RuntimeFlowStatus GetStatus() => _scheduler.GetStatus();

        /// <summary>Human-readable rendering of the graph: services, flags, edges and their origins.</summary>
        public string Describe() => GraphDescriber.Describe(_graph);

        /// <summary>
        /// Cancels the run, disposes every <see cref="IAsyncDisposable"/> service in reverse completion order,
        /// then disposes the resolver when this run owns it. Teardown logs failures instead of throwing, and
        /// every step runs even when an earlier one failed. Every constructed service is disposed, including
        /// ones that never started (mirroring how VContainer disposes every <see cref="IDisposable"/>
        /// registration): construction alone takes ownership. A service that timed out but is still inside
        /// its <c>InitializeAsync</c> is waited for (bounded by <see cref="RuntimeFlowOptions.CancellationGrace"/>)
        /// before it is disposed. Nothing new starts once this is called, and the tokens are cancelled only
        /// after a yield, so a service may dispose its own run from inside its <c>InitializeAsync</c>.
        /// Concurrent, repeated and re-entrant calls share one teardown and all complete when it does. With
        /// an infinite grace a service that ignores its token keeps the teardown pending forever.
        /// </summary>
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
            await _scheduler.CancelAsync();
            await _scheduler.WaitForAbandonedAsync();

            foreach (var node in TeardownOrder())
            {
                if (!DisposesServices) break;
                if (!(node.Instance is IAsyncDisposable disposable)) continue;
                try
                {
                    await disposable.DisposeAsync();
                }
                catch (Exception exception)
                {
                    _options.Logger.Error(
                        $"[RuntimeFlow] {Name}: disposing {node.Name} threw {exception.GetType().Name}; continuing teardown.",
                        exception);
                }
            }

            try { _scheduler.DisposeTokens(); }
            catch (Exception exception)
            {
                _options.Logger.Error($"[RuntimeFlow] {Name}: releasing the run's tokens threw {exception.GetType().Name}; continuing teardown.", exception);
            }
            if (!_ownsScope) return;

            // A throwing Dispose() must not turn teardown into a failure (or wedge a restart).
            ScopeDisposal.Dispose(_scope, _options.Logger, Name);
        }

        private List<ServiceNode> TeardownOrder()
        {
            var order = new List<ServiceNode>();
            var seen = new HashSet<ServiceNode>();
            var completed = _scheduler.CompletionOrder;
            for (var i = completed.Count - 1; i >= 0; i--)
            {
                if (seen.Add(completed[i])) order.Add(completed[i]);
            }
            for (var i = _graph.Services.Count - 1; i >= 0; i--)
            {
                if (seen.Add(_graph.Services[i])) order.Add(_graph.Services[i]);
            }
            return order;
        }
    }
}
