using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Content
{
    /// <summary>
    /// A content source defined by a delegate — no subclass required:
    /// <code>
    /// cfg.Config("remote-config",
    ///     load: async (flow, ct) => Parse(await http.Fetch(ct)),
    ///     policy: ContentPolicy&lt;RemoteConfigSnapshot&gt;.Required());
    /// </code>
    /// <paramref name="load"/> receives a <see cref="FlowLoadContext"/> for resolving other
    /// sources (data chaining) plus the cancellation token.
    ///
    /// Ordering: pass <c>after:</c> tokens from previously registered sources, or chain via
    /// class-based sources whose constructors inject <c>IContentSource&lt;TData&gt;</c>.
    /// </summary>
    public sealed class DelegateContentSource<TData> : ContentSource<TData>
        where TData : class
    {
        private readonly string _sourceName;
        private readonly Func<FlowLoadContext, CancellationToken, Task<TData>> _load;
        private Func<Type, object?>? _resolver;

        public DelegateContentSource(
            string sourceName,
            Func<FlowLoadContext, CancellationToken, Task<TData>> load)
        {
            _sourceName = string.IsNullOrWhiteSpace(sourceName)
                ? throw new ArgumentNullException(nameof(sourceName))
                : sourceName.Trim();
            _load = load ?? throw new ArgumentNullException(nameof(load));
        }

        public override string SourceName => _sourceName;

        /// <summary>Called by the registration harness when the owning context is available.</summary>
        internal void AttachResolver(Func<Type, object?> resolve) => _resolver = resolve ?? throw new ArgumentNullException(nameof(resolve));

        protected override async Task<TData> LoadAsync(CancellationToken cancellationToken)
        {
            if (_resolver == null)
                throw new InvalidOperationException(
                    $"Delegate content source '{SourceName}' was not attached to a context.");
            var flow = new FlowLoadContext(_resolver, cancellationToken);
            return await _load(flow, cancellationToken).ConfigureAwait(false);
        }
    }
}
