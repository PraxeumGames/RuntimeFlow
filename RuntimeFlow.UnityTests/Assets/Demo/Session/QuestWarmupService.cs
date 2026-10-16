using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Demo
{
    /// <summary>
    /// Warms the quest pack up after the catalog is on disk. It does not need the catalog's data, only
    /// its side effect, so the edge is declared with <see cref="DependsOnAttribute"/> instead of a
    /// constructor parameter. Weight 5 makes it dominate the progress bar, and
    /// <see cref="InitContext.ReportProgress"/> fills that share in while the warmup runs.
    /// </summary>
    [DependsOn(typeof(CatalogService))]
    [Init(Phase = "warmup", Weight = 5)]
    public sealed class QuestWarmupService : IAsyncInitializable
    {
        private readonly float[] _steps = { 0.2f, 0.4f, 0.6f, 0.8f };
        private readonly FakeBackend _backend;
        private readonly ChaosToggles _chaos;

        /// <summary>Takes the backend and the toggles; the catalog edge is declared by the attribute.</summary>
        public QuestWarmupService(FakeBackend backend, ChaosToggles chaos)
        {
            _backend = backend;
            _chaos = chaos;
        }

        /// <summary>The warmed quest pack, or null while the service has not finished.</summary>
        public QuestPack? Quests { get; private set; }

        /// <summary>Number of sub-progress steps reported so far.</summary>
        public int WarmedSteps { get; private set; }

        /// <inheritdoc />
        public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
        {
            // Never returns: the run's cancellation (deadline, failure, restart or disposal) ends it.
            if (_chaos.HangInQuestWarmup) await Task.Delay(Timeout.Infinite, cancellationToken);

            var pack = await _backend.GetAsync<QuestPack>(FakeBackend.Endpoints.Quests, cancellationToken);

            foreach (var step in _steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                context.ReportProgress(step);
                WarmedSteps++;
                await Task.Yield();
            }

            Quests = pack;
        }
    }
}
