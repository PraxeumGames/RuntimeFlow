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
        private bool _disposed;

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
        /// <param name="parents">Runs of the ancestor scopes, whose services become pre-completed external nodes.</param>
        /// <param name="ownsScope">When true <see cref="DisposeAsync"/> also disposes the resolver.</param>
        /// <exception cref="InitGraphException">The graph is invalid: a cycle, an unknown target, a non-singleton service.</exception>
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

            var graph = GraphBuilder.Build(scope, name, options, parentGraphs);
            return new ScopeRun(scope, graph, options, ownsScope);
        }

        /// <summary>Name of the scope, as used in messages and status snapshots.</summary>
        public string Name { get; }

        /// <summary>State of the run.</summary>
        public RunState State => _disposed ? RunState.Disposed : _scheduler.State;

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
            if (_disposed) throw new ObjectDisposedException(nameof(ScopeRun));
            return _scheduler.RunAsync(isRestart, generation, cancellationToken);
        }

        /// <summary>Cancels the run and every service token, then waits for a bounded settle; never throws.</summary>
        public Task CancelAsync() => _scheduler.CancelAsync();

        /// <summary>Immutable snapshot of the run and its services.</summary>
        public RuntimeFlowStatus GetStatus() => _scheduler.GetStatus();

        /// <summary>Human-readable rendering of the graph: services, flags, edges and their origins.</summary>
        public string Describe() => GraphDescriber.Describe(_graph);

        /// <summary>
        /// Cancels the run, disposes every <see cref="IAsyncDisposable"/> service in reverse completion order,
        /// then disposes the resolver when this run owns it. Teardown logs failures instead of throwing.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            await _scheduler.CancelAsync();

            foreach (var node in TeardownOrder())
            {
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

            _scheduler.DisposeTokens();
            if (_ownsScope) _scope.Dispose();
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
