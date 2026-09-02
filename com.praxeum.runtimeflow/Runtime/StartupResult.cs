using System;
using System.Collections.Generic;

namespace RuntimeFlow
{
    /// <summary>How a run ended when it did not throw.</summary>
    public enum StartupOutcome
    {
        /// <summary>Every required service finished; optional services may have degraded.</summary>
        Completed,

        /// <summary>A service called <see cref="InitContext.Halt"/>; startup stopped on purpose.</summary>
        Halted
    }

    /// <summary>Result of a completed or halted run of one scope.</summary>
    public sealed class StartupResult
    {
        internal StartupResult(
            StartupOutcome outcome,
            string scope,
            TimeSpan elapsed,
            IReadOnlyList<string> degraded,
            string? haltReason = null,
            string? haltedBy = null)
        {
            Outcome = outcome;
            Scope = scope;
            Elapsed = elapsed;
            Degraded = degraded;
            HaltReason = haltReason;
            HaltedBy = haltedBy;
        }

        /// <summary>Whether the run completed or was halted.</summary>
        public StartupOutcome Outcome { get; }

        /// <summary>Name of the scope this result describes.</summary>
        public string Scope { get; }

        /// <summary>Wall-clock duration of the run.</summary>
        public TimeSpan Elapsed { get; }

        /// <summary>Names of optional services that failed; the run continued without them.</summary>
        public IReadOnlyList<string> Degraded { get; }

        /// <summary>Reason passed to <see cref="InitContext.Halt"/>, or null when the run completed.</summary>
        public string? HaltReason { get; }

        /// <summary>Name of the service that halted the run, or null when the run completed.</summary>
        public string? HaltedBy { get; }

        /// <inheritdoc />
        public override string ToString() => Outcome == StartupOutcome.Halted
            ? $"{Scope}: halted by {HaltedBy} — '{HaltReason}'"
            : $"{Scope}: completed in {Elapsed.TotalSeconds:0.0}s";
    }
}
