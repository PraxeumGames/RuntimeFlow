using System;
using System.Collections.Generic;
using VContainer;

namespace RuntimeFlow.Contexts
{
    /// <summary>Represents a scoped DI context that manages service registration, resolution, and lifecycle.</summary>
    public interface IGameContext
    {
        void Register<TService, TImplementation>() where TImplementation : TService;
        void Register(Type serviceType, Type implementationType);
        void Register(Type serviceType, Type implementationType, Lifetime lifetime);
        void RegisterInstance<TService>(TService instance);
        void RegisterInstance(Type serviceType, object instance);
        void RegisterInstance(object instance, IReadOnlyCollection<Type> serviceTypes);
        void ConfigureContainer(Action<IContainerBuilder> configure);
        bool IsRegistered(Type serviceType, bool includeInterfaceTypes = true);
        /// <summary>The underlying VContainer resolver for this context's scope.</summary>
        VContainer.IObjectResolver Resolver { get; }
        TService Resolve<TService>();
        object Resolve(Type serviceType);
        event System.Action? OnBeforeInitialize;
        event System.Action? OnInitialized;
        event System.Action? OnBeforeDispose;
        event System.Action? OnDisposed;
        void Initialize();
        void Dispose();
    }
}
