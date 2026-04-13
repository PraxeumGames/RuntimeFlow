using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using VContainer;

namespace RuntimeFlow.Contexts
{
    internal sealed class GameContextRegistrationStore
    {
        private readonly List<Action<IContainerBuilder>> _registrations = new();
        private readonly HashSet<Type> _registeredServiceTypes = new();
        private readonly ConcurrentDictionary<Type, Type> _implementationTypes = new();
        private readonly Dictionary<Type, object> _registeredInstances = new();
        private readonly Dictionary<Type, (Lifetime lifetime, List<Type> interfaces)> _typedRegistrations = new();
        private readonly Dictionary<Type, (object instance, List<Type> interfaces, bool ownsLifetime)> _instanceRegistrations = new();
        private readonly List<RuntimeFlowInstanceProvider> _instanceProviders = new();

        public IReadOnlyCollection<Type> RegisteredServiceTypes => _registeredServiceTypes;
        public List<RuntimeFlowInstanceProvider> InstanceProviders => _instanceProviders;

        public void Register(Type serviceType, Type implementationType, Lifetime lifetime)
        {
            _registeredServiceTypes.Add(serviceType);
            _implementationTypes[serviceType] = implementationType;
            AddTypedRegistration(implementationType, lifetime, serviceType);
        }

        public void ConfigureContainer(Action<IContainerBuilder> configure)
        {
            _registrations.Add(configure);
        }

        public bool IsRegistered(
            Type serviceType,
            bool includeInterfaceTypes,
            bool initialized,
            IObjectResolver? container)
        {
            if (_registeredServiceTypes.Contains(serviceType))
                return true;

            if (includeInterfaceTypes && _implementationTypes.ContainsKey(serviceType))
                return true;

            if (initialized && container != null && container.TryGetRegistration(serviceType, out var registration))
            {
                if (includeInterfaceTypes || registration!.ImplementationType == serviceType)
                    return true;
            }

            return false;
        }

        public bool TryGetImplementationType(
            Type serviceType,
            bool initialized,
            IObjectResolver? container,
            out Type implementationType)
        {
            if (_implementationTypes.TryGetValue(serviceType, out implementationType!))
                return true;

            if (initialized
                && container != null
                && container.TryGetRegistration(serviceType, out var registration)
                && registration != null)
            {
                implementationType = registration.ImplementationType;
                _implementationTypes[serviceType] = implementationType;
                return true;
            }

            implementationType = null!;
            return false;
        }

        public bool TryGetRegisteredInstance(Type serviceType, out object instance)
        {
            return _registeredInstances.TryGetValue(serviceType, out instance!);
        }

        public KeyValuePair<Type, object>[] GetRegisteredInstanceEntriesSnapshot()
        {
            return _registeredInstances.ToArray();
        }

        public void RegisterInstance(
            Type implementationType,
            object instance,
            IReadOnlyCollection<Type> serviceTypes,
            bool ownsLifetime)
        {
            if (implementationType == null) throw new ArgumentNullException(nameof(implementationType));
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            if (serviceTypes == null) throw new ArgumentNullException(nameof(serviceTypes));

            var exposedTypes = serviceTypes
                .Where(type => type != null)
                .Distinct()
                .ToArray();

            if (exposedTypes.Length == 0)
                exposedTypes = new[] { implementationType };

            foreach (var serviceType in exposedTypes)
            {
                _registeredServiceTypes.Add(serviceType);
                _implementationTypes[serviceType] = implementationType;
                _registeredInstances[serviceType] = instance;
            }

            if (_instanceRegistrations.TryGetValue(implementationType, out var existing))
            {
                existing.instance = instance;
                foreach (var t in exposedTypes)
                {
                    if (!existing.interfaces.Contains(t))
                        existing.interfaces.Add(t);
                }

                existing.ownsLifetime &= ownsLifetime;
            }
            else
            {
                _instanceRegistrations[implementationType] =
                    (instance, new List<Type>(exposedTypes), ownsLifetime);
            }
        }

        public void ApplyRegistrations(IContainerBuilder builder)
        {
            foreach (var (implType, (lifetime, interfaces)) in _typedRegistrations)
            {
                var registrationBuilder = builder.Register(implType, lifetime);
                foreach (var iface in interfaces)
                    registrationBuilder.As(iface);
            }

            foreach (var (_, (instance, interfaces, _)) in _instanceRegistrations)
            {
                var provider = new RuntimeFlowInstanceProvider(instance);
                _instanceProviders.Add(provider);
                var registrationBuilder = new RuntimeFlowInstanceRegistrationBuilder(instance.GetType(), provider);
                foreach (var serviceType in interfaces)
                    registrationBuilder.As(serviceType);
                builder.Register(registrationBuilder);
            }

            foreach (var registration in _registrations)
                registration(builder);
        }

        public void DisposeOwnedRegisteredInstances(ref List<Exception>? disposeFailures)
        {
            var disposedInstances = new List<object>();

            foreach (var (instance, _, ownsLifetime) in _instanceRegistrations.Values.Reverse())
            {
                if (!ownsLifetime || instance is not IDisposable disposable)
                    continue;

                if (disposedInstances.Any(existing => ReferenceEquals(existing, instance)))
                    continue;

                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    AddDisposeFailure(ref disposeFailures, ex);
                }

                disposedInstances.Add(instance);
            }
        }

        public void ClearProviderInstances()
        {
            _instanceProviders.Clear();
        }

        public void ClearRegistrations()
        {
            _registrations.Clear();
            _registeredServiceTypes.Clear();
            _implementationTypes.Clear();
            _registeredInstances.Clear();
            _typedRegistrations.Clear();
            _instanceRegistrations.Clear();
        }

        private void AddTypedRegistration(Type implementationType, Lifetime lifetime, Type serviceType)
        {
            if (_typedRegistrations.TryGetValue(implementationType, out var existing))
            {
                if (!existing.interfaces.Contains(serviceType))
                    existing.interfaces.Add(serviceType);
            }
            else
            {
                _typedRegistrations[implementationType] = (lifetime, new List<Type> { serviceType });
            }
        }

        private static void AddDisposeFailure(ref List<Exception>? failures, Exception exception)
        {
            failures ??= new List<Exception>();
            failures.Add(exception);
        }
    }
}
