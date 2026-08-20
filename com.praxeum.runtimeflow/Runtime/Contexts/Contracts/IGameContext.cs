using System;
using System.Collections.Generic;
using VContainer;

namespace RuntimeFlow.Contexts
{
    /// <summary>Represents a scoped DI context that manages service registration, resolution, and lifecycle.</summary>
    public interface IGameContext : IAsyncDisposable
    {
        void Register<TService, TImplementation>() where TImplementation : TService;
        void Register(Type serviceType, Type implementationType);
        void Register(Type serviceType, Type implementationType, Lifetime lifetime);
        void RegisterInstance<TService>(TService instance);
        void RegisterInstance(Type serviceType, object instance);
        void RegisterInstance(object instance, IReadOnlyCollection<Type> serviceTypes);
        void ConfigureContainer(Action<IContainerBuilder> configure);
        bool IsRegistered(Type serviceType, bool includeInterfaceTypes = true);
        /// <summary>This context is its own resolver.</summary>
        VContainer.IObjectResolver Resolver { get; }
        TService Resolve<TService>();
        object Resolve(Type serviceType);
        event System.Action? OnBeforeInitialize;
        event System.Action? OnInitialized;
        event System.Action? OnBeforeDispose;
        event System.Action? OnDisposed;
        void Initialize();
        /// <summary>
        /// Synchronously disposes the context. Throws <see cref="NotSupportedException"/> when the context
        /// holds initialized async-disposable services; use <see cref="IAsyncDisposable.DisposeAsync"/> instead.
        /// </summary>
        void Dispose();
    }
}
