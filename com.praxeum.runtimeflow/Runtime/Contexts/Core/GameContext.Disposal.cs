using System;
using System.Collections.Generic;
using VContainer;

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

            DisposeOwnedRegisteredInstances(ref disposeFailures);
            if (_container is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    AddDisposeFailure(ref disposeFailures, ex);
                }
            }

            foreach (var instanceProvider in _instanceProviders)
            {
                try
                {
                    instanceProvider.Release();
                }
                catch (Exception ex)
                {
                    AddDisposeFailure(ref disposeFailures, ex);
                }
            }

            _registrationStore.ClearProviderInstances();
            _container = null;
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

        private void DisposeOwnedRegisteredInstances(ref List<Exception>? disposeFailures)
        {
            _registrationStore.DisposeOwnedRegisteredInstances(ref disposeFailures);
        }

        private static void AddDisposeFailure(ref List<Exception>? failures, Exception exception)
        {
            failures ??= new List<Exception>();
            failures.Add(exception);
        }
    }
}
