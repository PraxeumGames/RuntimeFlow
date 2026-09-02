using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    /// Single-threaded by construction: no locks, every await continues on the captured context.
    /// </summary>
    internal sealed class Scheduler
    {
        private enum StopKind { None, Failure, Halt, Cancel }

        private readonly ServiceGraph _graph;
        private readonly RuntimeFlowOptions _options;
        private readonly ObserverList _observers;
        private readonly ILogger _logger;
        private readonly string _scope;

        private readonly List<ServiceNode> _ready = new List<ServiceNode>();
        private readonly List<ServiceNode> _inFlight = new List<ServiceNode>();
        private readonly List<ServiceNode> _completionOrder = new List<ServiceNode>();
        private readonly List<string> _degraded = new List<string>();
        private readonly ReadOnlyCollection<string> _degradedView;
        private readonly List<(string Service, Exception Error, TimeSpan Elapsed)> _failures =
            new List<(string, Exception, TimeSpan)>();
        private readonly Stopwatch _clock = new Stopwatch();

        private CancellationTokenSource? _runCts;
        private CancellationTokenRegistration _callerRegistration;
        private CancellationToken _callerToken;
        private TaskCompletionSource<StartupResult>? _done;
        private StallWatch? _watch;
        private StopKind _stop = StopKind.None;
        private bool _pumping;
        private bool _finished;
        private bool _isRestart;
        private int _generation;
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
            _observers = new ObserverList(options.Observers, _logger, _scope);
            _degradedView = new ReadOnlyCollection<string>(_degraded);
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
        public Task<StartupResult> RunAsync(bool isRestart, int generation, CancellationToken cancellationToken)
        {
            if (State != RunState.NotStarted)
                throw new InvalidOperationException($"The '{_scope}' run has already been started; create a new ScopeRun for another run.");

            if (SynchronizationContext.Current == null)
                _logger.Warn($"[RuntimeFlow] {_scope}: SynchronizationContext.Current is null; continuations run inline on the calling thread.");

            _isRestart = isRestart;
            _generation = generation;
            _callerToken = cancellationToken;
            State = RunState.Running;
            _done = new TaskCompletionSource<StartupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _runCts = new CancellationTokenSource();
            _clock.Restart();
            _lastProgress = TimeSpan.Zero;

            _observers.RunStarted(isRestart);
            _logger.Info(StartMessage(isRestart));

            if (cancellationToken.IsCancellationRequested)
            {
                BeginStop(StopKind.Cancel);
                return _done.Task;
            }
            if (cancellationToken.CanBeCanceled)
                _callerRegistration = cancellationToken.Register(() => BeginStop(StopKind.Cancel));

            if (_graph.Phases.Count > 0)
            {
                _currentPhase = _graph.Phases[0];
                _observers.PhaseStarted(_currentPhase);
            }

            _watch = new StallWatch(StallWatch.IntervalFor(_options, _graph.Services), OnWatchTick);
            _watch.Start(_runCts.Token);

            foreach (var node in _graph.Services)
            {
                if (node.ConstructionError == null || _stop != StopKind.None) continue;
                node.Clock.Reset();
                FailNode(node, node.ConstructionError, TimeSpan.Zero);
            }

            if (_stop == StopKind.None)
            {
                foreach (var node in _graph.Nodes)
                {
                    if (node.State == ServiceState.Pending && node.PendingDeps == 0) _ready.Add(node);
                }
                Pump();
            }

            return _done.Task;
        }

        /// <summary>Cancels the run (if any) and waits for it to settle; never throws.</summary>
        public async Task CancelAsync()
        {
            if (State == RunState.Running)
            {
                BeginStop(StopKind.Cancel);
                try { await _done!.Task; }
                catch (Exception) { /* teardown never throws */ }
                return;
            }

            _watch?.Stop();
            _runCts?.Cancel();
        }

        /// <summary>Cancels every service token and releases the run's cancellation sources.</summary>
        public void DisposeTokens()
        {
            _watch?.Stop();
            _callerRegistration.Dispose();
            try { _runCts?.Cancel(); } catch (ObjectDisposedException) { }
            foreach (var node in _graph.Services)
            {
                node.Context?.Abandon();
                node.Cts?.Dispose();
                node.Cts = null;
            }
            _runCts?.Dispose();
            _runCts = null;
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

            var percent = weight > 0 ? done / weight * 100.0 : (services.Count == 0 ? 100.0 : 0.0);
            return new RuntimeFlowStatus(State, _scope, _currentPhase, services, running,
                completed, services.Count, percent, _clock.Elapsed, RestartCount, _haltReason, Error);
        }

        private ServiceStatus Snapshot(ServiceNode node) => new ServiceStatus(
            node.Name, node.Scope, node.Phase, node.State, node.Optional, node.UserGated,
            node.UserGated && node.State == ServiceState.Running, node.Clock.Elapsed, node.Progress, node.Weight,
            node.Deps.Select(e => e.Target.DisplayName).ToList(), node.UnmetDependencies(), node.Error);

        private void Pump()
        {
            if (_pumping) return;
            _pumping = true;
            try
            {
                while (_stop == StopKind.None && _ready.Count > 0)
                {
                    _ready.Sort((a, b) => a.Index.CompareTo(b.Index));
                    var node = _ready[0];
                    _ready.RemoveAt(0);
                    if (node.State != ServiceState.Pending) continue;
                    if (node.Kind == NodeKind.Barrier) CompleteBarrier(node);
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
            node.Context = new InitContext(_scope, _isRestart, _generation, _degradedView,
                reason => RequestHalt(node, reason), fraction => ReportProgress(node, fraction));

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
            _ = ObserveAsync(node, task);
        }

        private async Task ObserveAsync(ServiceNode node, Task task)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException cancelled)
            {
                if (node.State != ServiceState.Running) return;
                if (_stop != StopKind.None || node.Cts == null || node.Cts.IsCancellationRequested) CancelNode(node);
                else FailNode(node, cancelled, node.Clock.Elapsed);
                return;
            }
            catch (Exception exception)
            {
                if (node.State != ServiceState.Running) return;
                FailNode(node, exception, node.Clock.Elapsed);
                return;
            }

            if (node.State != ServiceState.Running) return;
            CompleteNode(node);
        }

        private void CompleteNode(ServiceNode node)
        {
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
            node.State = ServiceState.Cancelled;
            node.Clock.Stop();
            _inFlight.Remove(node);
        }

        private void FailNode(ServiceNode node, Exception error, TimeSpan elapsed)
        {
            node.Clock.Stop();
            node.Error = error;
            _inFlight.Remove(node);

            if (node.Optional)
            {
                node.State = ServiceState.Degraded;
                _degraded.Add(node.Name);
                _completionOrder.Add(node);
                Touch();
                _logger.Warn(DegradedMessage(node, error, elapsed));
                _observers.ServiceFailed(Snapshot(node), error);
                Release(node);
                Pump();
                return;
            }

            node.State = ServiceState.Failed;
            _failures.Add((node.Name, error, elapsed));
            _observers.ServiceFailed(Snapshot(node), error);
            BeginStop(StopKind.Failure);
        }

        private void Release(ServiceNode node)
        {
            foreach (var dependent in node.Dependents)
            {
                if (--dependent.PendingDeps == 0 && dependent.State == ServiceState.Pending)
                    _ready.Add(dependent);
            }
        }

        private void RequestHalt(ServiceNode node, string reason)
        {
            if (_stop != StopKind.None || State != RunState.Running)
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
            node.Progress = fraction < 0f ? 0f : (fraction > 1f ? 1f : fraction);
            Touch();
        }

        private void Touch() => _lastProgress = _clock.Elapsed;

        private void BeginStop(StopKind kind)
        {
            if (_stop != StopKind.None) return;
            _stop = kind;
            State = kind == StopKind.Failure ? RunState.Failed
                : kind == StopKind.Halt ? RunState.Halted
                : RunState.Cancelled;
            _ready.Clear();
            try { _runCts?.Cancel(); } catch (ObjectDisposedException) { }
            _ = SettleAsync();
        }

        private void TryFinish()
        {
            if (_finished || _stop != StopKind.None || State != RunState.Running) return;
            if (_inFlight.Count > 0 || _ready.Count > 0) return;
            foreach (var node in _graph.Nodes)
            {
                if (!node.IsTerminal) return;
            }
            Finish();
        }

        private async Task SettleAsync()
        {
            await Task.Yield();

            var pending = new List<Task>();
            foreach (var node in _inFlight)
            {
                if (node.Task != null) pending.Add(Swallow(node.Task));
            }

            if (pending.Count > 0)
            {
                var all = Task.WhenAll(pending);
                var grace = _options.CancellationGrace;
                if (grace <= TimeSpan.Zero) await Task.WhenAny(all, Task.Delay(TimeSpan.Zero));
                else await Task.WhenAny(all, Task.Delay(grace));
                await Task.Yield();
            }

            Finish();
        }

        private static async Task Swallow(Task task)
        {
            try { await task; }
            catch (Exception) { /* observed by ObserveAsync */ }
        }

        private void Finish()
        {
            if (_finished) return;
            _finished = true;
            _watch?.Stop();
            _clock.Stop();
            _callerRegistration.Dispose();

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
            }
            _inFlight.Clear();

            foreach (var node in _graph.Nodes)
            {
                if (node.State == ServiceState.Pending) node.State = ServiceState.Skipped;
            }

            var degraded = _degraded.ToList();
            switch (_stop)
            {
                case StopKind.None:
                {
                    State = RunState.Completed;
                    var result = new StartupResult(StartupOutcome.Completed, _scope, elapsed, degraded);
                    _logger.Info(CompletedMessage(elapsed, degraded));
                    _observers.RunCompleted(result);
                    _done!.TrySetResult(result);
                    break;
                }
                case StopKind.Halt:
                {
                    var result = new StartupResult(StartupOutcome.Halted, _scope, elapsed, degraded, _haltReason, _haltedBy);
                    _logger.Info(HaltMessage(elapsed));
                    _observers.RunHalted(result);
                    _done!.TrySetResult(result);
                    break;
                }
                case StopKind.Failure:
                {
                    var completed = _completionOrder.Where(n => n.State == ServiceState.Completed)
                        .Select(n => n.Name).ToList();
                    var phase = _failures.Count > 0
                        ? _graph.Services.FirstOrDefault(n => n.Name == _failures[0].Service)?.Phase
                        : null;
                    var error = RuntimeFlowException.Create(_scope, phase, elapsed, completed, unfinished, _failures);
                    Error = error;
                    _logger.Error($"[RuntimeFlow] {_scope}: {error.Message}");
                    _observers.RunFailed(error);
                    _done!.TrySetException(error);
                    break;
                }
                default:
                    _done!.TrySetCanceled(_callerToken.IsCancellationRequested ? _callerToken : CancellationToken.None);
                    break;
            }
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

        private void OnWatchTick()
        {
            if (_stop != StopKind.None || State != RunState.Running) return;

            if (_options.TimeoutMultiplier > 0)
            {
                foreach (var node in _inFlight.OrderBy(n => n.Index).ToList())
                {
                    if (node.UserGated || node.TimeoutSeconds <= 0 || node.State != ServiceState.Running) continue;
                    var limit = TimeSpan.FromSeconds(node.TimeoutSeconds * _options.TimeoutMultiplier);
                    if (node.Clock.Elapsed < limit) continue;

                    var elapsed = node.Clock.Elapsed;
                    node.Cts?.Cancel();
                    FailNode(node, new TimeoutException(TimeoutMessage(node, limit)), elapsed);
                    if (_stop != StopKind.None) return;
                }
            }

            if (_options.StallWarningAfter <= TimeSpan.Zero) return;
            var since = _clock.Elapsed - _lastProgress;
            if (since < _options.StallWarningAfter) return;

            var running = _inFlight.Where(n => !n.UserGated).OrderBy(n => n.Index).ToList();
            var gated = _inFlight.Where(n => n.UserGated).OrderBy(n => n.Index).ToList();
            if (running.Count == 0 && gated.Count == 0) return;

            if (running.Count == 0)
            {
                _logger.Info($"[RuntimeFlow] {_scope}: awaiting player: {string.Join(", ", gated.Select(Describe))}.");
                Touch();
                return;
            }

            _logger.Warn(StallMessage(running, gated));
            Touch();
        }

        private string TimeoutMessage(ServiceNode node, TimeSpan limit)
            => $"{node.Name} did not complete within {Fmt.S1(limit)} (limit {Fmt.N(node.TimeoutSeconds)}s from " +
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
