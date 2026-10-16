using System;
using System.Collections.Generic;

namespace RuntimeFlow.Testing
{
    /// <summary>
    /// An <see cref="IRuntimeFlowObserver"/> that records every callback as a
    /// <c>"{event}:{scope}:{subject}"</c> string, so tests can assert on order and content.
    /// </summary>
    public sealed class CollectingObserver : IRuntimeFlowObserver
    {
        private readonly List<string> _events = new List<string>();

        /// <summary>Recorded events, in the order they were raised.</summary>
        public IReadOnlyList<string> Events => _events;

        /// <summary>True when the exact event string was recorded.</summary>
        public bool Contains(string entry) => _events.Contains(entry);

        /// <summary>Position of the exact event string, or -1.</summary>
        public int IndexOf(string entry) => _events.IndexOf(entry);

        /// <summary>Drops every recorded event.</summary>
        public void Clear() => _events.Clear();

        /// <inheritdoc />
        public void OnRunStarted(string scope, bool isRestart)
            => _events.Add($"run-started:{scope}:{(isRestart ? "restart" : "start")}");

        /// <inheritdoc />
        public void OnPhaseStarted(string scope, string phase) => _events.Add($"phase-started:{scope}:{phase}");

        /// <inheritdoc />
        public void OnPhaseCompleted(string scope, string phase) => _events.Add($"phase-completed:{scope}:{phase}");

        /// <inheritdoc />
        public void OnServiceStarted(ServiceStatus service) => _events.Add($"started:{service.Scope}:{service.Name}");

        /// <inheritdoc />
        public void OnServiceAwaitingPlayer(ServiceStatus service) => _events.Add($"awaiting:{service.Scope}:{service.Name}");

        /// <inheritdoc />
        public void OnServiceCompleted(ServiceStatus service) => _events.Add($"completed:{service.Scope}:{service.Name}");

        /// <inheritdoc />
        public void OnServiceFailed(ServiceStatus service, Exception error) => _events.Add($"failed:{service.Scope}:{service.Name}");

        /// <inheritdoc />
        public void OnRunCompleted(string scope, StartupResult result) => _events.Add($"run-completed:{scope}:{result.Outcome}");

        /// <inheritdoc />
        public void OnRunHalted(string scope, StartupResult result) => _events.Add($"run-halted:{scope}:{result.HaltedBy}");

        /// <inheritdoc />
        public void OnRunFailed(string scope, RuntimeFlowException error) => _events.Add($"run-failed:{scope}:{error.Service}");

        /// <inheritdoc />
        public void OnRunCancelled(string scope) => _events.Add($"run-cancelled:{scope}");
    }
}
