namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Marker interface for services whose InitializeAsync should be deferred until first resolution.
    /// Services implementing both IAsyncInitializableService and ILazyInitializableService
    /// are excluded from the eager initialization graph and initialized on-demand.
    /// </summary>
    public interface ILazyInitializableService { }
}
