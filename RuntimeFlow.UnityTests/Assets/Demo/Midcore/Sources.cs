using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Content;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Demo.Midcore
{
    // ---------- session-scope content sources ----------
    // These chain after global sources via constructor data-flow edges.
    // Session scope rebuilds on restart; Global stays warm.

    /// <summary>Server profile: chained after auth (ctor injects IContentSource&lt;AuthData&gt;). Optional — offline profile supported.</summary>
    public sealed class ProfileSource : ContentSource<ProfileData>, ISessionInitializableService
    {
        private readonly IContentSource<AuthData> _auth;

        public ProfileSource(IContentSource<AuthData> auth)
        {
            _auth = auth;
            Policy(optional: true, fallback: new ProfileData { DisplayName = "OfflineWarrior", Level = 1 });
        }

        public override string SourceName => "server-profile";

        protected override async Task<ProfileData> LoadAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            return new ProfileData
            {
                DisplayName = $"Hero_{_auth.Data.PlayerId}",
                Level = 42,
                Coins = 1200,
            };
        }
    }

    /// <summary>Addressables-style catalog: session Content stage.</summary>
    public sealed class CatalogSource : ContentSource<CatalogData>, ISessionInitializableService
    {
        public override string SourceName => "catalog";

        protected override Task<CatalogData> LoadAsync(CancellationToken cancellationToken)
        {
            var snapshot = new CatalogData();
            snapshot.Bundles.Add("heroes.base");
            snapshot.Bundles.Add("world.biome01");
            snapshot.Bundles.Add("ui.icons");
            return Task.FromResult(snapshot);
        }
    }
}
