using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Demo
{
    /// <summary>
    /// Signs the player in with the platform. Optional: the game is playable without an account, so a
    /// failure degrades the run instead of failing it. The properties keep their anonymous defaults when
    /// the request throws, and the exception still reaches RuntimeFlow, which reports the service as
    /// degraded and lists it in <see cref="StartupResult.Degraded"/> and <see cref="InitContext.DegradedServices"/>.
    /// </summary>
    [Init(Optional = true)]
    public sealed class PlatformAuthService : IAsyncInitializable
    {
        /// <summary>Identifier used while the player is not signed in.</summary>
        public const string AnonymousPlayerId = "anonymous";

        private readonly FakeBackend _backend;

        /// <summary>Takes the backend; the demo registers one instance in both scopes.</summary>
        public PlatformAuthService(FakeBackend backend) => _backend = backend;

        /// <summary>The signed-in identifier, or <see cref="AnonymousPlayerId"/> while degraded.</summary>
        public string PlayerId { get; private set; } = AnonymousPlayerId;

        /// <summary>True until a sign-in succeeds; stays true when the platform is down.</summary>
        public bool IsAnonymous { get; private set; } = true;

        /// <inheritdoc />
        public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
        {
            var auth = await _backend.GetAsync<AuthResult>(FakeBackend.Endpoints.Auth, cancellationToken);
            PlayerId = auth.PlayerId;
            IsAnonymous = false;
        }
    }
}
