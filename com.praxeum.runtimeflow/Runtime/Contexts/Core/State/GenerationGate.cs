using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    internal sealed class GenerationGate
    {
        private readonly ScopeOperationCoordinator _coordinator;

        public GenerationGate(ScopeOperationCoordinator coordinator)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        }

        public ScopeOperationCoordinator Coordinator => _coordinator;

        public SemaphoreSlim SideLock => _coordinator.SideLock;

        public SemaphoreSlim LazyInitLock => _coordinator.LazyInitLock;

        public long CurrentGeneration => _coordinator.ReadScopeGeneration();

        public long BeginNewGeneration() => _coordinator.BeginNewScopeGeneration();

        public void PublishInCurrentGeneration(long generation, CancellationToken cancellationToken, Action publish)
            => _coordinator.PublishInCurrentGeneration(generation, cancellationToken, publish);

        public void ThrowIfStaleGeneration(long generation, CancellationToken cancellationToken)
            => _coordinator.ThrowIfStaleGeneration(generation, cancellationToken);

        public Task ExecuteExclusiveAsync(IInitializationProgressNotifier? notifier, CancellationToken cancellationToken, Func<ScopeOperationCoordinator.ScopeOperationContext, Task> operation)
            => _coordinator.ExecuteExclusiveScopeOperationAsync(notifier, cancellationToken, operation);

        public Task ExecuteParentInvalidatingExclusiveAsync(IInitializationProgressNotifier? notifier, CancellationToken cancellationToken, Func<ScopeOperationCoordinator.ScopeOperationContext, Task> operation)
            => _coordinator.ExecuteParentInvalidatingExclusiveScopeOperationAsync(notifier, cancellationToken, operation);

        public Task ExecuteGenerationBoundSideAsync(IInitializationProgressNotifier? notifier, CancellationToken cancellationToken, Func<ScopeOperationCoordinator.ScopeOperationContext, Task> operation)
            => _coordinator.ExecuteGenerationBoundSideScopeOperationAsync(notifier, cancellationToken, operation);

        public Task CancelActiveLoadAsync(CancellationToken cancellationToken = default)
            => _coordinator.CancelActiveLoadAsync(cancellationToken);
    }
}
