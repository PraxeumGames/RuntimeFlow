using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Demo
{
    /// <summary>
    /// Stops the session on purpose when the operators closed the game. The constructor parameter is the
    /// ordering edge: RuntimeFlow runs <see cref="RemoteConfigService"/> (a global service) first and
    /// only then this gate. <see cref="InitContext.Halt"/> ends the run without an exception, so the
    /// caller gets <see cref="StartupOutcome.Halted"/> and can show a maintenance screen.
    /// </summary>
    [Init(Phase = "platform")]
    public sealed class MaintenanceGateService : IAsyncInitializable
    {
        /// <summary>Reason handed to <see cref="InitContext.Halt"/>.</summary>
        public const string MaintenanceReason = "maintenance-window";

        private readonly RemoteConfigService _config;
        private readonly ChaosToggles _chaos;

        /// <summary>Takes the global config service (the ordering edge) and the demo toggles.</summary>
        public MaintenanceGateService(RemoteConfigService config, ChaosToggles chaos)
        {
            _config = config;
            _chaos = chaos;
        }

        /// <summary>True when this service halted the run.</summary>
        public bool DidHalt { get; private set; }

        /// <inheritdoc />
        public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
        {
            if (_config.Config.Maintenance || _chaos.MaintenanceHalt)
            {
                DidHalt = true;
                context.Halt(MaintenanceReason);
            }

            return Task.CompletedTask;
        }
    }
}
