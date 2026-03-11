using System;
using System.Collections.Generic;
using VContainer;

namespace RuntimeFlow.Contexts
{
    internal sealed class ResolverBackedGameContext : IGameContext
    {
        private readonly VContainer.IObjectResolver _resolver;

        public ResolverBackedGameContext(VContainer.IObjectResolver resolver)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        }

        public IObjectResolver Resolver => _resolver;

        public event Action? OnBeforeInitialize { add { } remove { } }
        public event Action? OnInitialized { add { } remove { } }
        public event Action? OnBeforeDispose { add { } remove { } }
        public event Action? OnDisposed { add { } remove { } }

        public void Register<TService, TImplementation>() where TImplementation : TService
        {
            throw new NotSupportedException("External resolver-backed context does not support registrations.");
        }

        public void Register(Type serviceType, Type implementationType)
        {
            throw new NotSupportedException("External resolver-backed context does not support registrations.");
        }

        public void Register(Type serviceType, Type implementationType, Lifetime lifetime)
        {
            throw new NotSupportedException("External resolver-backed context does not support registrations.");
        }

        public void RegisterInstance<TService>(TService instance)
        {
            throw new NotSupportedException("External resolver-backed context does not support registrations.");
        }

        public void RegisterInstance(Type serviceType, object instance)
        {
            throw new NotSupportedException("External resolver-backed context does not support registrations.");
        }

        public void RegisterInstance(object instance, IReadOnlyCollection<Type> serviceTypes)
        {
            throw new NotSupportedException("External resolver-backed context does not support registrations.");
        }

        public void ConfigureContainer(Action<VContainer.IContainerBuilder> configure)
        {
            throw new NotSupportedException("External resolver-backed context does not support container configuration.");
        }

        public bool IsRegistered(Type serviceType, bool includeInterfaceTypes = true)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            try
            {
                _resolver.Resolve(serviceType);
                return true;
            }
            catch (VContainerException)
            {
                return false;
            }
        }

        public TService Resolve<TService>()
        {
            return (TService)Resolve(typeof(TService));
        }

        public object Resolve(Type serviceType)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            return _resolver.Resolve(serviceType);
        }

        public void Initialize()
        {
        }

        public void Dispose()
        {
        }
    }
}
