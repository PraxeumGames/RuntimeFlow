using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VContainer;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Holds references to pipeline infrastructure for lifecycle management.
    /// Dispose order: Cancel CTS → Dispose Pipeline → Dispose Container → Dispose CTS.
    /// </summary>
    public sealed class BootstrapResult : IDisposable, IAsyncDisposable
    {
        private readonly ILogger _logger;

        public RuntimePipeline Pipeline { get; }
        public IObjectResolver RootContainer { get; }
        public CancellationTokenSource CancellationTokenSource { get; }

        /// <summary>True if bootstrap completed successfully (not cancelled or failed).</summary>
        public bool IsSuccess { get; private set; }

        /// <summary>True after bootstrap has finished (regardless of outcome).</summary>
        public bool IsCompleted { get; private set; }

        /// <summary>
        /// The session-scope game context. Available after successful bootstrap.
        /// Use Resolve&lt;T&gt;() to access session-scoped services in tests.
        /// </summary>
        public IGameContext SessionContext { get; set; }

        private bool _disposed;

        public BootstrapResult(RuntimePipeline pipeline, IObjectResolver rootContainer, CancellationTokenSource cts, ILogger logger = null)
        {
            Pipeline = pipeline;
            RootContainer = rootContainer;
            CancellationTokenSource = cts;
            _logger = logger;
        }

        public void MarkCompleted(bool success)
        {
            IsCompleted = true;
            IsSuccess = success;
        }

        /// <summary>
        /// Async disposal — preferred in tests. Properly awaits pipeline teardown.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            try { CancellationTokenSource?.Cancel(); } catch (ObjectDisposedException) { }

            if (Pipeline != null)
            {
                try
                {
                    await Pipeline.DisposeAsync(CancellationTokenSource?.Token ?? CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error disposing pipeline.");
                }
            }

            if (RootContainer is IDisposable disposable)
                disposable.Dispose();

            CancellationTokenSource?.Dispose();
        }

        /// <summary>
        /// Synchronous disposal — used by GameEntryPoint.OnDestroy (cannot be async).
        /// Pipeline disposal is best-effort synchronous wait.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { CancellationTokenSource?.Cancel(); } catch (ObjectDisposedException) { }

            if (Pipeline != null)
            {
                try
                {
                    Pipeline.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error disposing pipeline.");
                }
            }

            if (RootContainer is IDisposable disposable)
                disposable.Dispose();

            CancellationTokenSource?.Dispose();
        }
    }
}
