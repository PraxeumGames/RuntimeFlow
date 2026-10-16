using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace RuntimeFlow
{
    /// <summary>
    /// Knobs shared by every run: logging, phases, stall and timeout policy, restart budget and observers.
    /// </summary>
    public sealed class RuntimeFlowOptions
    {
        /// <summary>Sink for every diagnostic message; never null, defaults to the Unity console.</summary>
        public ILogger Logger { get; set; } = UnityConsoleLogger.Default;

        /// <summary>Ordered phase names; empty (the default) means no phase barriers.</summary>
        public IReadOnlyList<string> Phases { get; set; } = Array.Empty<string>();

        /// <summary>Phase used by services without <see cref="InitAttribute.Phase"/>; null means the last phase.</summary>
        public string? DefaultPhase { get; set; }

        /// <summary>
        /// How long a run may make no progress before a warning is logged; <see cref="TimeSpan.Zero"/> disables it.
        /// Time the application spends suspended (backgrounded, or unfocused without Run In Background) does not count.
        /// </summary>
        public TimeSpan StallWarningAfter { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// How long teardown waits for cancelled services still in flight before abandoning them.
        /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> waits forever, zero does not wait, a negative
        /// value is rejected when the graph is built.
        /// </summary>
        public TimeSpan CancellationGrace { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Scales every <see cref="InitAttribute.TimeoutSeconds"/>; 0 disables timeouts entirely. Timeouts count
        /// only the time the application is not suspended (see <see cref="InitAttribute.TimeoutSeconds"/>).
        /// </summary>
        public double TimeoutMultiplier { get; set; } = 1.0;

        /// <summary>
        /// Maximum number of accepted restarts inside <see cref="RestartWindow"/>; 0 or less means unlimited.
        /// A refused request is not counted.
        /// </summary>
        public int MaxRestartsPerWindow { get; set; } = 5;

        /// <summary>
        /// Sliding window the restart budget is measured over; zero or less never slides, so the limit
        /// then counts every restart over the host's lifetime.
        /// </summary>
        public TimeSpan RestartWindow { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// How long a restart in progress absorbs further restart requests instead of starting another one;
        /// defaults to <see cref="RuntimeFlow.RestartCoalescing.UntilSessionInitialized"/>.
        /// </summary>
        public RestartCoalescing RestartCoalescing { get; set; } = RestartCoalescing.UntilSessionInitialized;

        /// <summary>
        /// How long each <see cref="IRestartPreparation"/> may take before its token is cancelled and the
        /// restart continues without waiting for it any further (an error names the hook).
        /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> waits forever, zero or less does not wait.
        /// Measured in wall time.
        /// </summary>
        public TimeSpan RestartPreparationTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Observers notified about run and service lifecycle events.</summary>
        public List<IRuntimeFlowObserver> Observers { get; } = new List<IRuntimeFlowObserver>();
    }

    /// <summary>
    /// Until when a restart in progress absorbs further <see cref="RuntimeFlowHost.RestartAsync"/> requests.
    /// A coalesced request starts nothing, is not budgeted, is logged and reported to
    /// <see cref="IRuntimeFlowObserver.OnRestartRequested"/> with <see cref="RestartRequestInfo.Coalesced"/>
    /// set, and its task completes with the outcome of the restart it joined.
    /// </summary>
    public enum RestartCoalescing
    {
        /// <summary>
        /// Requests coalesce for as long as <see cref="RuntimeFlowHost.IsRestarting"/> is true: until the run
        /// of the session the restart builds concluded (completed, failed, halted or cancelled). A service of
        /// the restarted generation that asks for yet another restart while that generation initializes
        /// joins the restart in progress instead of opening a new generation, so restart loops cannot form.
        /// Once the new session concluded — failed included, the "Retry" case — requests are accepted again.
        /// </summary>
        UntilSessionInitialized,

        /// <summary>
        /// Requests coalesce only until the restart has built its session: a request from that session's
        /// own initialization opens a new generation (budgeted by
        /// <see cref="RuntimeFlowOptions.MaxRestartsPerWindow"/>).
        /// </summary>
        UntilSessionBuilt
    }
}
