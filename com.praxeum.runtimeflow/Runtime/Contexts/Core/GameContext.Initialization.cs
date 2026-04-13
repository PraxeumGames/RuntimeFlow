using System;
using VContainer;

namespace RuntimeFlow.Contexts
{
    public partial class GameContext
    {
        public void Initialize()
        {
            if (_initialized) return;

            // VContainer scope creation invokes build callbacks that may use Unity APIs
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
            OnBeforeInitialize?.Invoke();

            _decorationChain.ValidateRegistrations(serviceType => IsRegistered(serviceType));

            IObjectResolver? parentResolver = null;

            if (_parent is GameContext parentContext && parentContext._initialized)
            {
                parentResolver = parentContext._container;

                // If parent has a VContainer resolver registered, use it as external scope parent
                if (parentContext.TryGetRegisteredInstance(typeof(IObjectResolver), out var externalResolver))
                {
                    parentResolver = (IObjectResolver)externalResolver;
                }
            }

            if (parentResolver != null)
            {
                _container = parentResolver.CreateScope(_registrationStore.ApplyRegistrations);
            }
            else
            {
                var builder = new VContainer.ContainerBuilder();
                _registrationStore.ApplyRegistrations(builder);
                _container = builder.Build();
            }

            _decorationChain.Apply(_container);

            _initialized = true;
            OnInitialized?.Invoke();
        }
    }
}
