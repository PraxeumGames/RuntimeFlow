namespace RuntimeFlow
{
    /// <summary>
    /// Optional hook into a run's lifecycle for UI, telemetry and tests.
    /// Every method has a default empty body, so implementers override only what they need.
    /// Exceptions thrown by an observer are caught and logged; they never affect the run.
    /// </summary>
    public interface IRuntimeFlowObserver
    {
        /// <summary>A run of <paramref name="scope"/> started; <paramref name="isRestart"/> is true for restarts.</summary>
        void OnRunStarted(string scope, bool isRestart) { }

        /// <summary>Services of <paramref name="phase"/> may now start.</summary>
        void OnPhaseStarted(string scope, string phase) { }

        /// <summary>Every service of <paramref name="phase"/> has finished.</summary>
        void OnPhaseCompleted(string scope, string phase) { }

        /// <summary>A service began initializing.</summary>
        void OnServiceStarted(ServiceStatus service) { }

        /// <summary>A user-gated service began waiting for the player.</summary>
        void OnServiceAwaitingPlayer(ServiceStatus service) { }

        /// <summary>A service finished successfully.</summary>
        void OnServiceCompleted(ServiceStatus service) { }

        /// <summary>A service failed; optional services report state <see cref="ServiceState.Degraded"/>.</summary>
        void OnServiceFailed(ServiceStatus service, System.Exception error) { }

        /// <summary>The run finished successfully.</summary>
        void OnRunCompleted(string scope, StartupResult result) { }

        /// <summary>The run was halted by a service.</summary>
        void OnRunHalted(string scope, StartupResult result) { }

        /// <summary>The run failed; <paramref name="error"/> carries every collected failure.</summary>
        void OnRunFailed(string scope, RuntimeFlowException error) { }

        /// <summary>
        /// The run was cancelled: by the caller's token, by <see cref="ScopeRun.CancelAsync"/> or teardown,
        /// or by the host replacing it (a restart, the application quitting).
        /// </summary>
        void OnRunCancelled(string scope) { }
    }
}
