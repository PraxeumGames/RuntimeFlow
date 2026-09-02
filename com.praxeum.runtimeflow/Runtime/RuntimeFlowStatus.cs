using System;
using System.Collections.Generic;

namespace RuntimeFlow
{
    /// <summary>Lifecycle state of a single service inside a run.</summary>
    public enum ServiceState
    {
        /// <summary>Waiting for its dependencies; not started yet.</summary>
        Pending,

        /// <summary><see cref="IAsyncInitializable.InitializeAsync"/> is in flight.</summary>
        Running,

        /// <summary>Finished successfully.</summary>
        Completed,

        /// <summary>Optional service that failed; the run continued without it.</summary>
        Degraded,

        /// <summary>Required service that failed and brought the run down.</summary>
        Failed,

        /// <summary>Was running when the run was cancelled, halted or failed.</summary>
        Cancelled,

        /// <summary>Never started because the run stopped first.</summary>
        Skipped
    }

    /// <summary>State of a whole scope run.</summary>
    public enum RunState
    {
        /// <summary>Created but never run.</summary>
        NotStarted,

        /// <summary>A run is in flight.</summary>
        Running,

        /// <summary>Every required service finished.</summary>
        Completed,

        /// <summary>A service called <see cref="InitContext.Halt"/>.</summary>
        Halted,

        /// <summary>A required service failed.</summary>
        Failed,

        /// <summary>The caller's token was cancelled.</summary>
        Cancelled,

        /// <summary>The run was disposed.</summary>
        Disposed
    }

    /// <summary>Immutable snapshot of one service, produced by <see cref="ScopeRun.GetStatus"/>.</summary>
    public sealed class ServiceStatus
    {
        internal ServiceStatus(
            string name,
            string scope,
            string? phase,
            ServiceState state,
            bool optional,
            bool userGated,
            bool awaitingPlayer,
            TimeSpan elapsed,
            float progress,
            double weight,
            IReadOnlyList<string> dependencies,
            IReadOnlyList<string> waitingOn,
            Exception? error)
        {
            Name = name;
            Scope = scope;
            Phase = phase;
            State = state;
            Optional = optional;
            UserGated = userGated;
            AwaitingPlayer = awaitingPlayer;
            Elapsed = elapsed;
            Progress = progress;
            Weight = weight;
            Dependencies = dependencies;
            WaitingOn = waitingOn;
            Error = error;
        }

        /// <summary>Service name; the type's simple name, or its full name when two types collide.</summary>
        public string Name { get; }

        /// <summary>Name of the scope the service belongs to.</summary>
        public string Scope { get; }

        /// <summary>Declared phase, or null when the run has no phases.</summary>
        public string? Phase { get; }

        /// <summary>Current lifecycle state.</summary>
        public ServiceState State { get; }

        /// <summary>True when a failure degrades rather than fails the run.</summary>
        public bool Optional { get; }

        /// <summary>True when the service waits for the player and never times out.</summary>
        public bool UserGated { get; }

        /// <summary>True while a user-gated service is running.</summary>
        public bool AwaitingPlayer { get; }

        /// <summary>Time spent running, still ticking while <see cref="ServiceState.Running"/>.</summary>
        public TimeSpan Elapsed { get; }

        /// <summary>Last value reported through <see cref="InitContext.ReportProgress"/>, 0..1.</summary>
        public float Progress { get; }

        /// <summary>Progress weight from <see cref="InitAttribute.Weight"/>.</summary>
        public double Weight { get; }

        /// <summary>Names of every service this one waits for.</summary>
        public IReadOnlyList<string> Dependencies { get; }

        /// <summary>Dependencies that have not finished yet; empty once the service can start.</summary>
        public IReadOnlyList<string> WaitingOn { get; }

        /// <summary>Failure of this service, or null.</summary>
        public Exception? Error { get; }

        /// <inheritdoc />
        public override string ToString() => $"{Name} ({State})";
    }

    /// <summary>Immutable snapshot of a scope run, safe to poll from UI code.</summary>
    public sealed class RuntimeFlowStatus
    {
        internal RuntimeFlowStatus(
            RunState state,
            string scope,
            string? phase,
            IReadOnlyList<ServiceStatus> services,
            IReadOnlyList<ServiceStatus> running,
            int completedCount,
            int totalCount,
            double percent,
            TimeSpan elapsed,
            int restartCount,
            string? haltReason,
            Exception? error)
        {
            State = state;
            Scope = scope;
            Phase = phase;
            Services = services;
            Running = running;
            CompletedCount = completedCount;
            TotalCount = totalCount;
            Percent = percent;
            Elapsed = elapsed;
            RestartCount = restartCount;
            HaltReason = haltReason;
            Error = error;
        }

        /// <summary>State of the run.</summary>
        public RunState State { get; }

        /// <summary>Name of the scope.</summary>
        public string Scope { get; }

        /// <summary>Phase currently executing, or null when the run has no phases.</summary>
        public string? Phase { get; }

        /// <summary>Every service of the scope, in graph order.</summary>
        public IReadOnlyList<ServiceStatus> Services { get; }

        /// <summary>The subset of <see cref="Services"/> that is currently running.</summary>
        public IReadOnlyList<ServiceStatus> Running { get; }

        /// <summary>Number of services that finished, counting degraded ones.</summary>
        public int CompletedCount { get; }

        /// <summary>Number of services in the scope.</summary>
        public int TotalCount { get; }

        /// <summary>Weighted completion percentage, 0..100, including reported sub-progress.</summary>
        public double Percent { get; }

        /// <summary>Time since the run started.</summary>
        public TimeSpan Elapsed { get; }

        /// <summary>Number of restarts this scope has been through.</summary>
        public int RestartCount { get; }

        /// <summary>Reason of the halt, or null.</summary>
        public string? HaltReason { get; }

        /// <summary>Failure of the run, or null.</summary>
        public Exception? Error { get; }

        /// <inheritdoc />
        public override string ToString()
            => $"{Scope}: {State} {CompletedCount}/{TotalCount} ({Percent:0.0}%)";
    }
}
