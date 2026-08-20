using System;
using System.Collections.Generic;

namespace RuntimeFlow.Contexts
{
    public partial class GameContext
    {
        public void Dispose()
        {
            if (!_initialized) return;
            List<Exception>? disposeFailures = null;
            var onDisposed = OnDisposed;

            try
            {
                OnBeforeDispose?.Invoke();
            }
            catch (Exception ex)
            {
                AddDisposeFailure(ref disposeFailures, ex);
            }

            _decorationChain.ClearResolvedInstances();

            // Single ownership: every instance this context registered or constructed is
            // disposed here, in reverse order, exactly once. No container to reconcile with.
            DisposeOwnedList(_ownedRegisteredInstances, ref disposeFailures);
            DisposeOwnedList(_ownedResolvedInstances, ref disposeFailures);

            _registry = null;
            _sharedInstances.Clear();
            _ownedRegisteredInstances.Clear();
            _ownedResolvedInstances.Clear();
            _initialized = false;

            _registrationStore.ClearRegistrations();
            _decorationChain.Clear();

            OnBeforeInitialize = null;
            OnInitialized = null;
            OnBeforeDispose = null;
            OnDisposed = null;
            try
            {
                onDisposed?.Invoke();
            }
            catch (Exception ex)
            {
                AddDisposeFailure(ref disposeFailures, ex);
            }

            if (disposeFailures is { Count: > 0 })
            {
                throw new AggregateException(
                    "GameContext disposal encountered one or more failures.",
                    disposeFailures);
            }
        }

        private static void DisposeOwnedList(List<object> instances, ref List<Exception>? disposeFailures)
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
                    AddDisposeFailure(ref disposeFailures, ex);
                }
            }
        }

        private static void AddDisposeFailure(ref List<Exception>? failures, Exception exception)
        {
            failures ??= new List<Exception>();
            failures.Add(exception);
        }
    }
}