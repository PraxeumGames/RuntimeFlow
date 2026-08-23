using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Content
{
    /// <summary>
    /// Registration helpers for content sources. One line registers the source, exposes
    /// <see cref="ContentSource{TData}"/> and <see cref="IContentSourceInfo"/> to consumers,
    /// and keeps the concrete type resolvable for <c>[DependsOn(typeof(TSource))]</c> edges.
    /// </summary>
    public static class ContentSourceRegistrationExtensions
    {
        /// <summary>
        /// Registers a content source in the enclosing scope. Consumers inject
        /// <see cref="ContentSource{TData}"/> (or <see cref="IContentSource{TData}"/>)
        /// and declare data-flow edges via constructor parameters of type
        /// <c>IContentSource&lt;TData&gt;</c> / <c>ContentSource&lt;TData&gt;</c>.
        /// </summary>
        public static IGameScopeRegistrationBuilder Content<TSource, TData>(
            this IGameScopeRegistrationBuilder builder,
            DiLifetime lifetime = DiLifetime.Singleton)
            where TSource : ContentSource<TData>
            where TData : class
        {
            return builder
                .Register<TSource>(lifetime)
                .As<ContentSource<TData>>()
                .As<IContentSource<TData>>()
                .As<IContentSourceInfo>();
        }

        internal static void RegisterDelegateContent<TData>(
            this GameContextBuilder builder,
            GameContextType scope,
            string sourceName,
            Func<FlowLoadContext, CancellationToken, Task<TData>> load,
            ContentPolicy<TData>? policy,
            System.Collections.Generic.IReadOnlyList<Type> afterEdgeTypes)
            where TData : class
        {
            var source = new DelegateContentSource<TData>(sourceName, load);
            if (policy != null)
                source.Policy(policy.IsOptional, policy.Fallback);

            var edgeTypes = new Type[afterEdgeTypes.Count];
            for (var i = 0; i < afterEdgeTypes.Count; i++)
                edgeTypes[i] = afterEdgeTypes[i];
            ContentEdgeRegistry.Set(
                typeof(DelegateContentSource<TData>), edgeTypes);

            builder.RegisterInstanceDeferredForDiscovery(
                scope,
                source,
                typeof(ContentSource<TData>),
                new Type[] { typeof(IContentSource<TData>), typeof(IContentSourceInfo) },
                onContextAvailable: context => source.AttachResolver(t => context.Resolve(t)));
        }
    }
}

