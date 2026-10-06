using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        private static readonly object Retired = new object();

        /// <summary>VContainer keeps its entry-point exception handler internal; it is found by full name.</summary>
        private static readonly Type? EntryPointHandlerType =
            typeof(EntryPointsBuilder).Assembly.GetType("VContainer.Unity.EntryPointExceptionHandler");

        /// <summary>The delegate a registered entry-point exception handler forwards to.</summary>
        private static readonly FieldInfo? EntryPointHandlerDelegate =
            EntryPointHandlerType?.GetField("handler", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly Action<IContainerBuilder>? _globalInstaller;
        private readonly Action<IContainerBuilder> _sessionInstaller;
        private readonly RuntimeFlowOptions _options;
        private readonly ILogger _logger;
        private readonly bool _ownsGlobal;
        private readonly List<ChildRun> _childRuns = new List<ChildRun>();
        private readonly List<Exception> _entryPointErrors = new List<Exception>();
        private readonly List<RestartRequest> _restarts = new List<RestartRequest>();
        private readonly ConditionalWeakTable<IScopedObjectResolver, object> _retiredSessions =
            new ConditionalWeakTable<IScopedObjectResolver, object>();
        // Only identity and a small lifecycle marker survive removal from _childRuns. Keeping the run
        // here would retain its graph and services for as long as the caller holds the resolver.
        private readonly ConditionalWeakTable<IScopedObjectResolver, ChildScopeIdentity> _childScopes =
            new ConditionalWeakTable<IScopedObjectResolver, ChildScopeIdentity>();
        private readonly Stopwatch _uptime = Stopwatch.StartNew();
        private readonly QuitHook _quitHook;

        private IReadOnlyList<string> _globalDegraded = Array.Empty<string>();
        private StartupResult? _globalHalt;
        private IObjectResolver? _global;
        private IScopedObjectResolver? _session;
        private ScopeRun? _globalRun;
        private ScopeRun? _sessionRun;
        private Exception? _sessionBuildError;
        private CancellationTokenSource? _sessionTokenSource;
        private Task<StartupResult>? _current;
        private Task? _disposal;

        // The restart state machine. _pending: a restart was accepted and its session has not been built
        // yet (later requests coalesce into it). _building: a startup or restart chain is between its start
        // and the publication of its session run; a request arriving then has no session to cancel and is
        // honoured by the chain itself, which builds its session as the restart.
        private bool _pending;
        private bool _building;
        private CancellationToken _pendingToken;
        private RestartRequest? _foldedRequest;

        private EntryPointCollector? _collecting;
        private bool _externalGlobalFailed;
        private bool _sessionAbortedByQuit;
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
        /// The host never disposes <paramref name="existingGlobal"/> or its services, but it does run that
        /// scope's own initialization graph once on <see cref="StartAsync"/>, so the container must not have
        /// been initialized by another host already. If that global run fails, the host cannot retry it
        /// (its singletons are half-initialized): build a new container and a new host.
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

        /// <summary>
        /// The root container; throws <see cref="InvalidOperationException"/> until <see cref="StartAsync"/>
        /// has built it and <see cref="ObjectDisposedException"/> once the host is disposed.
        /// </summary>
        public IObjectResolver Global
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException(nameof(RuntimeFlowHost), "The host has been disposed; its global scope is gone.");
                return _global ?? throw new InvalidOperationException("The global scope does not exist yet; await RuntimeFlowHost.StartAsync first.");
            }
        }

        /// <summary>
        /// The current session scope. Throws <see cref="InvalidOperationException"/> until
        /// <see cref="StartAsync"/> has created it, after a session failed to build (the exception is the
        /// <see cref="Exception.InnerException"/>; call <see cref="RestartAsync"/> to build it again), when
        /// the global scope was halted (no session is ever built then), and when a quit tore the session
        /// down; <see cref="ObjectDisposedException"/> once the host is disposed.
        /// </summary>
        public IScopedObjectResolver Session
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException(nameof(RuntimeFlowHost), "The host has been disposed; its session scope is gone.");
                if (_session != null) return _session;
                if (_globalHalt != null)
                {
                    throw new InvalidOperationException(
                        $"No session scope exists: the global scope was halted by {_globalHalt.HaltedBy} ('{_globalHalt.HaltReason}'), " +
                        "so no session is built. The global scope is never rebuilt; dispose this host and create a new one.");
                }
                if (_sessionAbortedByQuit)
                {
                    throw new InvalidOperationException(
                        "No session scope exists: the application is quitting, and the session was torn down without a replacement.");
                }
                if (_sessionBuildError != null)
                {
                    throw new InvalidOperationException(
                        $"The session scope failed to build ({_sessionBuildError.GetType().Name}: {_sessionBuildError.Message}); " +
                        "call RuntimeFlowHost.RestartAsync to build it again.", _sessionBuildError);
                }
                throw new InvalidOperationException("The session scope does not exist yet; await RuntimeFlowHost.StartAsync first.");
            }
        }

        /// <summary>
        /// <see cref="RunState.Running"/> while a startup or restart is in flight (including the teardown of
        /// the generation it replaces), <see cref="RunState.Failed"/> when the last session failed to build,
        /// <see cref="RunState.Cancelled"/> when a quit tore the session down without a replacement,
        /// otherwise the state of the session run, or of the global run while no session exists
        /// (<see cref="RunState.Halted"/> after a halt in the global scope).
        /// </summary>
        public RunState State
        {
            get
            {
                if (_disposed) return RunState.Disposed;
                if (_building || _pending) return RunState.Running;
                if (_sessionRun != null) return _sessionRun.State;
                if (_sessionBuildError != null) return RunState.Failed;
                if (_sessionAbortedByQuit) return RunState.Cancelled;
                return _globalRun?.State ?? RunState.NotStarted;
            }
        }

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
        /// <remarks>
        /// When the global phase fails the host forgets the attempt, so a later call retries it from
        /// scratch (except for a global supplied through <see cref="From"/>). When a global service halts,
        /// no session is built: the result is the global run's <see cref="StartupOutcome.Halted"/> (scope
        /// "global"), later calls return it again, and the host cannot be restarted — the global scope is
        /// never rebuilt. When the global phase succeeded but the session failed, later calls return that
        /// same failure: recover with <see cref="RestartAsync"/>, which rebuilds only the session.
        /// </remarks>
        /// <param name="cancellationToken">Cancels both runs.</param>
        /// <returns>
        /// The result of the last run in the chain — a restart requested mid-startup redirects this awaiter.
        /// <see cref="StartupResult.Degraded"/> covers both scopes, global services first.
        /// </returns>
        public Task<StartupResult> StartAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (_current != null) return AwaitFinalAsync(_current);
            if (_externalGlobalFailed)
            {
                return Task.FromException<StartupResult>(new InvalidOperationException(
                    "The global container supplied through RuntimeFlowHost.From failed to initialize and cannot be " +
                    "initialized twice; build a new container and create a new host."));
            }

            var task = StartCoreAsync(cancellationToken);
            _current = task;
            return AwaitFinalAsync(task);
        }

        /// <summary>
        /// Tears the session scope down and builds it again with <see cref="InitContext.IsRestart"/> set.
        /// The work is deferred, so a service may call this from its own initialization; requests that
        /// arrive while a restart is pending are coalesced into it.
        /// </summary>
        /// <remarks>
        /// <para>Accepting a restart freezes the running session at once — no further service of the doomed
        /// generation starts — while its tokens are cancelled only after the caller's stack unwound.</para>
        /// <para>A request that arrives before the session exists (during the global phase of the startup)
        /// has no session to cancel: the startup builds its first session directly as the restart
        /// (generation 1). If the global phase fails instead, the request is dropped (and not budgeted) and
        /// its awaiters observe the startup failure.</para>
        /// <para>Global services are never rebuilt. A global service that requests a restart must not wait
        /// on its own token afterwards: nothing cancels it, and the startup would never finish.</para>
        /// <para>After a session failed to build, a restart builds a fresh one; this is how a "Retry"
        /// button recovers.</para>
        /// </remarks>
        /// <param name="reason">Short machine-readable reason, logged and listed in budget errors.</param>
        /// <param name="cancellationToken">
        /// Cancels the new session run. A token that is already cancelled yields a cancelled task and tears
        /// nothing down. A token of the run this restart tears down (the requesting service's own token) is
        /// ignored with a warning: it is cancelled by the teardown and would cancel the new generation at
        /// birth. Tokens derived from it (linked sources) cannot be recognised — do not pass them.
        /// </param>
        /// <returns>
        /// The result of the last run in the chain; <see cref="StartupResult.Degraded"/> covers both scopes.
        /// </returns>
        /// <exception cref="RuntimeFlowException">More restarts than <see cref="RuntimeFlowOptions.MaxRestartsPerWindow"/>.</exception>
        /// <exception cref="InvalidOperationException">
        /// The host was not started, the application is quitting, or the global scope was halted (the
        /// global scope is never rebuilt, so there is nothing a restart could build a session on).
        /// </exception>
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

            if (_globalHalt != null)
            {
                _logger.Warn($"[RuntimeFlow] restart '{reason}' refused: the global scope was halted.");
                return Task.FromException<StartupResult>(new InvalidOperationException(
                    $"The restart '{reason}' was refused: the global scope was halted by {_globalHalt.HaltedBy} " +
                    $"('{_globalHalt.HaltReason}'). RestartAsync rebuilds only the session and the global scope is never " +
                    "rebuilt; dispose this host and create a new one to start over."));
            }

            if (_globalRun?.State == RunState.Running)
                _logger.Warn($"[RuntimeFlow] restart '{reason}' requested while the global scope is initializing: {GlobalRestartAdvice}");
            else if (_globalRun != null && _globalRun.OwnsToken(cancellationToken))
                _logger.Warn($"[RuntimeFlow] restart '{reason}' requested with a token of the global scope: {GlobalRestartAdvice}");

            if (OwnedByDoomedRun(cancellationToken))
            {
                _logger.Warn($"[RuntimeFlow] restart '{reason}': the CancellationToken passed to RestartAsync belongs to " +
                             "the run this restart tears down; it is ignored, otherwise the new generation would be " +
                             "cancelled at birth. Pass CancellationToken.None or a token that outlives the restart.");
                cancellationToken = CancellationToken.None;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _logger.Info($"[RuntimeFlow] restart '{reason}' not started: its CancellationToken is already cancelled.");
                return Task.FromCanceled<StartupResult>(cancellationToken);
            }

            if (_pending)
            {
                _logger.Info($"[RuntimeFlow] restart '{reason}' coalesced with the restart already in progress");
                return AwaitFinalAsync(_current);
            }

            var overBudget = ChargeBudget(reason, out var accepted);
            if (overBudget != null) return Task.FromException<StartupResult>(overBudget);

            _pending = true;
            if (_building)
            {
                // No session to cancel: the chain in flight builds its session as this restart.
                _pendingToken = cancellationToken;
                _foldedRequest = accepted;
                _logger.Info($"[RuntimeFlow] restart requested: '{reason}' (no session yet: the next session is built as a restart)");
                return AwaitFinalAsync(_current);
            }

            var running = _sessionRun?.State == RunState.Running;
            _logger.Info($"[RuntimeFlow] restart requested: '{reason}' (" +
                         (_sessionRun == null ? "no session: building one"
                             : running ? "session run in progress: cancelling" : "session idle") + ")");

            // Freeze now, cancel later: nothing of the doomed generation may start on stale data, but
            // cancelling synchronously would re-enter the caller's own InitializeAsync.
            if (running) _sessionRun!.Freeze();
            foreach (var child in _childRuns.ToArray())
            {
                if (child.UnderSession) child.Run.Freeze();
            }

            var task = RestartCoreAsync(cancellationToken);
            _current = task;
            return AwaitFinalAsync(task);
        }

        /// <summary>
        /// Runs the initialization graph of a scope created below this host, for example
        /// <c>host.Session.CreateScope(installer)</c>. Services of every ancestor scope are treated as
        /// already initialized externals; the returned run is disposed with its parent: with the session
        /// (on restart or disposal) when it descends from it, with the host when it descends from global.
        /// </summary>
        /// <remarks>
        /// Every parent run must have completed and no startup or restart may be in flight: a child scope
        /// created from a service of its parent scope, while that scope is still initializing, is refused
        /// instead of running on uninitialized parents. So is a child whose own constructors request a
        /// restart, and every child once the application is quitting. The scope itself is disposed when its
        /// graph is invalid or it is refused after its services were constructed.
        /// </remarks>
        /// <param name="scope">A descendant of <see cref="Session"/> or <see cref="Global"/>.</param>
        /// <param name="name">Name used in messages and status, for example "lobby".</param>
        /// <param name="cancellationToken">Cancels the child run.</param>
        /// <exception cref="InitGraphException">The scope is not a descendant of this host, or its graph is invalid.</exception>
        /// <exception cref="InvalidOperationException">A parent has not completed, or a startup or restart is in flight.</exception>
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
            if (_quitting)
                throw new InvalidOperationException($"Scope '{name}' cannot be initialized: the application is quitting.");
            if (_building || _pending)
            {
                throw new InvalidOperationException(
                    $"Scope '{name}' cannot be initialized while a startup or restart is in flight; " +
                    "initialize child scopes once StartAsync or RestartAsync has completed.");
            }

            var parents = ParentsOf(scope, name, out var underSession);
            foreach (var parent in parents)
            {
                if (parent.State == RunState.Completed) continue;
                throw new InvalidOperationException(
                    $"Scope '{name}' cannot be initialized: its parent scope '{parent.Name}' is {parent.State}, not Completed. " +
                    "Child scopes start once every parent completed; create them after StartAsync or RestartAsync " +
                    "returned, not from a service of the parent scope.");
            }

            // Reserve identity before constructors run: user code can re-enter this host. A failing
            // Create disposes its scope, so its identity must remain retired even without a child run.
            var identity = new ChildScopeIdentity(name);
            _childScopes.Add(scope, identity);
            ScopeRun run;
            try { run = ScopeRun.Create(scope, name, _options, parents, ownsScope: true); }
            catch
            {
                identity.Retired = true;
                throw;
            }

            // Constructing the child's services ran user code: a constructor may have requested a restart
            // (dooming the session this child would run on), disposed the host, or the application may be
            // quitting. The child is not registered yet, so nothing else would freeze or cancel it.
            var refusal = _disposed ? "the host was disposed while its services were being constructed"
                : _quitting ? "the application is quitting"
                : _building || _pending ? "a restart was requested while its services were being constructed"
                : parents.Any(p => p.State != RunState.Completed) ? "a parent scope stopped while its services were being constructed"
                : null;
            if (refusal != null)
            {
                identity.Retired = true;
                await run.DisposeAsync();
                throw new InvalidOperationException($"Scope '{name}' was refused: {refusal}. Create a new child scope once the host is idle.");
            }

            var child = new ChildRun(run, scope, underSession);
            run.Disposing = _ => identity.Retired = true;
            run.Disposed = _ => _childRuns.Remove(child);
            _childRuns.Add(child);
            identity.Constructing = false;
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
                    Array.Empty<ServiceStatus>(), 0, 0, 0.0, _uptime.Elapsed, RestartCount, null, _sessionBuildError);
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

            var percent = weight > 0
                ? done / weight * 100.0
                : (services.Count == 0 || completed == services.Count ? 100.0 : 0.0);
            return new RuntimeFlowStatus(State, primary.Scope, primary.Phase, services, running,
                completed, services.Count, percent, primary.Elapsed, RestartCount,
                session?.HaltReason ?? global?.HaltReason, session?.Error ?? _sessionBuildError ?? global?.Error,
                session?.HaltedBy ?? global?.HaltedBy);
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
        /// Every run — each child run included — is frozen at once, so no further service of any scope
        /// starts once this is called, and cancelled right after a yield, so a service may call this from
        /// its own <see cref="IAsyncInitializable.InitializeAsync"/>. A startup or restart still in flight is
        /// waited for (bounded by twice <see cref="RuntimeFlowOptions.CancellationGrace"/> plus a second;
        /// unbounded when the grace is <see cref="Timeout.InfiniteTimeSpan"/>), so it never builds a scope on
        /// a disposed host; its awaiters complete with <see cref="ObjectDisposedException"/> or a
        /// cancellation. Afterwards <see cref="Global"/> and <see cref="Session"/> throw
        /// <see cref="ObjectDisposedException"/>. Teardown never throws; concurrent calls share it.
        /// </summary>
        public ValueTask DisposeAsync() => new ValueTask(_disposal ??= DisposeCoreAsync());

        private async Task DisposeCoreAsync()
        {
            _disposed = true;
            _quitHook.Detach();

            // Nothing of any run starts once DisposeAsync was called: freeze every run up front — the child
            // runs too, which are disposed one after the other below — and cancel them only after a yield,
            // so a service that calls DisposeAsync from its own InitializeAsync is not cancelled inside it.
            // Finished runs keep their service tokens until their own turn below, in teardown order.
            foreach (var child in _childRuns.ToArray()) child.Run.Freeze();
            _sessionRun?.Freeze();
            _globalRun?.Freeze();
            await Task.Yield();
            foreach (var child in _childRuns.ToArray())
            {
                if (child.Run.State == RunState.Running) _ = child.Run.CancelAsync();
            }
            if (_sessionRun?.State == RunState.Running) _ = _sessionRun.CancelAsync();
            if (_globalRun?.State == RunState.Running) _ = _globalRun.CancelAsync();

            await AbandonChainAsync();
            await DisposeChildRunsAsync(sessionOnly: false);

            if (_sessionRun != null)
            {
                await DisposeRunQuietlyAsync(_sessionRun);
                Retire(_session);
                _sessionRun = null;
            }
            DisposeSessionToken();

            if (_globalRun != null)
            {
                await DisposeRunQuietlyAsync(_globalRun);
                _globalRun = null;
            }

            _session = null;
            _global = null;
            FlowRegistry.Remove(this);
        }

        /// <summary>Reacts to <see cref="Application.quitting"/>; exposed for tests that simulate a quit.</summary>
        internal void OnQuitting()
        {
            if (_quitting || _disposed) return;
            _quitting = true;
            _logger.Info("[RuntimeFlow] the application is quitting: cancelling the current run and refusing restarts.");
            // Child runs too: one frozen by an accepted restart is otherwise never cancelled when the quit
            // stops that restart before it disposes them, and its awaiter would never complete.
            foreach (var child in _childRuns.ToArray()) _ = child.Run.CancelAsync();
            if (_sessionRun != null) _ = _sessionRun.CancelAsync();
            if (_globalRun != null) _ = _globalRun.CancelAsync();
        }

        /// <summary>Child runs created by <see cref="InitializeScopeAsync"/> and not disposed yet, in creation order.</summary>
        internal IReadOnlyList<ScopeRun> ChildRuns => _childRuns.Select(c => c.Run).ToList();

        /// <summary>Run of the global scope, or null before <see cref="StartAsync"/>; read by the editor dashboard.</summary>
        internal ScopeRun? GlobalRun => _globalRun;

        /// <summary>Run of the current session scope, or null before it exists; read by the editor dashboard.</summary>
        internal ScopeRun? SessionRun => _sessionRun;

        /// <summary>True when <see cref="RestartAsync"/> would be accepted (budget aside); read by the editor dashboard.</summary>
        internal bool CanRestart => !_disposed && !_quitting && _current != null;

        private const string GlobalRestartAdvice =
            "global services are not rebuilt by RestartAsync, only the session is. Do not wait on your token after " +
            "requesting a restart from the global scope: nothing cancels it, and the startup would never finish.";

        private async Task<StartupResult> StartCoreAsync(CancellationToken cancellationToken)
        {
            _building = true;
            Task<StartupResult> run;
            try
            {
                await Task.Yield();
                ThrowIfAborted();

                if (_globalRun == null)
                {
                    StartupResult globalResult;
                    try
                    {
                        if (_ownsGlobal)
                        {
                            var builder = new ContainerBuilder();
                            var collector = Compose(builder, _globalInstaller!, "global", null);
                            _entryPointErrors.Clear();
                            _collecting = collector;
                            try { _global = builder.Build(); }
                            finally { _collecting = null; }
                            ThrowOnEntryPointErrors("global");
                        }

                        _globalRun = ScopeRun.Create(_global!, "global", _options, null, _ownsGlobal);
                        _globalRun.DisposesServices = _ownsGlobal;
                        globalResult = await _globalRun.RunAsync(false, 0, cancellationToken);
                        _globalDegraded = globalResult.Degraded;
                    }
                    catch (Exception)
                    {
                        // A restart requested during the global phase never got a session to restart:
                        // drop it (and its budget) so a retried startup starts clean, and let its
                        // awaiters observe the startup failure instead of a misleading restart.
                        DropFoldedRestart();
                        await DisposeGlobalAsync();
                        if (!_ownsGlobal) _externalGlobalFailed = true;
                        _current = null;
                        throw;
                    }

                    if (globalResult.Outcome == StartupOutcome.Halted)
                    {
                        // A halted global scope never builds a session: its skipped services were never
                        // initialized, and a session on top of them would run on half an application. A
                        // restart requested meanwhile has no session to restart and is dropped (unbudgeted).
                        _globalHalt = globalResult;
                        DropFoldedRestart();
                        _building = false;
                        _logger.Info($"[RuntimeFlow] the global scope was halted by {globalResult.HaltedBy} " +
                                     $"('{globalResult.HaltReason}'); no session is built and the host cannot be restarted.");
                        return globalResult;
                    }
                }

                ThrowIfAborted();
                run = StartSession(false, cancellationToken);
            }
            catch (Exception)
            {
                AbortChain();
                throw;
            }

            return WithGlobalDegraded(await run);
        }

        private async Task<StartupResult> RestartCoreAsync(CancellationToken cancellationToken)
        {
            _building = true;
            Task<StartupResult> run;
            try
            {
                await Task.Yield();
                ThrowIfAborted();

                var previous = _sessionRun;
                if (previous != null)
                {
                    await previous.CancelAsync();
                    ThrowIfAborted();
                    await DisposeChildRunsAsync(sessionOnly: true);
                    ThrowIfAborted();
                    await DisposeRunQuietlyAsync(previous);
                    Retire(_session);
                    _sessionRun = null;
                    _session = null;
                    DisposeSessionToken();
                    ThrowIfAborted();
                }

                run = StartSession(true, cancellationToken);
            }
            catch (Exception)
            {
                AbortChain();
                throw;
            }

            return WithGlobalDegraded(await run);
        }

        /// <summary>
        /// Builds the session scope, publishes it and starts its run — synchronously, so no request can
        /// ever observe a built session whose run has not started.
        /// </summary>
        private Task<StartupResult> StartSession(bool isRestart, CancellationToken cancellationToken)
        {
            if (_pending)
            {
                // A restart accepted while no session existed (the global phase, or this chain's own
                // request) is honoured by building this very session as the restart.
                _pending = false;
                _foldedRequest = null;
                isRestart = true;
                RestartCount++;
                Generation++;
                cancellationToken = SessionToken(cancellationToken, _pendingToken);
                _pendingToken = default;
            }

            _sessionBuildError = null;
            IScopedObjectResolver? scope = null;
            ScopeRun session;
            try
            {
                _entryPointErrors.Clear();
                var consumer = ConsumerHandlerOf(_global);
                try { scope = Global.CreateScope(builder => _collecting = Compose(builder, _sessionInstaller, "session", consumer)); }
                finally { _collecting = null; }

                try
                {
                    ThrowOnEntryPointErrors("session");
                }
                catch (Exception)
                {
                    DisposeQuietly(scope, "session");
                    throw;
                }

                // A failing Create disposes the scope it was going to own.
                session = ScopeRun.Create(scope, "session", _options, new[] { _globalRun! }, ownsScope: true);
            }
            catch (Exception exception)
            {
                // A failed build leaves no half-published session behind: the host reports Failed and the
                // next RestartAsync builds a fresh one.
                _sessionBuildError = exception;
                DisposeSessionToken();
                throw;
            }

            session.RestartCount = RestartCount;
            _session = scope;
            _sessionRun = session;
            _building = false;

            if (_pending)
            {
                // Requested while the scope was being built (an entry point or a constructor asked):
                // this session is doomed before it starts, so nothing in it starts at all.
                session.Freeze();
                var token = _pendingToken;
                _pendingToken = default;
                _foldedRequest = null; // honoured by the restart below; its budget entry stays
                _current = RestartCoreAsync(token);
            }

            // The generation counts as started before its first InitializeAsync: a synchronous request
            // from it finds a published, running session and opens a new generation instead of joining it.
            return session.RunAsync(isRestart, Generation, cancellationToken);
        }

        /// <summary>Resets the restart state machine after a chain failed or was aborted before its session ran.</summary>
        private void AbortChain()
        {
            // A request folded into this chain was accepted but never honoured: it gives its budget back.
            if (_foldedRequest != null) _restarts.Remove(_foldedRequest);
            _building = false;
            _pending = false;
            _pendingToken = default;
            _foldedRequest = null;
            if (_quitting && _sessionRun == null) _sessionAbortedByQuit = true;
        }

        private void DropFoldedRestart()
        {
            if (!_pending) return;
            if (_foldedRequest != null) _restarts.Remove(_foldedRequest);
            _pending = false;
            _pendingToken = default;
            _foldedRequest = null;
        }

        /// <summary>
        /// The token of a session run: the chain's own, combined with the one of a restart request that was
        /// folded into it. The linked source lives as long as that session.
        /// </summary>
        private CancellationToken SessionToken(CancellationToken chain, CancellationToken request)
        {
            if (!request.CanBeCanceled || request == chain) return chain;
            if (!chain.CanBeCanceled) return request;
            DisposeSessionToken();
            _sessionTokenSource = CancellationTokenSource.CreateLinkedTokenSource(chain, request);
            return _sessionTokenSource.Token;
        }

        private void DisposeSessionToken()
        {
            _sessionTokenSource?.Dispose();
            _sessionTokenSource = null;
        }

        /// <summary>True when the token is cancelled by the teardown a restart performs.</summary>
        private bool OwnedByDoomedRun(CancellationToken token)
        {
            if (!token.CanBeCanceled) return false;
            if (_sessionRun != null && _sessionRun.OwnsToken(token)) return true;
            foreach (var child in _childRuns.ToArray())
            {
                if (child.UnderSession && child.Run.OwnsToken(token)) return true;
            }
            return false;
        }

        /// <summary>
        /// Stops a startup or restart chain after any await once the host is being disposed or the
        /// application is quitting: nothing may be built on a disposed global or into a dying player.
        /// </summary>
        private void ThrowIfAborted()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RuntimeFlowHost),
                    "The host was disposed while a startup or restart was in flight; no further scope is built.");
            }
            if (_quitting)
                throw new OperationCanceledException("The application is quitting; the startup or restart in flight stops before building another session.");
        }

        /// <summary>
        /// Cancels whatever the chain in flight is waiting for and waits for it to unwind, bounded by twice
        /// the grace plus a second (unbounded when the grace is infinite).
        /// </summary>
        private async Task AbandonChainAsync()
        {
            var grace = _options.CancellationGrace;
            var unbounded = grace == Timeout.InfiniteTimeSpan || grace.TotalMilliseconds * 2 + 1000 > int.MaxValue;
            var bound = unbounded ? TimeSpan.Zero
                : TimeSpan.FromMilliseconds(Math.Max(0, grace.TotalMilliseconds) * 2 + 1000);
            var clock = Stopwatch.StartNew();

            while (true)
            {
                var chain = _current;
                if (chain == null || chain.IsCompleted) return;

                // Snapshot: a cancellation callback (user code) may dispose a child run, which removes it.
                foreach (var child in _childRuns.ToArray()) _ = child.Run.CancelAsync();
                if (_sessionRun != null) _ = _sessionRun.CancelAsync();
                if (_globalRun?.State == RunState.Running) _ = _globalRun.CancelAsync();

                Task winner;
                if (unbounded)
                {
                    winner = await Task.WhenAny(chain);
                }
                else
                {
                    var remaining = bound - clock.Elapsed;
                    winner = remaining > TimeSpan.Zero ? await Task.WhenAny(chain, Task.Delay(remaining)) : Task.CompletedTask;
                }

                if (!ReferenceEquals(winner, chain))
                {
                    _logger.Warn($"[RuntimeFlow] dispose: the startup or restart in flight did not unwind within {Fmt.S1(bound)}; disposing anyway.");
                    return;
                }
                _ = chain.Exception; // observed: its awaiters get the outcome through AwaitFinalAsync
            }
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

        /// <summary>
        /// Composes a scope: the host itself, the host's entry-point exception collector — registered
        /// <em>before</em> the consumer installer, so nothing (VContainer 1.19's
        /// <c>EnsureDispatcherRegistered</c>, which registers <c>Debug.LogException</c> in every scope that has
        /// no handler yet) can slip a default handler in first, and a handler the installer registers
        /// afterwards still wins (VContainer resolves the last registration) — then the installer, the
        /// inspection pass and the dispatcher.
        /// </summary>
        /// <param name="consumer">The consumer handler of a parent scope, which stays in charge of this one.</param>
        private EntryPointCollector Compose(IContainerBuilder builder, Action<IContainerBuilder> installer, string scope,
            Action<Exception>? consumer)
        {
            builder.RegisterInstance(this);
            var collector = new EntryPointCollector(this, consumer);
            builder.RegisterEntryPointExceptionHandler(collector.Handle);
            var index = builder.Count - 1;
            var ours = builder[index];

            installer(builder);

            if (Inspect(builder, scope) > 1 && index < builder.Count && ReferenceEquals(builder[index], ours))
            {
                // The consumer registered a handler of its own: it supersedes the collector. VContainer 1.15
                // refuses two singleton handlers ("Conflict implementation type"), so the collector's entry is
                // replaced by an inert registration instead of competing with the consumer's.
                builder[index] = new RegistrationBuilder(typeof(SupersededEntryPointCollector), Lifetime.Transient);
            }
            EntryPointsBuilder.EnsureDispatcherRegistered(builder);
            return collector;
        }

        /// <summary>
        /// Receives every exception VContainer's entry points throw into a scope the host composed. While
        /// that scope is being built these are the IInitializable failures that fail the build; afterwards
        /// they come from the player loop (IStartable, ITickable, IAsyncStartable...) and are logged, never
        /// swallowed. When a parent scope has a consumer handler, it receives both instead.
        /// </summary>
        private void OnEntryPointError(EntryPointCollector collector, Exception error)
        {
            if (collector.Consumer != null)
            {
                // The consumer's choice, rethrow included, exactly as if VContainer had called it directly.
                collector.Consumer(error);
                return;
            }

            if (ReferenceEquals(_collecting, collector))
            {
                _entryPointErrors.Add(error);
                return;
            }

            _logger.Error($"[RuntimeFlow] {EntryPointName(error)} threw {error.GetType().Name} in a VContainer entry point " +
                          $"after its scope was built: {error.Message}", error);
        }

        /// <summary>
        /// The consumer's entry-point exception handler that <paramref name="scope"/> (or a scope above it)
        /// resolves, or null: none, the host's own collector without a consumer behind it, or VContainer
        /// 1.19's default <c>Debug.LogException</c>, which only logs and is not a consumer's decision.
        /// </summary>
        private static Action<Exception>? ConsumerHandlerOf(IObjectResolver? scope)
        {
            if (scope == null || EntryPointHandlerType == null || EntryPointHandlerDelegate == null) return null;
            try
            {
                if (!HasEntryPointHandler(scope)) return null;
                var handler = scope.Resolve(EntryPointHandlerType);
                if (!(EntryPointHandlerDelegate.GetValue(handler) is Action<Exception> action)) return null;
                if (action.Target is EntryPointCollector collector) return collector.Consumer;
                var method = action.Method;
                if (action.Target == null && method.DeclaringType == typeof(UnityEngine.Debug) && method.Name == "LogException")
                    return null;
                return action;
            }
            catch (Exception)
            {
                // Diagnostics only: without a readable handler the host's collector takes charge.
                return null;
            }
        }

        private static bool HasEntryPointHandler(IObjectResolver resolver)
        {
            IObjectResolver? current = resolver;
            while (current != null)
            {
                if (current.TryGetRegistration(EntryPointHandlerType!, out _)) return true;
                current = (current as IScopedObjectResolver)?.Parent;
            }
            return false;
        }

        /// <summary>
        /// Looks at what the consumer installer left behind. It warns about a service that implements
        /// <see cref="IAsyncInitializable"/> without exposing it — such a service silently never joins a
        /// graph — and reports a consumer-registered entry-point exception handler, which supersedes the
        /// host's collector (the collector is the first handler registration; any later one wins).
        /// Inspection never registers anything and never aborts composition. Factory registrations only
        /// reveal their declared type, so a factory whose product implements <see cref="IAsyncInitializable"/>
        /// without exposing it cannot be detected here.
        /// </summary>
        /// <returns>The number of entry-point exception handler registrations, the host's collector included.</returns>
        private int Inspect(IContainerBuilder builder, string scope)
        {
            var handlers = 0;
            for (var i = 0; i < builder.Count; i++)
            {
                Registration? registration;
                try
                {
                    // Build() is pure, but a component builder needs a LifetimeScope this early; such a
                    // registration simply cannot be inspected and is left alone.
                    registration = builder[i].Build();
                }
                catch (Exception)
                {
                    continue;
                }

                if (registration == null) continue;

                if (registration.ImplementationType == EntryPointHandlerType
                    || registration.ImplementationType.FullName == "VContainer.Unity.EntryPointExceptionHandler")
                {
                    handlers++;
                    continue;
                }

                if (!typeof(IAsyncInitializable).IsAssignableFrom(registration.ImplementationType)) continue;
                if (ExposesInitializable(registration)) continue;

                _logger.Warn($"[RuntimeFlow] {scope}: {registration.ImplementationType.Name} implements IAsyncInitializable " +
                             "but is registered without exposing it; it will never be initialized. " +
                             "Use RegisterInitializable<T>() or .As<IAsyncInitializable>().");
            }

            if (handlers > 1)
            {
                _logger.Info($"[RuntimeFlow] {scope}: a custom EntryPointExceptionHandler is registered; " +
                             "IInitializable exceptions are delivered to it instead of failing the run.");
            }
            return handlers;
        }

        /// <summary>True when the registration is resolvable as <see cref="IAsyncInitializable"/>.</summary>
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

        /// <summary>
        /// Records an accepted restart, or refuses it without recording it: refused requests never eat into
        /// the budget. With <see cref="RuntimeFlowOptions.RestartWindow"/> zero or less the window never
        /// slides and the limit counts every restart over the host's lifetime.
        /// </summary>
        private Exception? ChargeBudget(string reason, out RestartRequest? accepted)
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

            var limit = _options.MaxRestartsPerWindow;
            if (limit <= 0 || _restarts.Count + 1 <= limit)
            {
                accepted = new RestartRequest(now, reason);
                _restarts.Add(accepted);
                return null;
            }

            accepted = null;
            var reasons = new string[_restarts.Count + 1];
            for (var i = 0; i < _restarts.Count; i++) reasons[i] = _restarts[i].Reason;
            reasons[_restarts.Count] = reason;

            var span = window > TimeSpan.Zero ? $"within {Fmt.N(window.TotalSeconds)}s" : "over the host's lifetime";
            return new RuntimeFlowException("session",
                $"Restart budget exceeded: {reasons.Length.ToString(CultureInfo.InvariantCulture)} restarts {span} " +
                $"(limit {limit.ToString(CultureInfo.InvariantCulture)}). Reasons: {string.Join(", ", reasons)}");
        }

        /// <summary>
        /// Runs of every ancestor of <paramref name="scope"/> that the host knows, root first: global, the
        /// session when the scope descends from it, and every child run in between.
        /// </summary>
        private IReadOnlyList<ScopeRun> ParentsOf(IScopedObjectResolver scope, string name, out bool underSession)
        {
            underSession = false;
            if (ReferenceEquals(scope, _session) || ReferenceEquals(scope, _global))
            {
                throw new InitGraphException(name,
                    $"Scope '{name}' is the host's own {(ReferenceEquals(scope, _session) ? "session" : "global")} scope, " +
                    "which the host initializes itself; pass a scope created below it, for example host.Session.CreateScope(...).");
            }
            if (_childScopes.TryGetValue(scope, out var identity))
            {
                throw new InitGraphException(name, $"Scope '{name}' has already been initialized as '{identity.Name}'; a scope's graph runs once.");
            }

            var chain = new List<ScopeRun>();
            var current = scope.Parent;
            var reachesGlobal = false;
            while (current != null)
            {
                if (_retiredSessions.TryGetValue(current, out _))
                {
                    throw new InitGraphException(name,
                        $"Scope '{name}' descends from a session scope that a restart has replaced; create it below the current host.Session.");
                }

                if (_childScopes.TryGetValue(current, out var ancestor))
                {
                    if (ancestor.Retired)
                    {
                        throw new InitGraphException(name,
                            $"Scope '{name}' descends from child scope '{ancestor.Name}' that is disposed or disposing; create it below a live parent scope.");
                    }
                    if (ancestor.Constructing)
                    {
                        throw new InvalidOperationException(
                            $"Scope '{name}' cannot be initialized: its parent scope '{ancestor.Name}' is still being constructed. " +
                            "Initialize descendants after the parent's InitializeScopeAsync has completed.");
                    }
                }

                if (ReferenceEquals(current, _session) && _sessionRun != null)
                {
                    chain.Add(_sessionRun);
                    underSession = true;
                }
                else if (ReferenceEquals(current, _global))
                {
                    reachesGlobal = true;
                    break;
                }
                else
                {
                    foreach (var child in _childRuns)
                    {
                        if (!ReferenceEquals(child.Scope, current)) continue;
                        chain.Add(child.Run);
                        underSession |= child.UnderSession;
                        break;
                    }
                }
                current = current.Parent;
            }

            if (!reachesGlobal && !ReferenceEquals(scope.Root, _global))
            {
                throw new InitGraphException(name,
                    $"Scope '{name}' does not belong to this host: its parent chain reaches neither the session nor the " +
                    "global scope. Create it with host.Session.CreateScope(...) or host.Global.CreateScope(...).");
            }

            chain.Add(_globalRun!);
            chain.Reverse();
            return chain;
        }

        private async Task DisposeChildRunsAsync(bool sessionOnly)
        {
            var snapshot = _childRuns.ToArray();
            for (var i = snapshot.Length - 1; i >= 0; i--)
            {
                if (sessionOnly && !snapshot[i].UnderSession) continue;
                await DisposeRunQuietlyAsync(snapshot[i].Run);
                _childRuns.Remove(snapshot[i]);
            }
        }

        private async Task DisposeRunQuietlyAsync(ScopeRun run)
        {
            try
            {
                await run.DisposeAsync();
            }
            catch (Exception exception)
            {
                _logger.Error($"[RuntimeFlow] disposing the '{run.Name}' run threw {exception.GetType().Name}; continuing teardown.", exception);
            }
        }

        private void DisposeQuietly(IDisposable scope, string name) => ScopeDisposal.Dispose(scope, _logger, name);

        private void Retire(IScopedObjectResolver? session)
        {
            if (session == null) return;
            _retiredSessions.Remove(session);
            _retiredSessions.Add(session, Retired);
        }

        private async Task DisposeGlobalAsync()
        {
            if (_globalRun != null)
            {
                await DisposeRunQuietlyAsync(_globalRun);
                _globalRun = null;
            }
            else if (_ownsGlobal && _global != null)
            {
                DisposeQuietly(_global, "global");
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
        /// Best-effort name of the entry point that threw: the outermost frame of the exception's stack that
        /// belongs to a VContainer entry-point implementation (so a helper deeper down, a FileStream say,
        /// is not blamed), then the declaring type of the throwing method. Stack information can be missing
        /// (stripped builds), hence the generic fallback.
        /// </summary>
        private static string EntryPointName(Exception error)
        {
            try
            {
                var trace = new StackTrace(error, false);
                for (var i = 0; i < trace.FrameCount; i++)
                {
                    var type = trace.GetFrame(i)?.GetMethod()?.DeclaringType;
                    if (type == null) continue;
                    if (IsEntryPointType(type)) return type.Name;
                }
            }
            catch (Exception)
            {
                // Stack inspection is diagnostics only.
            }

            return error.TargetSite?.DeclaringType?.Name ?? "an entry point";
        }

        private static bool IsEntryPointType(Type type)
            => typeof(IInitializable).IsAssignableFrom(type)
               || typeof(IPostInitializable).IsAssignableFrom(type)
               || typeof(IStartable).IsAssignableFrom(type)
               || typeof(IPostStartable).IsAssignableFrom(type)
               || typeof(ITickable).IsAssignableFrom(type)
               || typeof(IPostTickable).IsAssignableFrom(type)
               || typeof(IFixedTickable).IsAssignableFrom(type)
               || typeof(IPostFixedTickable).IsAssignableFrom(type)
               || typeof(ILateTickable).IsAssignableFrom(type)
               || typeof(IPostLateTickable).IsAssignableFrom(type);

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

        private sealed class RestartRequest
        {
            public RestartRequest(TimeSpan at, string reason)
            {
                At = at;
                Reason = reason;
            }

            public TimeSpan At { get; }
            public string Reason { get; }
        }

        /// <summary>A run created by <see cref="InitializeScopeAsync"/>, with the scope it runs and its lineage.</summary>
        private sealed class ChildRun
        {
            public ChildRun(ScopeRun run, IScopedObjectResolver scope, bool underSession)
            {
                Run = run;
                Scope = scope;
                UnderSession = underSession;
            }

            public ScopeRun Run { get; }
            public IScopedObjectResolver Scope { get; }

            /// <summary>True when the scope descends from the session, so a restart disposes it.</summary>
            public bool UnderSession { get; }
        }

        private sealed class ChildScopeIdentity
        {
            public ChildScopeIdentity(string name) => Name = name;
            public string Name { get; }
            public bool Retired { get; set; }
            public bool Constructing { get; set; } = true;
        }

        /// <summary>Takes the place of the host's collector in a scope whose installer registered its own handler; never resolved.</summary>
        private sealed class SupersededEntryPointCollector
        {
        }

        /// <summary>
        /// The host's entry-point exception handler of one composed scope. <see cref="Consumer"/> is the
        /// consumer handler of a parent scope, which stays in charge of this scope too.
        /// </summary>
        private sealed class EntryPointCollector
        {
            private readonly RuntimeFlowHost _host;

            public EntryPointCollector(RuntimeFlowHost host, Action<Exception>? consumer)
            {
                _host = host;
                Consumer = consumer;
            }

            public Action<Exception>? Consumer { get; }

            public void Handle(Exception error) => _host.OnEntryPointError(this, error);
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
                else Detach();
            }
        }
    }
}
