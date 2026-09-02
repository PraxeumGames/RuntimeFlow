using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RuntimeFlow.Internal;
using VContainer;
using VContainer.Unity;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using Application = UnityEngine.Application;

namespace RuntimeFlow
{
    /// <summary>
    /// Owns the two long-lived scopes of a game: a Global container that is built once and stays warm,
    /// and a Session child scope that can be rebuilt from scratch by <see cref="RestartAsync"/>.
    /// The host composes both installers (registering itself, a VContainer entry-point exception handler
    /// and the entry-point dispatcher), runs each scope's initialization graph through a
    /// <see cref="ScopeRun"/>, and tears everything down in the right order on <see cref="DisposeAsync"/>.
    /// </summary>
    /// <remarks>
    /// Every member is meant to be used from the Unity main thread; the host never introduces threads or
    /// locks of its own. Restarts are deferred (safe to request from inside
    /// <see cref="IAsyncInitializable.InitializeAsync"/>), coalesced and budgeted.
    /// </remarks>
    public sealed class RuntimeFlowHost : IAsyncDisposable
    {
        private readonly Action<IContainerBuilder>? _globalInstaller;
        private readonly Action<IContainerBuilder> _sessionInstaller;
        private readonly RuntimeFlowOptions _options;
        private readonly ILogger _logger;
        private readonly bool _ownsGlobal;
        private readonly List<ScopeRun> _childRuns = new List<ScopeRun>();
        private readonly List<Exception> _entryPointErrors = new List<Exception>();
        private readonly List<RestartRequest> _restarts = new List<RestartRequest>();
        private readonly Stopwatch _uptime = Stopwatch.StartNew();
        private readonly QuitHook _quitHook;

        private IReadOnlyList<string> _globalDegraded = Array.Empty<string>();
        private IObjectResolver? _global;
        private IScopedObjectResolver? _session;
        private ScopeRun? _globalRun;
        private ScopeRun? _sessionRun;
        private Task<StartupResult>? _current;
        private int _requestedGeneration;
        private int _startedGeneration = -1;
        private bool _quitting;
        private bool _disposed;

        /// <summary>
        /// Creates a host that owns both scopes: <paramref name="global"/> builds the root container and
        /// <paramref name="session"/> the session scope rebuilt on every restart.
        /// </summary>
        /// <param name="global">Registers the services that survive restarts.</param>
        /// <param name="session">Registers the services rebuilt by every restart.</param>
        /// <param name="options">Shared options; defaults are used when null.</param>
        public RuntimeFlowHost(
            Action<IContainerBuilder> global,
            Action<IContainerBuilder> session,
            RuntimeFlowOptions? options = null)
            : this(null, global ?? throw new ArgumentNullException(nameof(global)), session, options, true)
        {
        }

        private RuntimeFlowHost(
            IObjectResolver? existingGlobal,
            Action<IContainerBuilder>? global,
            Action<IContainerBuilder> session,
            RuntimeFlowOptions? options,
            bool ownsGlobal)
        {
            _global = existingGlobal;
            _globalInstaller = global;
            _sessionInstaller = session ?? throw new ArgumentNullException(nameof(session));
            _options = options ?? new RuntimeFlowOptions();
            _logger = _options.Logger;
            _ownsGlobal = ownsGlobal;
            _quitHook = new QuitHook(this);
            FlowRegistry.Add(this);
        }

        /// <summary>
        /// Creates a host around a container somebody else built (a root <c>LifetimeScope</c>, for example).
        /// The host never disposes <paramref name="existingGlobal"/>, but it does run that scope's own
        /// initialization graph once on <see cref="StartAsync"/>, so the container must not have been
        /// initialized by another host already.
        /// </summary>
        /// <param name="existingGlobal">A built container the caller keeps ownership of.</param>
        /// <param name="session">Registers the services of the session scope created below it.</param>
        /// <param name="options">Shared options; defaults are used when null.</param>
        public static RuntimeFlowHost From(
            IObjectResolver existingGlobal,
            Action<IContainerBuilder> session,
            RuntimeFlowOptions? options = null)
        {
            if (existingGlobal == null) throw new ArgumentNullException(nameof(existingGlobal));
            return new RuntimeFlowHost(existingGlobal, null, session, options, false);
        }

