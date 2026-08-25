using System;

namespace RuntimeFlow.Content
{
    /// <summary>
    /// Failure policy for a content source. <see cref="Required"/> fails startup when loading
    /// fails; <see cref="Optional"/> degrades to the provided fallback snapshot and marks
    /// <see cref="IContentSource{TData}.UsedFallback"/>.
    /// </summary>
    public sealed class ContentPolicy<TData> where TData : class
    {
        private ContentPolicy(TData? fallback)
        {
            Fallback = fallback;
        }

        /// <summary>Non-null when the policy is optional; served when loading fails.</summary>
        public TData? Fallback { get; }

        public bool IsOptional => Fallback != null;

        /// <summary>The source must load successfully or startup fails.</summary>
        public static ContentPolicy<TData> Required() => new(null);

        /// <summary>The source degrades to <paramref name="fallback"/> when loading fails.</summary>
        public static ContentPolicy<TData> Optional(TData fallback) => new(fallback ?? throw new ArgumentNullException(nameof(fallback)));

    }
}
