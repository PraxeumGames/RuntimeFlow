using System;

namespace RuntimeFlow
{
    /// <summary>
    /// Declares scheduling metadata for an <see cref="IAsyncInitializable"/> implementation:
    /// phase, failure policy, user gating, timeout and progress weight.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
    public sealed class InitAttribute : Attribute
    {
        /// <summary>Name of the phase this service belongs to; must appear in <see cref="RuntimeFlowOptions.Phases"/>.</summary>
        public string? Phase;

        /// <summary>When true a failure degrades the run instead of failing it; dependents still run.</summary>
        public bool Optional;

        /// <summary>When true the service waits for the player and is never subject to a timeout.</summary>
        public bool UserGated;

        /// <summary>Timeout in seconds, scaled by <see cref="RuntimeFlowOptions.TimeoutMultiplier"/>; 0 means none.</summary>
        public double TimeoutSeconds;

        /// <summary>Relative weight of this service in the weighted progress percentage.</summary>
        public double Weight = 1.0;
    }
}
