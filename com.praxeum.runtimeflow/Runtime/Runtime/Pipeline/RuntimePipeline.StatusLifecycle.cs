using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RuntimeFlow.Contexts
{
    public sealed partial class RuntimePipeline
    {
        public ValueTask DisposeAsync() => DisposeAsyncCore(CancellationToken.None);
        internal async ValueTask DisposeAsync(CancellationToken cancellationToken) => await DisposeAsyncCore(cancellationToken).ConfigureAwait(false);
        private async ValueTask DisposeAsyncCore(CancellationToken cancellationToken)
        {
            if (_disposed) return;
            _disposed = true;
            if (ActivePipeline == this)
                ActivePipeline = null;
            try
            {
                await _builder.DisposeAllScopesAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipeline disposal encountered scope teardown failures; continuing teardown.");
            }
            _logger.LogDebug("Pipeline disposed");
        }
        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(RuntimePipeline)); }
    }
}
