using System;
using System.Threading;

namespace RuntimeFlow.Content
{
    /// <summary>
    /// Resolution surface handed to delegate content loaders:
    /// <c>load: async (flow, ct) => flow.Get&lt;IContentSource&lt;AuthSnapshot&gt;&gt;().Data...</c>
    /// </summary>
    public sealed class FlowLoadContext
    {
        private readonly Func<Type, object?> _resolve;
        private readonly string _sourceName;

        internal FlowLoadContext(Func<Type, object?> resolve, CancellationToken cancellationToken, string sourceName)
        {
            _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
            CancellationToken = cancellationToken;
            _sourceName = sourceName;
        }

        public CancellationToken CancellationToken { get; }

        /// <summary>
        /// Resolves a service from the owning context. Throws a descriptive
        /// <see cref="InvalidOperationException"/> if the service is not registered.
        /// </summary>
        public T Get<T>() where T : class
        {
            var resolved = _resolve(typeof(T));
            if (resolved == null)
                throw new InvalidOperationException(
                    $"Content source '{_sourceName}' cannot resolve '{typeof(T).Name}': " +
                    "the type is not registered in the current scope.");
            return (T)resolved;
        }
    }
}
