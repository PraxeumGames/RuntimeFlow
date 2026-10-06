using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RuntimeFlow.Internal
{
    /// <summary>
    /// Runs one scope's graph: it starts every node whose dependencies are satisfied, collects failures,
    /// enforces halt, timeout and cancellation, and produces the run's <see cref="StartupResult"/>.
    /// Single-threaded by construction: no locks, every await continues on the captured context, and the
    /// few entry points reachable from other threads (the caller token, <see cref="InitContext"/>) post
    /// their work to that context.
    /// </summary>
    internal sealed class Scheduler
    {
        private enum StopKind { None, Failure, Halt, Cancel }

        private readonly ServiceGraph _graph;
        private readonly RuntimeFlowOptions _options;
        private readonly ILogger _logger;
        private readonly string _scope;
        private ObserverList _observers;

        private readonly List<ServiceNode> _ready = new List<ServiceNode>();
        private readonly List<ServiceNode> _inFlight = new List<ServiceNode>();
        private readonly List<ServiceNode> _completionOrder = new List<ServiceNode>();
        private readonly List<string> _degraded = new List<string>();

        // Copy-on-write view handed to services: replaced (never mutated) on every degradation, so a
        // service can enumerate it across an await or from another thread.
        private string[] _degradedSnapshot = Array.Empty<string>();
        private readonly List<(ServiceNode Node, Exception Error, TimeSpan Elapsed)> _failures =
            new List<(ServiceNode, Exception, TimeSpan)>();
        private readonly Stopwatch _clock = new Stopwatch();

        private CancellationTokenSource? _runCts;
        private CancellationTokenRegistration _callerRegistration;
        private CancellationToken _callerToken;
        private SynchronizationContext? _context;
        private TaskCompletionSource<StartupResult>? _done;
        private StallWatch? _watch;
        private StopKind _stop = StopKind.None;
        private bool _pumping;
        private bool _finished;
        private bool _frozen;
        private bool _isRestart;
        private int _generation;
        private int _externalDegraded;
        private string? _haltReason;
        private string? _haltedBy;
        private string? _currentPhase;
        private TimeSpan _lastProgress;

        public Scheduler(ServiceGraph graph, RuntimeFlowOptions options)
        {
            _graph = graph;
            _options = options;
            _scope = graph.Scope;
            _logger = options.Logger;
            _observers = new ObserverList(Array.Empty<IRuntimeFlowObserver>(), _logger, _scope);
        }

        /// <summary>State of the run.</summary>
        public RunState State { get; private set; } = RunState.NotStarted;

        /// <summary>Number of restarts reported by the owner; surfaced through <see cref="GetStatus"/>.</summary>
        public int RestartCount { get; set; }

        /// <summary>Nodes in the order they finished, used to tear services down in reverse.</summary>
        public IReadOnlyList<ServiceNode> CompletionOrder => _completionOrder;

        /// <summary>Failure of the run, or null.</summary>
        public Exception? Error { get; private set; }

        /// <summary>Runs the graph to completion, a halt, a failure or cancellation.</summary>
        /// <exception cref="InvalidOperationException">The run was started already, or there is no synchronization context.</exception>
        public Task<StartupResult> RunAsync(bool isRestart, int generation, CancellationToken cancellationToken)
        {
            if (State != RunState.NotStarted)
                throw new InvalidOperationException($"The '{_scope}' run has already been started; create a new ScopeRun for another run.");

            // Without a context every continuation (Task.Yield, Task.Delay, the watch timer) resumes on the
            // thread pool and the lock-free bookkeeping below would be mutated from several threads at once.
            _context = SynchronizationContext.Current ?? throw new InvalidOperationException(
                $"The '{_scope}' run must be started on a thread with a SynchronizationContext (the Unity main thread); " +
                "SynchronizationContext.Current is null, so its continuations would run concurrently on the thread pool.");

            _isRestart = isRestart;
            _generation = generation;
            _callerToken = cancellationToken;
            // Snapshotted when the run starts (not when it was created): an observer added between
            // ScopeRun.Create and RunAsync receives the whole run, one added later nothing of it.
            _observers = new ObserverList(_options.Observers, _logger, _scope);
            State = RunState.Running;
            _done = new TaskCompletionSource<StartupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _runCts = new CancellationTokenSource();
            _clock.Restart();
            _lastProgress = TimeSpan.Zero;

            // A parent run may have finished after this graph was built: read the parents' states now.
            foreach (var external in _graph.Externals)
            {
                if (external.Source != null) external.State = GraphBuilder.ExternalState(external.Source);
            }

            // Services of parent scopes that degraded stay degraded here: seeding them makes them visible
            // through InitContext.DegradedServices and annotates the failures they precede. They are not
            // part of this run's StartupResult.Degraded, which stays per scope.
            foreach (var external in _graph.Externals)
            {
                if (external.State == ServiceState.Degraded) _degraded.Add(external.Name);
            }
            _externalDegraded = _degraded.Count;
            _degradedSnapshot = _degraded.ToArray();

            // Externals never block (they are not scheduled), so a service whose parent-scope dependency
            // never initialized — skipped by a halt, cancelled, failed, or not run yet — must not start
            // as if it had: it fails like a construction failure, before anything starts.
            foreach (var node in _graph.Services)
            {
                if (node.ConstructionError != null) continue;
                foreach (var edge in node.Deps)
                {
                    var target = edge.Target;
                    if (target.Kind != NodeKind.External || target.IsSatisfied) continue;
                    node.ConstructionError = new InvalidOperationException(
                        $"{node.Name} depends on {target.Name} of scope '{target.Scope}', which is {target.State} there: " +
                        $"that run never initialized it, so {node.Name} cannot start. Run a child scope only once every " +
                        "parent run completed.");
                    break;
                }
            }

            _observers.RunStarted(isRestart);
            _logger.Info(StartMessage(isRestart));

            if (cancellationToken.IsCancellationRequested)
            {
                BeginStop(StopKind.Cancel);
                return _done.Task;
            }
            if (cancellationToken.CanBeCanceled)
            {
                // The caller's token can be cancelled from any thread (CancelAfter uses a timer thread),
                // but every scheduler mutation must happen on the run's own context.
                var context = _context;
                _callerRegistration = cancellationToken.Register(() => context.Post(_ => BeginStop(StopKind.Cancel), null));
            }

            // A required service that could not even be constructed dooms the run: report every such
            // failure at once, before a single service starts, instead of the first one after some of
            // the graph already ran. Optional construction failures degrade in graph order instead (see
            // Pump), once their own dependencies are done, so the transitive order of the graph holds.
            foreach (var node in _graph.Services)
            {
                if (node.ConstructionError == null || node.Optional) continue;
                node.State = ServiceState.Failed;
                node.Error = node.ConstructionError;
                _failures.Add((node, node.ConstructionError, TimeSpan.Zero));
            }
            if (_failures.Count > 0)
            {
                // Transition first, notify second: an observer reacting to the failure (by cancelling the
                // run, say) must find the run already failing.
                Transition(StopKind.Failure);
                foreach (var failure in _failures) _observers.ServiceFailed(Snapshot(failure.Node), failure.Error);
                CancelAndSettle();
                return _done.Task;
            }

            if (_graph.Phases.Count > 0)
            {
                _currentPhase = _graph.Phases[0];
                _observers.PhaseStarted(_currentPhase);
            }

            _watch = new StallWatch(StallWatch.IntervalFor(_options, _graph.Services), OnWatchTick, OnWatchTickFailed);
            _watch.Start(_runCts.Token);

            foreach (var node in _graph.Nodes)
            {
                if (node.State == ServiceState.Pending && node.PendingDeps == 0) Enqueue(node);
            }

            Pump();
            return _done.Task;
        }

        /// <summary>
        /// Cancels the run (if any) and waits for it to settle; never throws. A run that is already
        /// stopping (halt, failure) is waited for as well, so teardown never overtakes its settle.
        /// </summary>
        public async Task CancelAsync()
        {
            if (State != RunState.NotStarted && !_finished && _done != null)
            {
                BeginStop(StopKind.Cancel);
                try { await _done.Task; }
                catch (Exception) { /* teardown never throws */ }
                return;
            }

            try { _watch?.Stop(); }
            catch (Exception) { /* the watch only owns its own delay */ }
            Cancellation.Cancel(_runCts, _logger, _scope, "the run");
        }

        /// <summary>
        /// Stops starting services without cancelling anything: the owner is about to replace this run
        /// (a restart, a disposal) and cancels it right after, outside the caller's stack. Services already
        /// in flight keep running until then; nothing new starts in the meantime. A frozen run never
        /// finishes on its own — not even when every service is done — only a cancellation ends it, so a
        /// doomed generation never reports <see cref="RunState.Completed"/>.
        /// </summary>
        public void Freeze()
        {
            if (_finished || _stop != StopKind.None) return;
            _frozen = true;
            _ready.Clear();
        }

        /// <summary>
        /// True when <paramref name="token"/> is the run token or a service token of this run, i.e. a
        /// token that is cancelled whenever this run is torn down.
        /// </summary>
        public bool OwnsToken(CancellationToken token)
        {
            if (!token.CanBeCanceled) return false;
            if (_runCts != null && _runCts.Token == token) return true;
            foreach (var node in _graph.Services)
            {
                if (node.Cts != null && node.Cts.Token == token) return true;
            }
            return false;
        }

        /// <summary>
        /// Cancels every service token and releases the run's cancellation sources. Every step is
        /// independent: a throwing cancellation callback never keeps the rest from being released.
        /// </summary>
        public void DisposeTokens()
        {
            try { _watch?.Stop(); }
            catch (Exception) { /* the watch only owns its own delay */ }
            try { _callerRegistration.Dispose(); }
            catch (Exception) { /* nothing to release */ }
            Cancellation.Cancel(_runCts, _logger, _scope, "the run");
            foreach (var node in _graph.Services)
            {
                node.Context?.Abandon();
                try { node.Cts?.Dispose(); }
                catch (Exception) { /* already released */ }
                node.Cts = null;
            }
            try { _runCts?.Dispose(); }
            catch (Exception) { /* already released */ }
            _runCts = null;
        }

        /// <summary>
        /// Waits — bounded by <see cref="RuntimeFlowOptions.CancellationGrace"/> — for services whose
        /// <see cref="IAsyncInitializable.InitializeAsync"/> is still running although the run already gave
        /// up on them (timed out while ignoring the token), so teardown does not dispose a service that is
        /// still unwinding. Services still running afterwards are reported and disposed anyway. Never throws.
        /// </summary>
        public async Task WaitForAbandonedAsync()
        {
            List<ServiceNode>? unwinding = null;
            foreach (var node in _graph.Services)
            {
                if (node.AbandonReported || node.Task == null || node.Task.IsCompleted) continue;
                (unwinding ??= new List<ServiceNode>()).Add(node);
            }
            if (unwinding == null) return;

            try
            {
                var observations = new List<Task>(unwinding.Count);
                foreach (var node in unwinding) observations.Add(node.Observation ?? node.Task!);
                var all = Task.WhenAll(observations);
                if (ReferenceEquals(await Task.WhenAny(all, Grace()), all))
                {
                    _ = all.Exception;
                    return;
                }

                var still = unwinding.Where(n => !n.Task!.IsCompleted).ToList();
                if (still.Count == 0) return;
                foreach (var node in still) node.AbandonReported = true;
                _logger.Error($"[RuntimeFlow] {_scope}: {still.Count.ToString(CultureInfo.InvariantCulture)} services still running " +
                              $"{Fmt.S1(_options.CancellationGrace)} after cancellation: " +
                              $"{string.Join(", ", still.Select(n => $"{n.Name} (timed out after {Fmt.S1(n.Clock.Elapsed)})"))}. " +
                              "Continuing teardown; they must observe their CancellationToken.");
            }
            catch (Exception exception)
            {
                _logger.Error($"[RuntimeFlow] {_scope}: waiting for abandoned services threw {exception.GetType().Name}; continuing teardown.", exception);
            }
        }

        /// <summary>Immutable snapshot of the run, safe to poll at any time.</summary>
        public RuntimeFlowStatus GetStatus()
        {
            var services = new List<ServiceStatus>(_graph.Services.Count);
            var running = new List<ServiceStatus>();
            var completed = 0;
            double weight = 0, done = 0;

            foreach (var node in _graph.Services)
            {
                var status = Snapshot(node);
                services.Add(status);
                if (node.State == ServiceState.Running) running.Add(status);
                if (node.IsSatisfied) completed++;

                weight += node.Weight;
                if (node.IsSatisfied) done += node.Weight;
                else if (node.State == ServiceState.Running) done += node.Weight * node.Progress;
            }

            var percent = weight > 0
                ? done / weight * 100.0
                : (services.Count == 0 || completed == services.Count ? 100.0 : 0.0);
            return new RuntimeFlowStatus(State, _scope, _currentPhase, services, running,
                completed, services.Count, percent, _clock.Elapsed, RestartCount, _haltReason, Error, _haltedBy);
        }

        /// <summary>
        /// Status of one node. A terminal node never changes again, so its snapshot is built once and
        /// shared; the dependency names are shared by every snapshot of the node. UIs poll every frame.
        /// </summary>
        private static ServiceStatus Snapshot(ServiceNode node)
        {
            if (node.CachedStatus != null) return node.CachedStatus;
            var status = new ServiceStatus(
                node.Name, node.Scope, node.Phase, node.State, node.Optional, node.UserGated,
                node.UserGated && node.State == ServiceState.Running, node.Clock.Elapsed, node.Progress, node.Weight,
                node.DependencyNames, node.UnmetDependencies(), node.Error);
            if (node.IsTerminal) node.CachedStatus = status;
            return status;
        }

        private void Pump()
        {
            if (_pumping) return;
            _pumping = true;
            try
            {
                while (_stop == StopKind.None && !_frozen && _ready.Count > 0)
                {
                    var node = _ready[0];
                    _ready.RemoveAt(0);
                    if (node.State != ServiceState.Pending) continue;
                    if (node.Kind == NodeKind.Barrier) CompleteBarrier(node);
                    else if (node.ConstructionError != null) FailNode(node, node.ConstructionError, TimeSpan.Zero);
                    else StartNode(node);
                }
            }
            finally
            {
                _pumping = false;
            }
            TryFinish();
        }

        private void StartNode(ServiceNode node)
        {
            node.State = ServiceState.Running;
            node.Clock.Restart();
            _inFlight.Add(node);
            node.Cts = CancellationTokenSource.CreateLinkedTokenSource(_runCts!.Token);
            node.Context = new InitContext(_scope, _isRestart, _generation, () => _degradedSnapshot,
                reason => OnContext(() => RequestHalt(node, reason)),
                fraction => OnContext(() => ReportProgress(node, fraction)));

            var status = Snapshot(node);
            _observers.ServiceStarted(status);
            if (node.UserGated) _observers.ServiceAwaitingPlayer(status);
            _logger.Debug($"[RuntimeFlow] {_scope}: {node.Name} started" +
                          (node.Phase != null ? $" (phase {node.Phase})" : string.Empty));

            Task task;
            try
            {
                task = node.Service?.InitializeAsync(node.Context, node.Cts.Token) ?? Task.CompletedTask;
            }
            catch (Exception exception)
            {
                task = Task.FromException(exception);
            }

            node.Task = task;
            node.Observation = ObserveAsync(node, task);
        }

        /// <summary>
        /// Runs <paramref name="action"/> on the run's context: inline when already there, posted otherwise.
        /// <see cref="InitContext"/> is handed to user code that may call it from any thread.
        /// </summary>
        private void OnContext(Action action)
        {
            var context = _context;
            if (context == null || SynchronizationContext.Current == context) action();
            else context.Post(_ => action(), null);
        }

        private async Task ObserveAsync(ServiceNode node, Task task)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException cancelled)
            {
                if (node.State != ServiceState.Running)
                {
                    ReportLateOutcome(node, cancelled);
                    return;
                }
                // A frozen run is about to be replaced: a service that bails out with a cancellation
                // right after requesting the replacement is cancelled, not failed.
                if (_stop != StopKind.None || _frozen || node.Cts == null || node.Cts.IsCancellationRequested) CancelNode(node);
                else FailNode(node, cancelled, node.Clock.Elapsed);
                return;
            }
            catch (Exception exception)
            {
                if (node.State != ServiceState.Running)
                {
                    ReportLateOutcome(node, exception);
                    return;
                }
                FailNode(node, exception, node.Clock.Elapsed);
                return;
            }

            if (node.State != ServiceState.Running)
            {
                ReportLateOutcome(node, null);
                return;
            }
            CompleteNode(node);
        }

        /// <summary>
        /// Reports the outcome of a node that finished after the run stopped waiting for it (it was
        /// abandoned, cancelled or timed out). Dropping it silently hides the very bug that made the run
        /// give up on the service in the first place.
        /// </summary>
        private void ReportLateOutcome(ServiceNode node, Exception? error)
        {
            if (error == null)
            {
                _logger.Debug($"[RuntimeFlow] {_scope}: {node.Name} completed after it was already {Fmt.State(node.State)}.");
                return;
            }
            if (error is OperationCanceledException)
            {
                _logger.Debug($"[RuntimeFlow] {_scope}: {node.Name} completed cancellation after it was abandoned.");
                return;
            }

            _logger.Warn($"[RuntimeFlow] {_scope}: {node.Name} threw {error.GetType().Name} after it was abandoned; " +
                         "it did not observe its CancellationToken.");
        }

        private void CompleteNode(ServiceNode node)
        {
            if (node.State != ServiceState.Running) return;
            node.State = ServiceState.Completed;
            node.Clock.Stop();
            node.Progress = 1f;
            _inFlight.Remove(node);
            _completionOrder.Add(node);
            Touch();
            _observers.ServiceCompleted(Snapshot(node));
            _logger.Debug($"[RuntimeFlow] {_scope}: {node.Name} completed in {Fmt.S2(node.Clock.Elapsed)}");
            Release(node);
            Pump();
        }

        private void CompleteBarrier(ServiceNode node)
        {
            node.State = ServiceState.Completed;
            if (node.Phase != null)
            {
                _observers.PhaseCompleted(node.Phase);
                var next = node.PhaseIndex + 1;
                if (next < _graph.Phases.Count)
                {
                    _currentPhase = _graph.Phases[next];
                    _observers.PhaseStarted(_currentPhase);
                }
            }
            Release(node);
            Pump();
        }

        private void CancelNode(ServiceNode node)
        {
            if (node.State != ServiceState.Running) return;
            node.State = ServiceState.Cancelled;
            node.Clock.Stop();
            _inFlight.Remove(node);
        }

        private void FailNode(ServiceNode node, Exception error, TimeSpan elapsed)
        {
            if (!MarkFailed(node, error, elapsed)) return;
            if (node.Optional)
            {
                _observers.ServiceFailed(Snapshot(node), error);
                Release(node);
                Pump();
                return;
            }

            // The run is failing before anybody hears about it: an observer that cancels the run from
            // OnServiceFailed must not turn this failure into a cancellation.
            var stopping = Transition(StopKind.Failure);
            _observers.ServiceFailed(Snapshot(node), error);
            if (stopping) CancelAndSettle();
        }

        /// <summary>
        /// Moves a running (or, for a construction failure, pending) node to Degraded or Failed and records
        /// it, without notifying observers, releasing dependents or stopping the run: callers that fail
        /// several nodes at once transition all of them first, so a cancellation fired by the first cannot
        /// misreport the rest, and they notify observers only once the run's own state reflects the failure.
        /// </summary>
        /// <returns>False when the node already reached a terminal state.</returns>
        private bool MarkFailed(ServiceNode node, Exception error, TimeSpan elapsed)
        {
            if (node.State != ServiceState.Running && node.State != ServiceState.Pending) return false;
            node.Clock.Stop();
            node.Error = error;
            _inFlight.Remove(node);

            if (node.Optional)
            {
                node.State = ServiceState.Degraded;
                _degraded.Add(node.Name);
                _degradedSnapshot = _degraded.ToArray();
                _completionOrder.Add(node);
                Touch();
                _logger.Warn(DegradedMessage(node, error, elapsed));
                return true;
            }

            node.State = ServiceState.Failed;
            _failures.Add((node, error, elapsed));
            return true;
        }

        private void Release(ServiceNode node)
        {
            foreach (var dependent in node.Dependents)
            {
                if (--dependent.PendingDeps == 0 && dependent.State == ServiceState.Pending)
                    Enqueue(dependent);
            }
        }

        /// <summary>
        /// Adds a node to the ready queue keeping it ordered by node index, so <see cref="Pump"/> can take
        /// the head without sorting the queue again on every iteration.
        /// </summary>
        private void Enqueue(ServiceNode node)
        {
            var at = _ready.Count;
            while (at > 0 && _ready[at - 1].Index > node.Index) at--;
            _ready.Insert(at, node);
        }

        private void RequestHalt(ServiceNode node, string reason)
        {
            if (_stop != StopKind.None || State != RunState.Running || _finished)
            {
                _logger.Warn($"[RuntimeFlow] {_scope}: halt '{reason}' from {node.Name} ignored; the run is no longer accepting one.");
                return;
            }

            _haltReason = reason;
            _haltedBy = node.Name;
            BeginStop(StopKind.Halt);
        }

        private void ReportProgress(ServiceNode node, float fraction)
        {
            // Progress posted from a worker thread can land after the service finished; a finished node
            // keeps its final progress.
            if (node.State != ServiceState.Running) return;
            node.Progress = fraction < 0f ? 0f : (fraction > 1f ? 1f : fraction);
            Touch();
        }

        private void Touch() => _lastProgress = _clock.Elapsed;

        private void BeginStop(StopKind kind)
        {
            if (Transition(kind)) CancelAndSettle();
        }

        /// <summary>
        /// Decides how the run ends, without cancelling anything yet. A cancellation posted from another
        /// thread can land after the run already finished; a finished run keeps its outcome and its
        /// service tokens stay valid.
        /// </summary>
        /// <returns>True when this call decided the outcome.</returns>
        private bool Transition(StopKind kind)
        {
            if (_stop != StopKind.None || _finished) return false;
            _stop = kind;
            State = kind == StopKind.Failure ? RunState.Failed
                : kind == StopKind.Halt ? RunState.Halted
                : RunState.Cancelled;
            _ready.Clear();
            return true;
        }

        /// <summary>
        /// Cancels the run token and settles the run. The settle starts whatever the cancellation callbacks
        /// (user code) do: without it the run would never finish.
        /// </summary>
        private void CancelAndSettle()
        {
            try
            {
                Cancellation.Cancel(_runCts, _logger, _scope, "the run");
            }
            finally
            {
                _ = SettleAsync();
            }
        }

        private void TryFinish()
        {
            if (_finished || _stop != StopKind.None || State != RunState.Running) return;
            // A frozen run is about to be replaced; only the cancellation that replaces it ends it.
            if (_frozen) return;
            if (_inFlight.Count > 0 || _ready.Count > 0) return;
            foreach (var node in _graph.Nodes)
            {
                if (!node.IsTerminal) return;
            }
            foreach (var node in _graph.Services)
            {
                // A service that bailed out cancelled never initialized: the run is not Completed.
                if (node.State != ServiceState.Cancelled) continue;
                BeginStop(StopKind.Cancel);
                return;
            }
            Finish();
        }

        private async Task SettleAsync()
        {
            try
            {
                await Task.Yield();

                // Wait for the bookkeeping (ObserveAsync) of the services still in flight, not just their
                // raw tasks: the failure list is written there. Services the run already gave up on (timed
                // out, degraded) are not waited for — they would stall every later stop for the whole grace.
                var pending = new List<Task>();
                foreach (var node in _inFlight)
                {
                    if (node.Observation != null) pending.Add(node.Observation);
                }

                if (pending.Count > 0)
                {
                    var all = Task.WhenAll(pending);
                    var winner = await Task.WhenAny(all, Grace());
                    if (ReferenceEquals(winner, all))
                    {
                        // Service outcomes are caught inside ObserveAsync, so a fault here is a framework
                        // bug: log it instead of letting it go unobserved; the run's outcome is decided.
                        try { await all; }
                        catch (Exception exception)
                        {
                            _logger.Error($"[RuntimeFlow] {_scope}: startup bookkeeping threw {exception.GetType().Name}; continuing teardown.", exception);
                        }
                    }
                    await Task.Yield();
                }
            }
            catch (Exception exception)
            {
                _logger.Error($"[RuntimeFlow] {_scope}: settling the run threw {exception.GetType().Name}; finishing anyway.", exception);
            }
            finally
            {
                Finish();
            }
        }

        /// <summary>
        /// The wait granted to cancelled services: <see cref="RuntimeFlowOptions.CancellationGrace"/>, where
        /// <see cref="Timeout.InfiniteTimeSpan"/> (or anything beyond what a timer can express) waits forever
        /// and zero or less does not wait.
        /// </summary>
        private Task Grace()
        {
            var grace = _options.CancellationGrace;
            if (grace == Timeout.InfiniteTimeSpan || grace.TotalMilliseconds > int.MaxValue)
                return new TaskCompletionSource<bool>().Task;
            return Task.Delay(grace > TimeSpan.Zero ? grace : TimeSpan.Zero);
        }

        private void Finish()
        {
            if (_finished) return;
            _finished = true;

            // Whatever happens below — a logger, an observer, a framework bug — the run's task completes:
            // an awaiter that never resumes is the one failure mode a startup framework cannot have.
            Action? complete = null;
            try
            {
                complete = Conclude();
            }
            catch (Exception exception)
            {
                _logger.Error($"[RuntimeFlow] {_scope}: finishing the run threw {exception.GetType().Name}; completing it anyway.", exception);
            }
            finally
            {
                if (complete != null) complete();
                else _done!.TrySetException(new InvalidOperationException($"The '{_scope}' run could not be finished; see the log."));
            }
        }

        /// <summary>Settles every node, reports the outcome and returns how to complete the run's task.</summary>
        private Action Conclude()
        {
            try { _watch?.Stop(); }
            catch (Exception) { /* the watch only owns its own delay */ }
            _clock.Stop();
            try { _callerRegistration.Dispose(); }
            catch (Exception) { /* nothing to release */ }

            var elapsed = _clock.Elapsed;
            var stillRunning = _inFlight.ToList();
            var unfinished = Unfinished();

            if (stillRunning.Count > 0)
            {
                var names = stillRunning.Select(n => $"{n.Name} ({Fmt.S1(n.Clock.Elapsed)})").ToList();
                _logger.Error($"[RuntimeFlow] {_scope}: {stillRunning.Count.ToString(CultureInfo.InvariantCulture)} services still running " +
                              $"{Fmt.S1(_options.CancellationGrace)} after cancellation: {string.Join(", ", names)}. " +
                              "Continuing teardown; they must observe their CancellationToken.");
            }

            foreach (var node in stillRunning)
            {
                node.State = ServiceState.Cancelled;
                node.Clock.Stop();
                node.AbandonReported = true;
            }
            _inFlight.Clear();

            foreach (var node in _graph.Nodes)
            {
                if (node.State == ServiceState.Pending) node.State = ServiceState.Skipped;
            }

            var degraded = _degraded.GetRange(_externalDegraded, _degraded.Count - _externalDegraded);
            switch (_stop)
            {
                case StopKind.None:
                {
                    State = RunState.Completed;
                    var result = new StartupResult(StartupOutcome.Completed, _scope, elapsed, degraded);
                    _logger.Info(CompletedMessage(elapsed, degraded));
                    _observers.RunCompleted(result);
                    return () => _done!.TrySetResult(result);
                }
                case StopKind.Halt:
                {
                    var result = new StartupResult(StartupOutcome.Halted, _scope, elapsed, degraded, _haltReason, _haltedBy);
                    _logger.Info(HaltMessage(elapsed));
                    WarnAboutFailuresWhileStopping("halting");
                    _observers.RunHalted(result);
                    return () => _done!.TrySetResult(result);
                }
                case StopKind.Failure:
                {
                    var completed = _completionOrder.Where(n => n.State == ServiceState.Completed)
                        .Select(n => n.Name).ToList();
                    var phase = _failures.Count > 0 ? _failures[0].Node.Phase : null;
                    var failures = new List<(string Service, Exception Error, TimeSpan Elapsed, IReadOnlyList<string> DegradedUpstreams)>(_failures.Count);
                    foreach (var failure in _failures)
                        failures.Add((failure.Node.Name, failure.Error, failure.Elapsed, DegradedUpstreams(failure.Node)));
                    var error = RuntimeFlowException.Create(_scope, phase, elapsed, completed, unfinished, failures);
                    Error = error;
                    _logger.Error($"[RuntimeFlow] {_scope}: {error.Message}");
                    _observers.RunFailed(error);
                    return () => _done!.TrySetException(error);
                }
                default:
                {
                    _logger.Info(CancelledMessage(elapsed));
                    WarnAboutFailuresWhileStopping("being cancelled");
                    _observers.RunCancelled();
                    var token = _callerToken.IsCancellationRequested ? _callerToken : CancellationToken.None;
                    return () => _done!.TrySetCanceled(token);
                }
            }
        }

        /// <summary>
        /// A halt or a cancellation decides the outcome, so a service that fails while the run settles does
        /// not change it — but dropping the failure silently would hide a bug in that service's cleanup.
        /// </summary>
        private void WarnAboutFailuresWhileStopping(string stopping)
        {
            foreach (var failure in _failures)
            {
                _logger.Warn($"[RuntimeFlow] {_scope}: {failure.Node.Name} threw {failure.Error.GetType().Name} " +
                             $"({failure.Error.Message}) while the run was {stopping}; the outcome stays unchanged.");
            }
        }

        /// <summary>
        /// Direct dependencies of <paramref name="node"/> that degraded, own or external, in edge order:
        /// the most likely explanation of why this service failed.
        /// </summary>
        private static List<string> DegradedUpstreams(ServiceNode node)
        {
            var result = new List<string>();
            foreach (var edge in node.Deps)
            {
                if (edge.Target.State == ServiceState.Degraded && !result.Contains(edge.Target.Name))
                    result.Add(edge.Target.Name);
            }
            return result;
        }

        private List<string> Unfinished()
        {
            var result = new List<string>();
            foreach (var node in _graph.Services)
            {
                switch (node.State)
                {
                    case ServiceState.Running:
                        result.Add($"{node.Name} (running {Fmt.S1(node.Clock.Elapsed)})");
                        break;
                    case ServiceState.Cancelled:
                        result.Add($"{node.Name} (cancelled after {Fmt.S1(node.Clock.Elapsed)})");
                        break;
                    case ServiceState.Pending:
                    {
                        var blockers = node.UnmetDependencies();
                        result.Add(blockers.Count == 0
                            ? $"{node.Name} (not started)"
                            : $"{node.Name} (blocked on {string.Join(", ", blockers)})");
                        break;
                    }
                }
            }
            return result;
        }

        private string StartMessage(bool isRestart)
        {
            var text = new StringBuilder();
            text.Append("[RuntimeFlow] ").Append(_scope).Append(": started — ")
                .Append(_graph.Services.Count.ToString(CultureInfo.InvariantCulture)).Append(" services");
            if (_graph.Phases.Count > 0)
                text.Append(", phases ").Append(string.Join(" > ", _graph.Phases));
            if (isRestart) text.Append(" (restart)");
            return text.ToString();
        }

        private string CompletedMessage(TimeSpan elapsed, IReadOnlyList<string> degraded)
        {
            var text = new StringBuilder();
            text.Append("[RuntimeFlow] ").Append(_scope).Append(": completed in ").Append(Fmt.S1(elapsed))
                .Append(" (").Append(_graph.Services.Count.ToString(CultureInfo.InvariantCulture)).Append(" services");
            if (degraded.Count > 0)
            {
                text.Append(", ").Append(degraded.Count.ToString(CultureInfo.InvariantCulture))
                    .Append(" degraded: ").Append(string.Join(", ", degraded));
            }
            return text.Append(')').ToString();
        }

        private string CancelledMessage(TimeSpan elapsed)
        {
            var completed = _graph.Services.Count(n => n.IsSatisfied);
            return $"[RuntimeFlow] {_scope}: cancelled after {Fmt.S1(elapsed)} " +
                   $"({completed.ToString(CultureInfo.InvariantCulture)}/{_graph.Services.Count.ToString(CultureInfo.InvariantCulture)} services completed).";
        }

        private string DegradedMessage(ServiceNode node, Exception error, TimeSpan elapsed)
        {
            var text = new StringBuilder();
            text.Append("[RuntimeFlow] ").Append(_scope).Append(": ").Append(node.Name)
                .Append(" is optional and failed after ").Append(Fmt.S1(elapsed))
                .Append(" (").Append(error.GetType().Name).Append(": ").Append(error.Message)
                .Append("); continuing degraded.");
            if (node.Dependents.Count > 0)
            {
                var names = node.Dependents.Where(d => d.Kind == NodeKind.Service).Select(d => d.Name).ToList();
                if (names.Count > 0) text.Append(" Dependents: ").Append(string.Join(", ", names)).Append('.');
            }
            return text.ToString();
        }

        private string HaltMessage(TimeSpan elapsed)
        {
            var completed = _graph.Services.Count(n => n.IsSatisfied);
            var cancelled = _graph.Services.Where(n => n.State == ServiceState.Cancelled).Select(n => n.Name).ToList();
            var text = new StringBuilder();
            text.Append("[RuntimeFlow] ").Append(_scope).Append(": halted by ").Append(_haltedBy)
                .Append(" — '").Append(_haltReason).Append("' after ").Append(Fmt.S1(elapsed))
                .Append(" (").Append(completed.ToString(CultureInfo.InvariantCulture)).Append('/')
                .Append(_graph.Services.Count.ToString(CultureInfo.InvariantCulture)).Append(" services completed");
            if (cancelled.Count > 0) text.Append("; cancelled: ").Append(string.Join(", ", cancelled));
            return text.Append(").").ToString();
        }

        private void OnWatchTickFailed(Exception exception)
        {
            try
            {
                _logger.Error($"[RuntimeFlow] {_scope}: the watch tick threw {exception.GetType().Name}; " +
                              "timeouts and stall warnings keep running.", exception);
            }
            catch (Exception)
            {
                // The logger itself is what failed; the watch keeps ticking regardless.
            }
        }

        private void OnWatchTick()
        {
            if (_stop != StopKind.None || State != RunState.Running || _finished) return;

            if (_options.TimeoutMultiplier > 0)
            {
                // Transition every expired service first, then release or stop once, and cancel the
                // tokens last: a cancellation continuation may run inline inside Cancel(), and it must
                // find its node already timed out — not complete it, and not have the first required
                // failure report the remaining ones as merely cancelled.
                List<ServiceNode>? expired = null;
                foreach (var node in _inFlight.OrderBy(n => n.Index).ToList())
                {
                    if (node.UserGated || node.TimeoutSeconds <= 0 || node.State != ServiceState.Running) continue;
                    var limit = node.TimeoutSeconds * _options.TimeoutMultiplier;
                    var elapsed = node.Clock.Elapsed;
                    if (elapsed.TotalSeconds < limit) continue;

                    if (MarkFailed(node, new TimeoutException(TimeoutMessage(node, limit)), elapsed))
                        (expired ??= new List<ServiceNode>()).Add(node);
                }

                if (expired != null)
                {
                    var required = expired.Exists(n => !n.Optional);
                    var stopping = required && Transition(StopKind.Failure);
                    foreach (var node in expired) _observers.ServiceFailed(Snapshot(node), node.Error!);
                    foreach (var node in expired)
                    {
                        if (node.Optional) Release(node);
                    }

                    // Every step below runs whatever the cancellation callbacks (user code) do: the
                    // released dependents must start, and a failing run must settle.
                    foreach (var node in expired) Cancellation.Cancel(node.Cts, _logger, _scope, node.Name);
                    if (stopping) CancelAndSettle();
                    else if (!required) Pump();
                    return;
                }
            }

            if (_options.StallWarningAfter <= TimeSpan.Zero) return;
            var since = _clock.Elapsed - _lastProgress;
            if (since < _options.StallWarningAfter) return;

            var running = _inFlight.Where(n => !n.UserGated).OrderBy(n => n.Index).ToList();
            var gated = _inFlight.Where(n => n.UserGated).OrderBy(n => n.Index).ToList();
            if (running.Count == 0 && gated.Count == 0) return;

            // Touch first: a logger that throws must not turn every later tick into a stall warning.
            Touch();
            if (running.Count == 0)
            {
                _logger.Info($"[RuntimeFlow] {_scope}: awaiting player: {string.Join(", ", gated.Select(Describe))}.");
                return;
            }

            _logger.Warn(StallMessage(running, gated));
        }

        private string TimeoutMessage(ServiceNode node, double limitSeconds)
            => $"{node.Name} did not complete within {Fmt.S1(limitSeconds)} (limit {Fmt.N(node.TimeoutSeconds)}s from " +
               $"[Init(TimeoutSeconds = {Fmt.N(node.TimeoutSeconds)})], multiplier {Fmt.Mul(_options.TimeoutMultiplier)}).";

        private string StallMessage(List<ServiceNode> running, List<ServiceNode> gated)
        {
            var text = new StringBuilder();
            text.Append("[RuntimeFlow] ").Append(_scope).Append(": no progress for ")
                .Append(Fmt.S1(_options.StallWarningAfter)).Append('.');
            text.Append(" Running: ").Append(Fmt.Capped(running.Select(Describe).ToList(), 5)).Append('.');
            if (gated.Count > 0)
                text.Append(" Awaiting player: ").Append(Fmt.Capped(gated.Select(Describe).ToList(), 5)).Append('.');

            var blocked = _graph.Services
                .Where(n => n.State == ServiceState.Pending)
                .OrderBy(n => n.Index)
                .Select(n => $"{n.Name} (waits for {string.Join(", ", n.UnmetDependencies())})")
                .ToList();
            if (blocked.Count > 0)
                text.Append(" Blocked: ").Append(Fmt.Capped(blocked, 5)).Append('.');

            return text.ToString();
        }

        private static string Describe(ServiceNode node) => $"{node.Name} ({Fmt.S1(node.Clock.Elapsed)})";
    }
}
