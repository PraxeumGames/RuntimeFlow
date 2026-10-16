using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Demo
{
    /// <summary>
    /// Waits for the player to accept the privacy notice before anything else in the session starts.
    /// User-gated, so it never times out and shows up as <see cref="ServiceState.Running"/> with
    /// <see cref="ServiceStatus.AwaitingPlayer"/> set; while it is the only thing in flight RuntimeFlow
    /// logs an "awaiting player" line instead of a stall warning.
    /// </summary>
    [Init(UserGated = true, Phase = "platform")]
    public sealed class GdprConsentService : IAsyncInitializable
    {
        private readonly TaskCompletionSource<bool> _accepted =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly ChaosToggles _chaos;

        /// <summary>Takes the toggles so the demo can skip the dialog on replays.</summary>
        public GdprConsentService(ChaosToggles chaos) => _chaos = chaos;

        /// <summary>True once the player accepted and initialization finished.</summary>
        public bool IsAccepted { get; private set; }

        /// <summary>Called by the UI when the player taps "Accept"; releases the gate.</summary>
        public void Accept() => _accepted.TrySetResult(true);

        /// <inheritdoc />
        public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
        {
            if (_chaos.GdprAlreadyAccepted) _accepted.TrySetResult(true);

            using (cancellationToken.Register(() => _accepted.TrySetCanceled()))
            {
                await _accepted.Task;
            }

            IsAccepted = true;
        }
    }
}
