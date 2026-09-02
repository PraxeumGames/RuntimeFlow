namespace RuntimeFlow.Demo
{
    /// <summary>
    /// The five ways the demo can break, one flag per RuntimeFlow failure semantic. A single instance is
    /// registered in both scopes, so the bootstrapper's checkboxes and the tests drive the same switches.
    /// </summary>
    public sealed class ChaosToggles
    {
        /// <summary>
        /// Latency <see cref="CatalogService"/> asks the backend for when <see cref="TimeoutInCatalog"/>
        /// is on: far past the service's own two-second deadline. It is a per-call value, so nothing
        /// about the backend stays slow once the toggle goes off again.
        /// </summary>
        public const int SlowCatalogMilliseconds = 10000;

        /// <summary>Makes <see cref="PlayerProfileService"/> throw: a required service failing mid-run.</summary>
        public bool ThrowInProfile { get; set; }

        /// <summary>Makes <see cref="QuestWarmupService"/> await the token forever: a stall warning, then a deadline.</summary>
        public bool HangInQuestWarmup { get; set; }

        /// <summary>
        /// Makes <see cref="CatalogService"/> ask <c>/catalog</c> for a
        /// <see cref="SlowCatalogMilliseconds"/> answer, so it hits its own timeout.
        /// </summary>
        public bool TimeoutInCatalog { get; set; }

        /// <summary>Makes <see cref="MaintenanceGateService"/> halt the run without an exception.</summary>
        public bool MaintenanceHalt { get; set; }

        /// <summary>Skips the user gate: <see cref="GdprConsentService"/> completes without waiting for the player.</summary>
        public bool GdprAlreadyAccepted { get; set; }
    }
}
