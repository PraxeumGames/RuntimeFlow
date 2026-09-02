using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Demo
{
    /// <summary>
    /// Downloads the item catalog of the revision the remote config named. The constructor parameter is
    /// the ordering edge to the global config service. The two-second timeout is this service's own
    /// policy: a catalog that takes longer fails with a <see cref="System.TimeoutException"/> naming it,
    /// and everything that waits on it is reported as blocked.
    /// </summary>
    [Init(Phase = "content", TimeoutSeconds = 2)]
    public sealed class CatalogService : IAsyncInitializable
    {
        private readonly RemoteConfigService _config;
        private readonly FakeBackend _backend;
        private readonly ChaosToggles _chaos;

        /// <summary>Takes the global config service (the ordering edge), the backend and the toggles.</summary>
        public CatalogService(RemoteConfigService config, FakeBackend backend, ChaosToggles chaos)
        {
            _config = config;
            _backend = backend;
            _chaos = chaos;
        }

        /// <summary>The downloaded catalog, or null while the service has not finished.</summary>
        public Catalog? Catalog { get; private set; }

        /// <summary>Revision this service asked for, taken from the remote config.</summary>
        public string RequestedVersion { get; private set; } = "unknown";

        /// <inheritdoc />
        public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
        {
            if (_chaos.TimeoutInCatalog) _backend.Slow(FakeBackend.Endpoints.Catalog, 10000);

            RequestedVersion = _config.Config.CatalogVersion;
            Catalog = await _backend.GetAsync<Catalog>(FakeBackend.Endpoints.Catalog, cancellationToken);
        }
    }
}
