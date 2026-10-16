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
        private readonly Func<IReadOnlyCollection<string>> _degradedServices;
        private readonly Action<string> _halt;
        private readonly Action<float> _reportProgress;
        private volatile bool _abandoned;

        internal InitContext(
            string scope,
            bool isRestart,
            int generation,
            Func<IReadOnlyCollection<string>> degradedServices,
            Action<string> halt,
            Action<float> reportProgress)
        {
            Scope = scope;
            IsRestart = isRestart;
            Generation = generation;
            _degradedServices = degradedServices;
            _halt = halt;
            _reportProgress = reportProgress;
        }

        /// <summary>Name of the scope being initialized, for example "global", "session" or a child-scope name.</summary>
        public string Scope { get; }

        /// <summary>True when this run replaces an earlier one (a restart) rather than being the first startup.</summary>
        public bool IsRestart { get; }

        /// <summary>Zero for the first run of a scope, incremented by one per restart.</summary>
        public int Generation { get; }

        /// <summary>
        /// Names of optional services that have degraded so far: the ones that failed in this run plus the
        /// ones a parent scope (global, typically) already reported. Every read returns an immutable
        /// snapshot — safe to enumerate across an <c>await</c> or from another thread while further services
        /// degrade — so read the property again to see later degradations.
        /// </summary>
        public IReadOnlyCollection<string> DegradedServices => _degradedServices();

        /// <summary>
        /// Stops the run gracefully without an exception and without a restart; the first call wins.
        /// </summary>
        /// <param name="reason">Short machine-readable reason, surfaced as <see cref="StartupResult.HaltReason"/>.</param>
        /// <exception cref="ArgumentException"><paramref name="reason"/> is null or empty.</exception>
        public void Halt(string reason)
        {
            ThrowIfAbandoned();
            if (string.IsNullOrEmpty(reason))
                throw new ArgumentException("Halt reason must be a non-empty machine-readable string.", nameof(reason));
            _halt(reason);
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
