using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Demo
{
    /// <summary>
    /// Fetches the remote configuration into the global scope. Required: nothing downstream can decide
    /// anything without it, so a failure here fails the whole startup with this service's name.
    /// It declares a five-second timeout because a hung config request must not hang the game forever.
    /// </summary>
    [Init(TimeoutSeconds = 5)]
    public sealed class RemoteConfigService : IAsyncInitializable
    {
        private readonly FakeBackend _backend;

        /// <summary>Takes the backend; the demo registers one instance in both scopes.</summary>
        public RemoteConfigService(FakeBackend backend) => _backend = backend;

        /// <summary>The fetched configuration; built-in defaults until <see cref="InitializeAsync"/> succeeds.</summary>
        public RemoteConfig Config { get; private set; } = new RemoteConfig();

        /// <inheritdoc />
        public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            => Config = await _backend.GetAsync<RemoteConfig>(FakeBackend.Endpoints.Config, cancellationToken);
    }
}
