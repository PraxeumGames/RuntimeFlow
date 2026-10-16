namespace RuntimeFlow
{
    /// <summary>
    /// A restart request <see cref="RuntimeFlowHost.RestartAsync"/> took on, as reported to
    /// <see cref="IRuntimeFlowObserver.OnRestartRequested"/>.
    /// </summary>
    public sealed class RestartRequestInfo
    {
        internal RestartRequestInfo(string reason, bool coalesced, int generation)
        {
            Reason = reason;
            Coalesced = coalesced;
            Generation = generation;
        }

        /// <summary>The reason passed to <see cref="RuntimeFlowHost.RestartAsync"/>.</summary>
        public string Reason { get; }

        /// <summary>
        /// True when the request joined a restart already in progress (see
        /// <see cref="RuntimeFlowOptions.RestartCoalescing"/>): it starts nothing and is not budgeted.
        /// </summary>
        public bool Coalesced { get; }

        /// <summary>The host's <see cref="RuntimeFlowHost.Generation"/> when the request arrived.</summary>
        public int Generation { get; }
    }
}
