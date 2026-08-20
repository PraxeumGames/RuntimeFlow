using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
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

            if (_registrationStore.IsRegistered(serviceType, includeInterfaceTypes))
                return true;

            if (_initialized && _registry != null && _registry.TryGet(serviceType, out var registration))
            {
                if (includeInterfaceTypes || registration!.ImplementationType == serviceType)
                    return true;
            }

            return false;
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
            if (!_initialized || _registry == null) throw new InvalidOperationException("Context not initialized");

            // The registry can instantiate Unity-bound objects during resolve. Ensure
            // resolve happens on Unity main thread when the flow continues from a
            // worker thread.
            return DispatchToMainThread(() => ResolveCore(serviceType), $"resolve '{serviceType.FullName}'");
        }

        internal object Resolve(ServiceInitializerBinding initializer)
        {
            if (initializer == null) throw new ArgumentNullException(nameof(initializer));
            if (!_initialized || _registry == null) throw new InvalidOperationException("Context not initialized");

            return DispatchToMainThread(
                () => ResolveCore(initializer),
                $"resolve '{initializer.ServiceType.FullName}'");
        }

        private object ResolveCore(Type serviceType)
        {
            if (_decorationChain.TryGetDecoratedInstance(serviceType, out var decorated))
                return decorated;

            // Try the local registry first; fall back to the parent chain only for
            // services this context does not register itself (same-or-wider rule).
            if (_registry!.TryGet(serviceType, out var registration) && registration != null)
                return ResolveRegistration(registration);

            if (_parent != null)
                return _parent.Resolve(serviceType);

            throw new VContainerException(serviceType, $"No such registration of type: {serviceType}");
        }

        private object ResolveCore(ServiceInitializerBinding initializer)
        {
            if (initializer.Registration != null)
                return ResolveRegistration(initializer.Registration);

            return ResolveCore(initializer.ResolveServiceType);
        }

        internal bool TryGetImplementationType(Type serviceType, [MaybeNullWhen(false)] out Type implementationType)
        {
            if (_registrationStore.TryGetImplementationType(serviceType, out implementationType))
                return true;

            if (_initialized && _registry != null && _registry.TryGet(serviceType, out var registration) && registration != null)
            {
                implementationType = registration.ImplementationType;
                return true;
            }

            implementationType = null;
            return false;
        }

        internal IReadOnlyList<Registration> GetRegistrationsForServiceType(Type serviceType)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (_registry == null)
                return Array.Empty<Registration>();

            var registrations = new List<Registration>();

            var collectionType = typeof(IReadOnlyList<>).MakeGenericType(serviceType);
            if (_registry.TryGet(collectionType, out var collectionRegistration)
                && collectionRegistration?.Provider is IEnumerable collectionProvider)
            {
                registrations.AddRange(collectionProvider.Cast<object>().OfType<Registration>());
            }

            if (_registry.TryGet(serviceType, out var registration) && registration != null)
            {
                registrations.Add(registration);
            }

            return registrations
                .GroupBy(candidate => candidate.ImplementationType)
                .Select(group => group.First())
                .ToArray();
        }

        internal bool TryGetRegisteredInstance(Type serviceType, [MaybeNullWhen(false)] out object instance)
        {
            return _registrationStore.TryGetRegisteredInstance(serviceType, out instance);
        }

        internal void RegisterInstanceEx(
            Type implementationType,
            object instance,
            IReadOnlyCollection<Type> serviceTypes,
            bool ownsLifetime)
        {
            _registrationStore.RegisterInstance(implementationType, instance, serviceTypes);

            if (ownsLifetime)
                TrackOwnedRegisteredInstance(instance);
        }
    }
}