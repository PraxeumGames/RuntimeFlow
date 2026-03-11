namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Passed to services during initialization, allowing them to report sub-progress.
    /// </summary>
    public interface IServiceInitializationContext
    {
        /// <summary>
        /// Reports initialization progress for the current service.
        /// </summary>
        /// <param name="progress">Progress from 0.0 to 1.0</param>
        /// <param name="message">Optional status message (e.g., "Downloading assets 45/100...")</param>
        void ReportProgress(float progress, string? message = null);
    }
}