        /// <summary>The root container; throws until <see cref="StartAsync"/> has built it.</summary>
        public IObjectResolver Global => _global
            ?? throw new InvalidOperationException("The global scope does not exist yet; await RuntimeFlowHost.StartAsync first.");

        /// <summary>The current session scope; throws until <see cref="StartAsync"/> has created it.</summary>
        public IScopedObjectResolver Session => _session
            ?? throw new InvalidOperationException("The session scope does not exist yet; await RuntimeFlowHost.StartAsync first.");

        /// <summary>State of the session run, or of the global run while the session does not exist.</summary>
        public RunState State => _disposed
            ? RunState.Disposed
            : _sessionRun?.State ?? _globalRun?.State ?? RunState.NotStarted;

        /// <summary>Number of completed or attempted restarts of the session scope.</summary>
        public int RestartCount { get; private set; }

        /// <summary>Zero for the first session, incremented by one per restart; travels in <see cref="InitContext"/>.</summary>
        public int Generation { get; private set; }

        /// <summary>True once the application announced it is quitting; further restarts are refused.</summary>
        public bool IsQuitting => _quitting;

        /// <summary>
        /// Builds the global scope (unless it was supplied by <see cref="From"/>), runs its graph, then
        /// creates the session scope and runs its graph. Calling it again returns the current run.
        /// </summary>
        /// <param name="cancellationToken">Cancels both runs.</param>
        /// <returns>
        /// The result of the last run in the chain — a restart requested mid-startup redirects this awaiter.
        /// <see cref="StartupResult.Degraded"/> covers both scopes, global services first.
        /// </returns>
        public Task<StartupResult> StartAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (_current != null) return AwaitFinalAsync(_current);

            var task = StartCoreAsync(cancellationToken);
            _current = task;
            return AwaitFinalAsync(task);
        }

        /// <summary>
        /// Tears the session scope down and builds it again with <see cref="InitContext.IsRestart"/> set.
        /// The work is deferred, so a service may call this from its own initialization; requests that
        /// arrive while a restart is pending are coalesced into it.
        /// </summary>
        /// <param name="reason">Short machine-readable reason, logged and listed in budget errors.</param>
        /// <param name="cancellationToken">Cancels the new session run.</param>
        /// <returns>
        /// The result of the last run in the chain; <see cref="StartupResult.Degraded"/> covers both scopes.
        /// </returns>
        /// <exception cref="RuntimeFlowException">More restarts than <see cref="RuntimeFlowOptions.MaxRestartsPerWindow"/>.</exception>
        public Task<StartupResult> RestartAsync(string reason, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (reason == null) throw new ArgumentNullException(nameof(reason));

            if (_quitting)
            {
                _logger.Warn($"[RuntimeFlow] restart '{reason}' refused: the application is quitting.");
                return Task.FromException<StartupResult>(new InvalidOperationException(
                    $"The application is quitting; the restart '{reason}' was refused."));
            }

            if (_current == null)
            {
                return Task.FromException<StartupResult>(new InvalidOperationException(
                    "RuntimeFlowHost.RestartAsync was called before StartAsync; start the host first."));
            }

            if (_requestedGeneration > _startedGeneration)
            {
                _logger.Info(_startedGeneration < 0
                    ? $"[RuntimeFlow] restart '{reason}' coalesced with the startup already in progress"
                    : $"[RuntimeFlow] restart '{reason}' coalesced with the restart already in progress");
                return AwaitFinalAsync(_current);
            }

            var overBudget = ChargeBudget(reason);
            if (overBudget != null) return Task.FromException<StartupResult>(overBudget);

            _logger.Info($"[RuntimeFlow] restart requested: '{reason}' (" +
                         (_sessionRun?.State == RunState.Running ? "session run in progress: cancelling" : "session idle") + ")");

            _requestedGeneration = Generation + 1;
            var task = RestartCoreAsync(cancellationToken);
            _current = task;
            return AwaitFinalAsync(task);
        }

