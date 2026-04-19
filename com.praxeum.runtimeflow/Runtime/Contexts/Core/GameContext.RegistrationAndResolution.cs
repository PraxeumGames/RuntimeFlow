using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using VContainer;

namespace RuntimeFlow.Contexts
{
    public partial class GameContext
    {
        public void Register<TService, TImplementation>() where TImplementation : TService
        {
            var serviceType = typeof(TService);
            var implType = typeof(TImplementation);
            _registrationStore.Register(serviceType, implType, Lifetime.Singleton);
        }

        public void Register(Type serviceType, Type implementationType)
        {
            Register(serviceType, implementationType, Lifetime.Singleton);
        }

        public void Register(Type serviceType, Type implementationType, Lifetime lifetime)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (implementationType == null) throw new ArgumentNullException(nameof(implementationType));
            if (!serviceType.IsAssignableFrom(implementationType) && serviceType != implementationType)
            {
                throw new InvalidOperationException(
                    $"Service type {serviceType.Name} is not assignable from {implementationType.Name}.");
            }

            _registrationStore.Register(serviceType, implementationType, lifetime);
        }

        public void ConfigureContainer(Action<IContainerBuilder> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            _registrationStore.ConfigureContainer(configure);
        }

        public void Decorate(Type serviceType, Type decoratorType)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (decoratorType == null) throw new ArgumentNullException(nameof(decoratorType));
            _decorationChain.Add(serviceType, decoratorType);
        }

        public bool IsRegistered(Type serviceType, bool includeInterfaceTypes = true)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            return _registrationStore.IsRegistered(serviceType, includeInterfaceTypes, _initialized, _container);
        }

        public void RegisterInstance(Type serviceType, object instance)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            RegisterInstanceEx(instance.GetType(), instance, new[] { serviceType }, ownsLifetime: true);
        }

        public void RegisterInstance<TService>(TService instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            RegisterInstanceEx(instance.GetType(), instance, new[] { typeof(TService) }, ownsLifetime: true);
        }

        public void RegisterInstance(object instance, IReadOnlyCollection<Type> serviceTypes)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            if (serviceTypes == null) throw new ArgumentNullException(nameof(serviceTypes));
            RegisterInstanceEx(instance.GetType(), instance, serviceTypes, ownsLifetime: true);
        }

        internal void RegisterImportedInstance(object instance, IReadOnlyCollection<Type> serviceTypes)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            if (serviceTypes == null) throw new ArgumentNullException(nameof(serviceTypes));
            RegisterInstanceEx(instance.GetType(), instance, serviceTypes, ownsLifetime: false);
        }

        public TService Resolve<TService>()
        {
            return (TService)Resolve(typeof(TService));
        }

        public object Resolve(Type serviceType)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (!_initialized || _container == null) throw new InvalidOperationException("Context not initialized");

            // VContainer can instantiate Unity-bound objects during resolve. Ensure resolve happens
            // on Unity main thread when runtime flow continues from a worker thread.
            return DispatchToMainThread(() => ResolveCore(serviceType), $"resolve '{serviceType.FullName}'");
        }

        private object ResolveCore(Type serviceType)
        {
            if (_decorationChain.TryGetDecoratedInstance(serviceType, out var decorated))
                return decorated;

            // Try own container first; fall back to parent only if this context doesn't have the registration
            if (_parent != null && !IsRegistered(serviceType))
            {
                return _parent.Resolve(serviceType);
            }

            return _container!.Resolve(serviceType);
        }

        internal bool TryGetImplementationType(Type serviceType, [MaybeNullWhen(false)] out Type implementationType)
        {
            return _registrationStore.TryGetImplementationType(serviceType, _initialized, _container, out implementationType);
        }

        internal bool TryGetRegisteredInstance(Type serviceType, [MaybeNullWhen(false)] out object instance)
        {
            return _registrationStore.TryGetRegisteredInstance(serviceType, out instance);
        }

        internal KeyValuePair<Type, object>[] GetRegisteredInstanceEntriesSnapshot()
        {
            return _registrationStore.GetRegisteredInstanceEntriesSnapshot();
        }

        internal void RegisterInstanceEx(
            Type implementationType,
            object instance,
            IReadOnlyCollection<Type> serviceTypes,
            bool ownsLifetime)
        {
            _registrationStore.RegisterInstance(implementationType, instance, serviceTypes, ownsLifetime);
        }
    }
}
