using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    internal sealed class ScopeOperationCoordinator
    {
        private CancellationTokenSource? _activeLoadCts;
        private Task _activeLoadTask = Task.CompletedTask;
        private readonly object _activeLoadSync = new();
        private readonly SemaphoreSlim _exclusiveScopeOperationStartLock = new(1, 1);
        private long _runGeneration;
        private readonly object _scopeGenerationSync = new();
        private readonly SemaphoreSlim _sideScopeOperationLock = new(1, 1);
        private readonly SemaphoreSlim _lazyInitLock = new(1, 1);

        internal SemaphoreSlim SideLock => _sideScopeOperationLock;
        internal SemaphoreSlim LazyInitLock => _lazyInitLock;

        internal async Task ExecuteExclusiveScopeOperationAsync(
            IInitializationProgressNotifier? progressNotifier,
            CancellationToken cancellationToken,
            Func<ScopeOperationContext, Task> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            Task operationTask;
            await _exclusiveScopeOperationStartLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await CancelActiveLoadAsync(CancellationToken.None).ConfigureAwait(false);
                operationTask = BeginExclusiveScopeOperation(progressNotifier, cancellationToken, operation);
            }
            finally
            {
                _exclusiveScopeOperationStartLock.Release();
            }
            await operationTask.ConfigureAwait(false);
        }

        internal async Task ExecuteParentInvalidatingExclusiveScopeOperationAsync(
            IInitializationProgressNotifier? progressNotifier,
            CancellationToken cancellationToken,
            Func<ScopeOperationContext, Task> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            Task operationTask;
            await _exclusiveScopeOperationStartLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await CancelActiveLoadAsync(CancellationToken.None).ConfigureAwait(false);
                await _sideScopeOperationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    operationTask = BeginExclusiveScopeOperation(progressNotifier, cancellationToken, operation);
                }
                catch
                {
                    _sideScopeOperationLock.Release();
                    throw;
                }
            }
            finally
            {
                _exclusiveScopeOperationStartLock.Release();
            }
            try
            {
                await operationTask.ConfigureAwait(false);
            }
            finally
            {
                _sideScopeOperationLock.Release();
            }
        }

        internal async Task ExecuteGenerationBoundSideScopeOperationAsync(
            IInitializationProgressNotifier? progressNotifier,
            CancellationToken cancellationToken,
            Func<ScopeOperationContext, Task> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            await _sideScopeOperationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var generation = ReadScopeGeneration();
                var operationContext = CreateScopeOperationContext(generation, progressNotifier, cancellationToken);
                await operation(operationContext).ConfigureAwait(false);
            }
            finally
            {
                _sideScopeOperationLock.Release();
            }
        }

        internal async Task CancelActiveLoadAsync(CancellationToken cancellationToken = default)
        {
            CancellationTokenSource? activeLoadCts;
            Task activeLoadTask;
            lock (_activeLoadSync)
            {
                activeLoadCts = _activeLoadCts;
                if (activeLoadCts == null) return;
                activeLoadTask = _activeLoadTask;
            }
            activeLoadCts.Cancel();
            try
            {
                await AwaitWithCancellation(activeLoadTask, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception) when (activeLoadCts.IsCancellationRequested) { }
            finally
            {
                if (ClearActiveLoadIfOwner(activeLoadCts))
                    activeLoadCts.Dispose();
            }
        }

        internal long BeginNewScopeGeneration()
        {
            lock (_scopeGenerationSync) return ++_runGeneration;
        }

        internal long ReadScopeGeneration()
        {
            lock (_scopeGenerationSync) return _runGeneration;
        }

        internal void PublishInCurrentGeneration(long generation, CancellationToken cancellationToken, Action publish)
        {
            if (publish == null) throw new ArgumentNullException(nameof(publish));
            cancellationToken.ThrowIfCancellationRequested();
            lock (_scopeGenerationSync)
            {
                if (generation != _runGeneration)
                    throw new OperationCanceledException(cancellationToken);
                publish();
            }
        }

        internal void ThrowIfStaleGeneration(long generation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_scopeGenerationSync)
            {
                if (generation != _runGeneration)
                    throw new OperationCanceledException(cancellationToken);
            }
        }

        private Task BeginExclusiveScopeOperation(
            IInitializationProgressNotifier? progressNotifier,
            CancellationToken cancellationToken,
            Func<ScopeOperationContext, Task> operation)
        {
            var generation = BeginNewScopeGeneration();
            var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var operationContext = CreateScopeOperationContext(generation, progressNotifier, operationCts.Token);
            var completionSource = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_activeLoadSync)
            {
                _activeLoadCts = operationCts;
                _activeLoadTask = completionSource.Task;
            }
            _ = RunExclusiveScopeOperationAsync(operation, operationContext, operationCts, completionSource);
            return completionSource.Task;
        }

        private async Task RunExclusiveScopeOperationAsync(
            Func<ScopeOperationContext, Task> operation,
            ScopeOperationContext operationContext,
            CancellationTokenSource operationCts,
            TaskCompletionSource<object?> completionSource)
        {
            try
            {
                await operation(operationContext).ConfigureAwait(false);
                completionSource.TrySetResult(null);
            }
            catch (OperationCanceledException) { completionSource.TrySetCanceled(); }
            catch (Exception ex) { completionSource.TrySetException(ex); }
            finally
            {
                if (ClearActiveLoadIfOwner(operationCts))
                    operationCts.Dispose();
            }
        }

        private bool ClearActiveLoadIfOwner(CancellationTokenSource operationCts)
        {
            lock (_activeLoadSync)
            {
                if (!ReferenceEquals(_activeLoadCts, operationCts)) return false;
                _activeLoadCts = null;
                _activeLoadTask = Task.CompletedTask;
                return true;
            }
        }

        private static async Task AwaitWithCancellation(Task task, CancellationToken cancellationToken)
        {
            if (task.IsCompleted) { await task.ConfigureAwait(false); return; }
            var cancellationTask = Task.Delay(Timeout.Infinite, cancellationToken);
            var completed = await Task.WhenAny(task, cancellationTask).ConfigureAwait(false);
            if (completed != task) cancellationToken.ThrowIfCancellationRequested();
            await task.ConfigureAwait(false);
        }

        private static ScopeOperationContext CreateScopeOperationContext(long generation, IInitializationProgressNotifier? progressNotifier, CancellationToken cancellationToken)
        {
            return new ScopeOperationContext(generation, progressNotifier ?? NullInitializationProgressNotifier.Instance, cancellationToken);
        }

        internal readonly struct ScopeOperationContext
        {
            public ScopeOperationContext(long generation, IInitializationProgressNotifier progressNotifier, CancellationToken cancellationToken)
            {
                Generation = generation;
                ProgressNotifier = progressNotifier ?? throw new ArgumentNullException(nameof(progressNotifier));
                CancellationToken = cancellationToken;
            }
            public long Generation { get; }
            public IInitializationProgressNotifier ProgressNotifier { get; }
            public CancellationToken CancellationToken { get; }
        }
    }
}
