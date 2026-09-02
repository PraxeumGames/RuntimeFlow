using System;
using System.Collections.Generic;

namespace RuntimeFlow
{
    /// <summary>
    /// Per-service view of the run handed to <see cref="IAsyncInitializable.InitializeAsync"/>:
    /// run metadata, the degraded-service list, and the halt and progress hooks.
    /// </summary>
    public sealed class InitContext
    {
        private readonly Action<string> _halt;
        private readonly Action<float> _reportProgress;
        private bool _abandoned;

        internal InitContext(
            string scope,
            bool isRestart,
            int generation,
            IReadOnlyCollection<string> degradedServices,
            Action<string> halt,
            Action<float> reportProgress)
        {
            Scope = scope;
            IsRestart = isRestart;
            Generation = generation;
            DegradedServices = degradedServices;
            _halt = halt;
            _reportProgress = reportProgress;
        }

        /// <summary>Name of the scope being initialized, for example "global", "session" or a child-scope name.</summary>
        public string Scope { get; }

        /// <summary>True when this run replaces an earlier one (a restart) rather than being the first startup.</summary>
        public bool IsRestart { get; }

        /// <summary>Zero for the first run of a scope, incremented by one per restart.</summary>
        public int Generation { get; }

        /// <summary>Names of optional services that have failed so far in this run; a live view.</summary>
        public IReadOnlyCollection<string> DegradedServices { get; }

        /// <summary>
        /// Stops the run gracefully without an exception and without a restart; the first call wins.
        /// </summary>
        /// <param name="reason">Short machine-readable reason, surfaced as <see cref="StartupResult.HaltReason"/>.</param>
        public void Halt(string reason)
        {
            ThrowIfAbandoned();
            _halt(reason ?? string.Empty);
        }

        /// <summary>
        /// Reports sub-progress of this service in the 0..1 range (clamped) and refreshes the stall timer.
        /// </summary>
        public void ReportProgress(float fraction)
        {
            ThrowIfAbandoned();
            _reportProgress(fraction);
        }

        /// <summary>Marks the context as belonging to an abandoned generation; further calls throw.</summary>
        internal void Abandon() => _abandoned = true;

        private void ThrowIfAbandoned()
        {
            if (_abandoned)
                throw new ObjectDisposedException(nameof(InitContext), $"The '{Scope}' run of generation {Generation} was abandoned.");
        }
    }
}
