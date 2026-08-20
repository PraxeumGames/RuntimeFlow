using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    public partial class GameContext
    {
        /// <summary>
        /// Synchronous teardown for contexts without asynchronous services. Async-disposable
        /// services require <see cref="DisposeAsync(System.Threading.CancellationToken)"/>;
        /// silently skipping them would leak their teardown, so this throws instead.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            if (HasPendingAsyncDisposals())
            {
                throw new NotSupportedException(
                    "GameContext owns asynchronous services; use await DisposeAsync() instead of Dispose() " +
                    "so their disposal runs on the correct scheduler and thread affinity.");
            }

            _disposed = true;

            var failures = new List<Exception>();
            RunSynchronousTeardown(failures);

            if (failures.Count > 0)
                throw new AggregateException("GameContext disposal encountered one or more failures.", failures);
        }

        /// <summary>
        /// Native teardown: async-disposable services are disposed in reverse
        /// initialization order (with their declared thread affinity), then every
        /// registered and constructed instance in reverse order. The context is the
        /// single owner of both phases.
        /// </summary>
        public async ValueTask DisposeAsync(CancellationToken cancellationToken = default)
        {
            if (ExecutionScheduler == null && HasPendingAsyncDisposals())
            {
                throw new InvalidOperationException(
                    "An ExecutionScheduler is required to dispose async-disposable services. " +
                    "Create the context through GameContextBuilder or set ExecutionScheduler explicitly.");
            }

            if (_disposed) return;
            _disposed = true;

            var failures = new List<Exception>();
            await DisposeInitializedServicesAsync(cancellationToken, failures).ConfigureAwait(false);
            await RunSynchronousTeardownAsync(failures, cancellationToken).ConfigureAwait(false);

            if (failures.Count > 0)
                throw new AggregateException("GameContext disposal encountered one or more failures.", failures);
        }

        ValueTask IAsyncDisposable.DisposeAsync() => DisposeAsync();

        private async Task DisposeInitializedServicesAsync(
            CancellationToken cancellationToken,
            List<Exception> failures)
        {
            if (_initializationOrder.Count == 0)
                return;

            var scheduler = ExecutionScheduler ?? InlineInitializationExecutionScheduler.Instance;
            var disposedInstances = new HashSet<object>(ReferenceEqualityComparer.Instance);

            for (var i = _initializationOrder.Count - 1; i >= 0; i--)
            {
                var initializer = _initializationOrder[i];
                if (!TryGetInitializedInstance(initializer, out var instance))
                    continue;

                // Multiple service types can map to the same instance; dispose it once.
                if (!disposedInstances.Add(instance))
                    continue;

                try
                {
                    if (instance is IAsyncDisposableService asyncService)
                    {
                        await scheduler.ExecuteAsync(
                                ResolveTeardownAffinity(instance),
                                token => asyncService.DisposeAsync(token),
                                cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (instance is IAsyncDisposable asyncDisposable)
                    {
                        await scheduler.ExecuteAsync(
                                ResolveTeardownAffinity(instance),
                                _ => asyncDisposable.DisposeAsync().AsTask(),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }
        }

        private static InitializationThreadAffinity ResolveTeardownAffinity(object instance)
        {
            return instance is IInitializationThreadAffinityProvider affinityProvider
                ? affinityProvider.ThreadAffinity
                : InitializationThreadAffinity.MainThread;
        }

        private bool TryGetInitializedInstance(ServiceInitializerBinding initializer, out object instance)
        {
            var registration = initializer.Registration;
            if (registration == null && _registry != null
                && _registry.TryGet(initializer.ResolveServiceType, out var found))
            {
                registration = found;
            }

            if (registration != null && _sharedInstances.TryGetValue(registration, out instance!))
                return true;

            instance = null!;
            return false;
        }

        private bool HasPendingAsyncDisposals()
        {
            foreach (var initializer in _initializationOrder)
            {
                if (TryGetInitializedInstance(initializer, out var instance)
                    && (instance is IAsyncDisposableService || instance is IAsyncDisposable))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task RunSynchronousTeardownAsync(
            List<Exception> failures,
            CancellationToken cancellationToken)
        {
            var scheduler = ExecutionScheduler ?? InlineInitializationExecutionScheduler.Instance;
            await scheduler.ExecuteAsync(
                    InitializationThreadAffinity.MainThread,
                    _ =>
                    {
                        RunSynchronousTeardown(failures);
                        return Task.CompletedTask;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private void RunSynchronousTeardown(List<Exception> failures)
        {
            var onDisposed = OnDisposed;

            try
            {
                OnBeforeDispose?.Invoke();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }

            _decorationChain.ClearResolvedInstances();

            // Single ownership: every instance this context registered or constructed is
            // disposed here, in reverse order, exactly once. No container to reconcile with.
            DisposeOwnedList(_ownedRegisteredInstances, failures);
            DisposeOwnedList(_ownedResolvedInstances, failures);

            ResetState();

            try
            {
                onDisposed?.Invoke();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        private void ResetState()
        {
            _registry = null;
            _sharedInstances.Clear();
            _ownedRegisteredInstances.Clear();
            _ownedRegisteredInstancesLookup.Clear();
            _ownedResolvedInstances.Clear();
            _ownedResolvedInstancesLookup.Clear();
            _initializationOrder.Clear();
            _initializationOrderLookup.Clear();
            _initialized = false;

            _registrationStore.ClearRegistrations();
            _decorationChain.Clear();

            OnBeforeInitialize = null;
            OnInitialized = null;
            OnBeforeDispose = null;
            OnDisposed = null;
        }

        private static void DisposeOwnedList(List<object> instances, List<Exception> failures)
        {
            for (var i = instances.Count - 1; i >= 0; i--)
            {
                if (instances[i] is not IDisposable disposable)
                    continue;

                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }
        }
    }
}