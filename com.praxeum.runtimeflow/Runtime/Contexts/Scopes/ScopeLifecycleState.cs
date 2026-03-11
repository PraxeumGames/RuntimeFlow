namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Represents the lifecycle state of a declared scope.
    /// </summary>
    public enum ScopeLifecycleState
    {
        /// <summary>Scope is declared but has never been initialized.</summary>
        NotLoaded = 0,

        /// <summary>Scope is currently being initialized (async init in progress).</summary>
        Loading = 1,

        /// <summary>Scope is fully initialized and activated, ready for use.</summary>
        Active = 2,

        /// <summary>Scope is being deactivated (OnScopeDeactivatingAsync in progress).</summary>
        Deactivating = 3,

        /// <summary>Scope is being reloaded (dispose old → init new cycle).</summary>
        Reloading = 4,

        /// <summary>Scope has been explicitly disposed/cleaned up.</summary>
        Disposed = 5,

        /// <summary>Scope initialization or activation failed.</summary>
        Failed = 6,

        /// <summary>Scope is initialized but activation was skipped (preloaded).</summary>
        Preloaded = 7
    }
}
