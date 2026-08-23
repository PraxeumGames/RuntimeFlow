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
    /// Addressables/catalog initialization, game-service authentication.
    ///
    /// Scope and stage are chosen by which markers the concrete class combines: register it in
    /// Global scope as an <see cref="IAsyncInitializableService"/>, or combine with
    /// <c>IPlatformStartupInitializableService</c> / <c>IContentStartupInitializableService</c>
    /// for session-stage placement. Combine with <c>IUserInteractionGatedInitializableService</c>
    /// when loading blocks on a user dialog (sign-in consent) so the health watchdog exempts it.
    ///
    /// Failure policy is declared once in the constructor via <see cref="Policy(bool, TData)"/>:
    /// required sources fail startup; optional sources degrade to the fallback snapshot and mark
    /// <see cref="UsedFallback"/>.
    ///
    /// Ordering: a constructor parameter of type <c>IContentSource&lt;TOther&gt;</c> /
    /// <c>ContentSource&lt;TOther&gt;</c> declares a data-flow edge — this source initializes
    /// after that one. No attributes needed for content chaining.
    /// </summary>
    public abstract class ContentSource<TData> : IAsyncInitializableService, IContentSource<TData>, IContentSourceInfo
        where TData : class
    {
        private bool _optional;
        private TData? _fallback;

        /// <summary>Loaded data, or the fallback snapshot when degraded.</summary>
        public TData Data { get; private set; } = null!;

        public bool IsLoaded { get; private set; }

        public bool UsedFallback { get; private set; }

        public abstract string SourceName { get; }

        public bool IsOptional => _optional;

        /// <summary>Override to log through the host's logger; defaults to silent.</summary>
        protected virtual ILogger Logger => NullLogger.Instance;

        /// <summary>
        /// Declares the failure policy. Call once from the constructor:
        /// <c>Policy(optional: true, fallback: Snapshot.Offline)</c>. Optional sources degrade
        /// to <paramref name="fallback"/> on failure; required sources fail startup.
        /// </summary>
        protected internal void Policy(bool optional, TData? fallback = null)
        {
            _optional = optional;
            _fallback = fallback;
            if (optional && fallback == null)
                throw new InvalidOperationException(
                    $"Content source '{GetType().Name}' declares an optional policy but provides no fallback.");
        }

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
            catch (Exception ex) when (_optional && !cancellationToken.IsCancellationRequested)
            {
                await OnLoadFailedAsync(ex, cancellationToken).ConfigureAwait(false);
                Logger.LogWarning(ex, "Content source '{SourceName}' failed; degraded to fallback data.", SourceName);
                Data = _fallback!;
                UsedFallback = true;
                IsLoaded = true;
            }
        }
    }
}