        /// <summary>
        /// Runs the initialization graph of a scope created below this host, for example
        /// <c>host.Session.CreateScope(installer)</c>. Services of the parent scopes are treated as
        /// already initialized externals; the returned run is disposed with the session.
        /// </summary>
        /// <param name="scope">A descendant of <see cref="Session"/> or <see cref="Global"/>.</param>
        /// <param name="name">Name used in messages and status, for example "lobby".</param>
        /// <param name="cancellationToken">Cancels the child run.</param>
        /// <exception cref="InitGraphException">The scope is not a descendant of this host, or its graph is invalid.</exception>
        public async Task<ScopeRun> InitializeScopeAsync(
            IScopedObjectResolver scope,
            string name,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (_globalRun == null)
                throw new InvalidOperationException("The host has no scopes yet; await RuntimeFlowHost.StartAsync first.");

            var run = ScopeRun.Create(scope, name, _options, ParentsOf(scope, name), ownsScope: true);
            _childRuns.Add(run);
            await run.RunAsync(false, Generation, cancellationToken);
            return run;
        }

        /// <summary>
        /// One snapshot covering both scopes: the services of the global scope followed by those of the
        /// session, with <see cref="RuntimeFlowStatus.Percent"/> weighted across all of them.
        /// </summary>
        public RuntimeFlowStatus GetStatus()
        {
            var global = _globalRun?.GetStatus();
            var session = _sessionRun?.GetStatus();
            var primary = session ?? global;
            if (primary == null)
            {
                return new RuntimeFlowStatus(State, "host", null, Array.Empty<ServiceStatus>(),
                    Array.Empty<ServiceStatus>(), 0, 0, 0.0, _uptime.Elapsed, RestartCount, null, null);
            }

            var services = new List<ServiceStatus>();
            var running = new List<ServiceStatus>();
            var completed = 0;
            if (global != null) Merge(global, services, running, ref completed);
            if (session != null) Merge(session, services, running, ref completed);

            double weight = 0, done = 0;
            foreach (var service in services)
            {
                weight += service.Weight;
                if (service.State == ServiceState.Completed || service.State == ServiceState.Degraded) done += service.Weight;
                else if (service.State == ServiceState.Running) done += service.Weight * service.Progress;
            }

            var percent = weight > 0 ? done / weight * 100.0 : (services.Count == 0 ? 100.0 : 0.0);
            return new RuntimeFlowStatus(State, primary.Scope, primary.Phase, services, running,
                completed, services.Count, percent, primary.Elapsed, RestartCount,
                session?.HaltReason ?? global?.HaltReason, session?.Error ?? global?.Error);
        }

        /// <summary>Human-readable rendering of both graphs, global first.</summary>
        public string Describe()
        {
            var text = new StringBuilder();
            if (_globalRun != null) text.Append(_globalRun.Describe());
            if (_sessionRun != null)
            {
                if (text.Length > 0) text.AppendLine().AppendLine();
                text.Append(_sessionRun.Describe());
            }
            return text.Length == 0 ? "RuntimeFlowHost: no scope has been built yet." : text.ToString();
        }

        /// <summary>
        /// Tears everything down in dependency order: child runs in reverse creation order, then the
        /// session, then the global run (whose container is disposed only when the host built it).
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _quitHook.Detach();

            await DisposeChildRunsAsync();

            if (_sessionRun != null)
            {
                await _sessionRun.DisposeAsync();
                _sessionRun = null;
            }
            if (_globalRun != null)
            {
                await _globalRun.DisposeAsync();
                _globalRun = null;
            }

