using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Content
{
    /// <summary>
    /// Consumer-facing abstraction over a content/config source. Dependents inject this
    /// (or the concrete <see cref="ContentSource{TData}"/>) and read <see cref="Data"/>
    /// after their dependency edge has initialized it.
    /// </summary>
    public interface IContentSource<TData> where TData : class
    {
        /// <summary>Loaded data, or the fallback snapshot when the source is optional and degraded.</summary>
        TData Data { get; }

        bool IsLoaded { get; }

        /// <summary>True when loading failed but the source was optional and served fallback data.</summary>
        bool UsedFallback { get; }
    }

    /// <summary>Metadata for dashboards and progress reporting.</summary>
    public interface IContentSourceInfo
    {
        string SourceName { get; }

        bool IsOptional { get; }
    }

    /// <summary>
    /// Universal base for content/config/platform sources: remote config fetches,
    /// Addressables/catalog initialization, game-service authentication. One small subclass
    /// plus one registration line replaces the usual operation/service/wiring triple.
    ///
    /// Scope and stage are chosen by which markers the concrete class combines:
    /// register it in Global scope as an <see cref="IAsyncInitializableService"/>, or combine
    /// with <c>IPlatformStartupInitializableService</c> / <c>IContentStartupInitializableService</c>
    /// for session-stage placement. Combine with
    /// <c>IUserInteractionGatedInitializableService</c> when loading blocks on a user dialog
    /// (sign-in consent) so the health watchdog exempts it.
    ///
    /// Failure policy: required sources fail startup; set <see cref="IsOptional"/> and provide
    /// <see cref="FallbackData"/> to degrade gracefully instead — the pipeline stays green and
    /// dependents read the fallback snapshot (<see cref="UsedFallback"/> marks it).
    /// </summary>
    public abstract class ContentSource<TData> : IAsyncInitializableService, IContentSource<TData>, IContentSourceInfo
        where TData : class
    {
        /// <summary>Loaded data, or the fallback snapshot when degraded.</summary>
        public TData Data { get; private set; } = null!;

        public bool IsLoaded { get; private set; }

        public bool UsedFallback { get; private set; }

        public abstract string SourceName { get; }

        /// <summary>When true, load failures degrade to <see cref="FallbackData"/> instead of failing startup.</summary>
        public virtual bool IsOptional => false;

        /// <summary>Served when an optional source fails. Must return non-null when <see cref="IsOptional"/> is true.</summary>
        protected virtual TData? FallbackData => null;

        /// <summary>Override to log through the host's logger; defaults to silent.</summary>
        protected virtual ILogger Logger => NullLogger.Instance;

        /// <summary>Fetches/builds the snapshot. Called once per initialization.</summary>
        protected abstract Task<TData> LoadAsync(CancellationToken cancellationToken);

        /// <summary>Failure hook invoked before degradation is applied (metrics, breadcrumbs).</summary>
        protected virtual Task OnLoadFailedAsync(Exception exception, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            try
            {
                Data = await LoadAsync(cancellationToken).ConfigureAwait(false);
                IsLoaded = true;
                Logger.LogInformation("Content source '{SourceName}' loaded.", SourceName);
            }
            catch (Exception ex) when (IsOptional && !cancellationToken.IsCancellationRequested)
            {
                var fallback = FallbackData ?? throw new InvalidOperationException(
                    $"Optional content source '{SourceName}' must provide FallbackData.", ex);

                await OnLoadFailedAsync(ex, cancellationToken).ConfigureAwait(false);
                Logger.LogWarning(ex, "Content source '{SourceName}' failed; degraded to fallback data.", SourceName);
                Data = fallback;
                UsedFallback = true;
                IsLoaded = true;
            }
        }
    }
}
