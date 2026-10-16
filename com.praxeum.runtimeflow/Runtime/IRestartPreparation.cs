using System.Threading;
using System.Threading.Tasks;
using VContainer;

namespace RuntimeFlow
{
    /// <summary>
    /// Prepares the application for a restart before the session scope it replaces is torn down: show a
    /// loading cover, reset static service locators, tear down UI navigation. Register any number of
    /// implementations in the global and/or the session installer (for example
    /// <c>builder.Register&lt;LoadingCover&gt;(Lifetime.Singleton).As&lt;IRestartPreparation&gt;()</c>).
    /// </summary>
    /// <remarks>
    /// <para>The implementations are resolved when a session scope is built, right after its graph was
    /// created and before its run starts, so a failing constructor fails that build like any other
    /// construction error and teardown never constructs anything. Global ones run first, then the
    /// session's, each in registration order; an instance registered under several contracts runs once.
    /// Register them as singletons: a transient one is resolved anew for every session.</para>
    /// <para><see cref="RuntimeFlowHost"/> calls them once per accepted restart that tears down a built
    /// session — whether its run is still initializing, frozen before it started, completed, failed or
    /// halted — after the session's constructions were drained and its run (and the runs of its child
    /// scopes) were cancelled and awaited, but before anything of it is disposed. No
    /// <see cref="IAsyncInitializable.InitializeAsync"/> of the doomed generation is still running then
    /// (beyond <see cref="RuntimeFlowOptions.CancellationGrace"/>), while every service of the session can
    /// still be resolved. They are not called when there is no session to tear down (a restart requested
    /// during the global phase of the startup, or after a session failed to build) and never by
    /// <see cref="RuntimeFlowHost.DisposeAsync"/> or a quit.</para>
    /// <para>Hooks are awaited one after the other on the main thread. A hook that throws is logged and
    /// the restart continues; a hook still running after <see cref="RuntimeFlowOptions.RestartPreparationTimeout"/>
    /// has its token cancelled and is no longer awaited. The timeout counts active time only: time the
    /// application spends suspended (backgrounded) does not count.</para>
    /// <para>A hook must not await <see cref="RuntimeFlowHost.StartAsync"/>, <see cref="RuntimeFlowHost.RestartAsync"/>
    /// or <see cref="RuntimeFlowHost.DisposeAsync"/> of its host: each of them waits for the restart that is
    /// waiting for the hook, so the call deadlocks until <see cref="RuntimeFlowOptions.RestartPreparationTimeout"/>
    /// cuts the hook — forever with <see cref="Timeout.InfiniteTimeSpan"/>. Fire such a call and return.</para>
    /// </remarks>
    public interface IRestartPreparation
    {
        /// <summary>Called before the session described by <paramref name="context"/> is torn down.</summary>
        /// <param name="context">The restart and the session it tears down.</param>
        /// <param name="cancellationToken">
        /// Cancelled at <see cref="RuntimeFlowOptions.RestartPreparationTimeout"/> and when the host is
        /// disposed or the application quits.
        /// </param>
        Task PrepareForRestartAsync(RestartPreparationContext context, CancellationToken cancellationToken);
    }

    /// <summary>What an <see cref="IRestartPreparation"/> is told about the restart it prepares for.</summary>
    public sealed class RestartPreparationContext
    {
        internal RestartPreparationContext(string reason, int generation, IObjectResolver session, RunState sessionState)
        {
            Reason = reason;
            Generation = generation;
            Session = session;
            SessionState = sessionState;
        }

        /// <summary>Reason of the accepted restart request.</summary>
        public string Reason { get; }

        /// <summary>Generation of the session being torn down.</summary>
        public int Generation { get; }

        /// <summary>
        /// The session scope being torn down; still alive, so its services can be resolved. Do not keep it:
        /// it is disposed right after the hooks returned.
        /// </summary>
        public IObjectResolver Session { get; }

        /// <summary>
        /// State of the session's run when the restart was accepted: <see cref="RunState.Running"/> for a
        /// session still initializing, <see cref="RunState.NotStarted"/> for one built but frozen before its
        /// run started (the restart was requested while it was being built), otherwise how it concluded
        /// (<see cref="RunState.Completed"/>, <see cref="RunState.Failed"/>, <see cref="RunState.Halted"/>,
        /// <see cref="RunState.Cancelled"/>).
        /// </summary>
        public RunState SessionState { get; }

        /// <summary>True when the restart interrupted the session while it was still initializing.</summary>
        public bool WasInitializing => SessionState == RunState.Running;
    }
}
