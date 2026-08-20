using System;
using VContainer;

namespace RuntimeFlow.Contexts
{
    public partial class GameContext
    {
        public void Initialize()
        {
            if (_initialized) return;

            // Registry build callbacks and decoration resolution may use Unity APIs
            // (Addressables, LayerMask, etc.) which require the main thread.
            // RuntimeFlow uses ConfigureAwait(false) so this method can be called
            // from a thread pool thread. Dispatch to main thread if needed.
            DispatchToMainThread(
                () =>
                {
                    InitializeCore();
                    return true;
                },
                "initialize context");
        }

        private void InitializeCore()
        {
            _disposed = false;
            OnBeforeInitialize?.Invoke();

            _decorationChain.ValidateRegistrations(serviceType => IsRegistered(serviceType));

            // The context becomes a live container before the registry is built: build
            // callbacks (RegisterBuildCallback warmup) and decoration resolution resolve
            // through this context and must not hit the "not initialized" guard.
            _initialized = true;

            try
            {
                var builder = new RuntimeFlowContainerBuilder();
                _registrationStore.ApplyRegistrations(builder);
                _registry = builder.BuildRegistry(this, Diagnostics);
                Diagnostics.NotifyContainerBuilt(this);

                _decorationChain.Apply(this);
            }
            catch
            {
                _initialized = false;
                _registry = null;
                throw;
            }

            OnInitialized?.Invoke();
        }
    }
}