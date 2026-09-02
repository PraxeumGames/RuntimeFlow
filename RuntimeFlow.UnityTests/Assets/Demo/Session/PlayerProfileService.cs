using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Demo
{
    /// <summary>
    /// Loads the player's progression. Both constructor parameters are ordering edges, so the profile is
    /// fetched only after the config and the sign-in attempt finished — including the case where the
    /// sign-in degraded, which yields an anonymous profile. Weight 2 makes it count double in the
    /// weighted progress percentage.
    /// </summary>
    [Init(Phase = "content", Weight = 2)]
    public sealed class PlayerProfileService : IAsyncInitializable
    {
        private readonly RemoteConfigService _config;
        private readonly PlatformAuthService _auth;
        private readonly FakeBackend _backend;
        private readonly ChaosToggles _chaos;

        /// <summary>Takes the two upstream services (the ordering edges), the backend and the toggles.</summary>
        public PlayerProfileService(
            RemoteConfigService config,
            PlatformAuthService auth,
            FakeBackend backend,
            ChaosToggles chaos)
        {
            _config = config;
            _auth = auth;
            _backend = backend;
            _chaos = chaos;
        }

        /// <summary>The loaded profile, or null while the service has not finished.</summary>
        public PlayerProfile? Profile { get; private set; }

        /// <inheritdoc />
        public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
        {
            if (_chaos.ThrowInProfile)
                throw new InvalidOperationException("profile service exploded (chaos)");

            var profile = await _backend.GetAsync<PlayerProfile>(FakeBackend.Endpoints.Profile, cancellationToken);
            profile.PlayerId = _auth.PlayerId;
            profile.DisplayName = _auth.IsAnonymous ? "Guest" : "Hero_" + _auth.PlayerId;
            profile.Coins += _config.Config.GiftCoins;
            Profile = profile;
        }
    }
}
