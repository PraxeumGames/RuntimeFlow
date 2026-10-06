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

        /// <summary>How long a run may make no progress before a warning is logged; <see cref="TimeSpan.Zero"/> disables it.</summary>
        public TimeSpan StallWarningAfter { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// How long teardown waits for cancelled services still in flight before abandoning them.
        /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> waits forever, zero does not wait, a negative
        /// value is rejected when the graph is built.
        /// </summary>
        public TimeSpan CancellationGrace { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Scales every <see cref="InitAttribute.TimeoutSeconds"/>; 0 disables timeouts entirely.</summary>
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

        /// <summary>Observers notified about run and service lifecycle events.</summary>
        public List<IRuntimeFlowObserver> Observers { get; } = new List<IRuntimeFlowObserver>();
    }
}
