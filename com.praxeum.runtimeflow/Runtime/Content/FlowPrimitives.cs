using System;
using System.Collections.Concurrent;
using System.Threading;

namespace RuntimeFlow.Content
{
    /// <summary>
    /// Registration-time registry of flow-declared content edges (from <c>after:</c> tokens).
    /// Keyed by the source's primary registration type; consulted by startup discovery so
    /// delegate sources produce real DAG edges without attributes or constructor wiring.
    /// </summary>
    internal static class ContentEdgeRegistry
    {
        private readonly static ConcurrentDictionary<Type, Type[]> _edges = new();

        public static void Set(Type sourcePrimaryType, Type[] edgeTypes)
        {
            if (sourcePrimaryType == null) throw new ArgumentNullException(nameof(sourcePrimaryType));
            _edges[sourcePrimaryType] = edgeTypes ?? Array.Empty<Type>();
        }

        public static bool TryGet(Type sourcePrimaryType, out Type[] edgeTypes)
            => _edges.TryGetValue(sourcePrimaryType, out edgeTypes!);
    }

    /// <summary>
    /// Handle to a registered content source: returned by GameFlow vocabulary steps and
    /// accepted by <c>after:</c> parameters to declare explicit ordering edges.
    /// </summary>
    public sealed class SourceToken<TData> where TData : class
    {
        internal Type EdgeType { get; }

        internal string SourceName { get; }

        internal SourceToken(Type edgeType, string sourceName)
        {
            EdgeType = edgeType;
            SourceName = sourceName;
        }
    }

    /// <summary>
    /// Resolution surface handed to delegate content loaders:
    /// <c>load: async (flow, ct) => flow.Get&lt;IContentSource&lt;AuthSnapshot&gt;&gt;().Data...</c>
    /// </summary>
    public sealed class FlowLoadContext
    {
        private readonly Func<Type, object?> _resolve;

        internal FlowLoadContext(Func<Type, object?> resolve, CancellationToken cancellationToken)
        {
            _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
            CancellationToken = cancellationToken;
        }

        public CancellationToken CancellationToken { get; }

        public T Get<T>() where T : class => (T)_resolve(typeof(T))!;
    }
}
