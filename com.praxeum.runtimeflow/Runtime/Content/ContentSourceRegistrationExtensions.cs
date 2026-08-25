using System;
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
    }
}