            FlowRegistry.Remove(this);
        }

        /// <summary>Reacts to <see cref="Application.quitting"/>; exposed for tests that simulate a quit.</summary>
        internal void OnQuitting()
        {
            if (_quitting || _disposed) return;
            _quitting = true;
            _logger.Info("[RuntimeFlow] the application is quitting: cancelling the current run and refusing restarts.");
            _ = _sessionRun?.CancelAsync();
            _ = _globalRun?.CancelAsync();
        }

        /// <summary>Child runs created by <see cref="InitializeScopeAsync"/>, in creation order.</summary>
        internal IReadOnlyList<ScopeRun> ChildRuns => _childRuns;

        /// <summary>Run of the global scope, or null before <see cref="StartAsync"/>; read by the editor dashboard.</summary>
        internal ScopeRun? GlobalRun => _globalRun;

        /// <summary>Run of the current session scope, or null before it exists; read by the editor dashboard.</summary>
        internal ScopeRun? SessionRun => _sessionRun;

        private async Task<StartupResult> StartCoreAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();

            if (_globalRun == null)
            {
                try
                {
                    if (_ownsGlobal)
                    {
                        _entryPointErrors.Clear();
                        var builder = new ContainerBuilder();
                        Compose(builder, _globalInstaller!);
                        _global = builder.Build();
                        ThrowOnEntryPointErrors("global");
                    }

                    _globalRun = ScopeRun.Create(_global!, "global", _options, null, _ownsGlobal);
                    var globalResult = await _globalRun.RunAsync(false, 0, cancellationToken);
                    _globalDegraded = globalResult.Degraded;
                }
                catch (Exception)
                {
                    await DisposeGlobalAsync();
                    _current = null;
                    throw;
                }
            }

            return await BuildAndRunSessionAsync(false, cancellationToken);
        }

        private async Task<StartupResult> RestartCoreAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();

            var previous = _sessionRun;
            if (previous != null)
            {
                await previous.CancelAsync();
                await DisposeChildRunsAsync();
                await previous.DisposeAsync();
                _sessionRun = null;
                _session = null;
            }

            RestartCount++;
            Generation++;
            return await BuildAndRunSessionAsync(true, cancellationToken);
        }

        private async Task<StartupResult> BuildAndRunSessionAsync(bool isRestart, CancellationToken cancellationToken)
        {
            Task<StartupResult> run;
            try
            {
                _entryPointErrors.Clear();
                var session = Global.CreateScope(builder => Compose(builder, _sessionInstaller));
                _session = session;
                try
                {
                    ThrowOnEntryPointErrors("session");
                    _sessionRun = ScopeRun.Create(session, "session", _options, new[] { _globalRun! }, ownsScope: true);
                }
                catch (Exception)
                {
                    _session = null;
                    session.Dispose();
                    throw;
                }

                _sessionRun.RestartCount = RestartCount;

                // Mark the generation as started before the first InitializeAsync runs: a service that
                // requests a restart synchronously must open a new one instead of joining this one.
                _startedGeneration = Generation;
                run = _sessionRun.RunAsync(isRestart, Generation, cancellationToken);
            }
            finally
            {
                _startedGeneration = Generation;
            }

            return WithGlobalDegraded(await run);
        }

        /// <summary>
        /// Widens the session result to both scopes: <see cref="StartupResult.Degraded"/> lists the global
        /// services that degraded first, then the session's own. Everything else stays the session run's.
        /// </summary>
        private StartupResult WithGlobalDegraded(StartupResult session)
        {
            if (_globalDegraded.Count == 0) return session;

            var degraded = new List<string>(_globalDegraded.Count + session.Degraded.Count);
            degraded.AddRange(_globalDegraded);
            degraded.AddRange(session.Degraded);
            return new StartupResult(session.Outcome, session.Scope, session.Elapsed, degraded,
                session.HaltReason, session.HaltedBy);
        }

        private void Compose(IContainerBuilder builder, Action<IContainerBuilder> installer)
        {
            builder.RegisterInstance(this);
            builder.RegisterEntryPointExceptionHandler(_entryPointErrors.Add);
            installer(builder);
            EntryPointsBuilder.EnsureDispatcherRegistered(builder);
        }

        private async Task<StartupResult> AwaitFinalAsync(Task<StartupResult> task)
        {
            while (true)
            {
                StartupResult? result = null;
                Exception? error = null;
                try
                {
                    result = await task;
                }
                catch (Exception exception)
                {
                    error = exception;
                }

                var latest = _current;
                if (latest == null || ReferenceEquals(latest, task))
                {
                    if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
                    return result!;
                }

                task = latest;
            }
        }

        private Exception? ChargeBudget(string reason)
        {
            var now = _uptime.Elapsed;
            var window = _options.RestartWindow;
            if (window > TimeSpan.Zero)
            {
                for (var i = _restarts.Count - 1; i >= 0; i--)
                {
                    if (now - _restarts[i].At > window) _restarts.RemoveAt(i);
                }
            }

            _restarts.Add(new RestartRequest(now, reason));
            var limit = _options.MaxRestartsPerWindow;
            if (limit <= 0 || _restarts.Count <= limit) return null;

            var reasons = new string[_restarts.Count];
            for (var i = 0; i < _restarts.Count; i++) reasons[i] = _restarts[i].Reason;

            return new RuntimeFlowException("session",
                $"Restart budget exceeded: {_restarts.Count.ToString(CultureInfo.InvariantCulture)} restarts within " +
                $"{Fmt.N(window.TotalSeconds)}s (limit {limit.ToString(CultureInfo.InvariantCulture)}). " +
                $"Reasons: {string.Join(", ", reasons)}");
        }

        private IReadOnlyList<ScopeRun> ParentsOf(IScopedObjectResolver scope, string name)
        {
            var current = scope.Parent;
            while (current != null)
            {
                if (_sessionRun != null && ReferenceEquals(current, _session))
                    return new[] { _globalRun!, _sessionRun };
                if (ReferenceEquals(current, _global))
                    return new[] { _globalRun! };
                current = current.Parent;
            }

            if (ReferenceEquals(scope.Root, _global)) return new[] { _globalRun! };

            throw new InitGraphException(name,
                $"Scope '{name}' does not belong to this host: its parent chain reaches neither the session nor the " +
                "global scope. Create it with host.Session.CreateScope(...) or host.Global.CreateScope(...).");
        }

        private async Task DisposeChildRunsAsync()
        {
            for (var i = _childRuns.Count - 1; i >= 0; i--)
            {
                await _childRuns[i].DisposeAsync();
            }
            _childRuns.Clear();
        }

        private async Task DisposeGlobalAsync()
        {
            if (_globalRun != null)
            {
                await _globalRun.DisposeAsync();
                _globalRun = null;
            }
            else if (_ownsGlobal)
            {
                _global?.Dispose();
            }

            if (_ownsGlobal) _global = null;
        }

        private void ThrowOnEntryPointErrors(string scope)
        {
            if (_entryPointErrors.Count == 0) return;

            var errors = _entryPointErrors.ToArray();
            _entryPointErrors.Clear();

            var failures = new (string Service, Exception Error)[errors.Length];
            for (var i = 0; i < errors.Length; i++) failures[i] = (EntryPointName(errors[i]), errors[i]);

            var text = new StringBuilder();
            text.Append("Initialization of scope '").Append(scope).Append("' failed: ");
            if (errors.Length == 1)
            {
                text.Append(failures[0].Service).Append(" threw ").Append(errors[0].GetType().Name)
                    .Append(" while VContainer was running the IInitializable entry points of the scope: ")
                    .Append(errors[0].Message).Append(". See InnerException.");
            }
            else
            {
                text.Append(errors.Length.ToString(CultureInfo.InvariantCulture))
                    .Append(" entry points threw while VContainer was running them — ");
                for (var i = 0; i < errors.Length; i++)
                {
                    if (i > 0) text.Append(", ");
                    text.Append(failures[i].Service).Append(" (").Append(errors[i].GetType().Name)
                        .Append(": ").Append(errors[i].Message).Append(')');
                }
                text.Append(". InnerException is an AggregateException with the original exceptions.");
            }

            Exception inner = errors.Length == 1 ? errors[0] : new AggregateException(errors);
            throw new RuntimeFlowException(text.ToString(), scope, failures[0].Service, null, TimeSpan.Zero,
                Array.Empty<string>(), Array.Empty<string>(), failures, inner);
        }

        /// <summary>
        /// Best-effort name of the entry point that threw: the declaring type of the throwing method.
        /// Stack information can be missing (stripped builds), hence the generic fallback.
        /// </summary>
        private static string EntryPointName(Exception error)
            => error.TargetSite?.DeclaringType?.Name ?? "an entry point";

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RuntimeFlowHost));
        }

        private static void Merge(
            RuntimeFlowStatus status,
            List<ServiceStatus> services,
            List<ServiceStatus> running,
            ref int completed)
        {
            services.AddRange(status.Services);
            running.AddRange(status.Running);
            completed += status.CompletedCount;
        }

        private readonly struct RestartRequest
        {
            public RestartRequest(TimeSpan at, string reason)
            {
                At = at;
                Reason = reason;
            }

            public TimeSpan At { get; }
            public string Reason { get; }
        }

        /// <summary>
        /// Bridges <see cref="Application.quitting"/> to the host without letting the static event keep
        /// the host alive, so a forgotten host can still be collected and pruned from the registry.
        /// </summary>
        private sealed class QuitHook
        {
            private readonly WeakReference<RuntimeFlowHost> _host;

            public QuitHook(RuntimeFlowHost host)
            {
                _host = new WeakReference<RuntimeFlowHost>(host);
                Application.quitting += Forward;
            }

            public void Detach() => Application.quitting -= Forward;

            private void Forward()
            {
                if (_host.TryGetTarget(out var host)) host.OnQuitting();
            }
        }
    }
}
