using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using RuntimeFlow.Content;

namespace RuntimeFlow.Demo.Midcore
{
    public sealed class PreloaderScene : ISceneScope
    {
        public void Configure(IGameScopeRegistrationBuilder builder)
        {
            builder.Register<PreloaderController>(DiLifetime.Singleton);
        }
    }

    public sealed class PreloaderController : ISceneInitializableService
    {
        private readonly IContentSource<AuthData> _auth;
        private readonly IContentSource<CatalogData> _catalog;

        public static readonly List<string> ProgressLog = new();
        public static bool Ready { get; private set; }
        public static void Reset() { ProgressLog.Clear(); Ready = false; }

        public PreloaderController(IContentSource<AuthData> auth, IContentSource<CatalogData> catalog)
        {
            _auth = auth; _catalog = catalog;
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            ProgressLog.Add($"player={_auth.Data.PlayerId} bundles={_catalog.Data.Bundles.Count}");
            Ready = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>Meta scene: the hub the player lands in after loading.</summary>
    public sealed class MetaScene : ISceneScope
    {
        public void Configure(IGameScopeRegistrationBuilder builder)
        {
            builder.Register<MetaBootstrap>(DiLifetime.Singleton);
        }
    }

    public sealed class MetaBootstrap : ISceneInitializableService
    {
        private readonly IContentSource<AuthData> _auth;
        private readonly IContentSource<CatalogData> _catalog;

        public static string? Summary { get; private set; }
        public static void ResetSummary() => Summary = null;

        public MetaBootstrap(IContentSource<AuthData> auth, IContentSource<CatalogData> catalog)
        {
            _auth = auth; _catalog = catalog;
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Summary = $"{_auth.Data.PlayerId} bundles={_catalog.Data.Bundles.Count}";
            return Task.CompletedTask;
        }
    }
}
