using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using VContainer;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Registration-phase store for a <see cref="GameContext"/>. It only collects what is
    /// registered before the context builds its registry; it owns no instances, tracks no
    /// disposal, and knows nothing about the runtime container. Instance ownership and
    /// teardown are the GameContext's single responsibility.
    /// </summary>
    internal sealed class GameContextRegistrationStore
    {
        private readonly List<Action<IContainerBuilder>> _registrations = new();
        private readonly HashSet<Type> _registeredServiceTypes = new();
        private readonly Dictionary<Type, Type> _implementationTypes = new();
        private readonly Dictionary<Type, object> _registeredInstances = new();
        private readonly Dictionary<Type, (Lifetime lifetime, List<Type> interfaces)> _typedRegistrations = new();
        private readonly Dictionary<Type, (object instance, List<Type> interfaces)> _instanceRegistrations = new();

        public IReadOnlyCollection<Type> RegisteredServiceTypes => _registeredServiceTypes;

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

        public bool IsRegistered(Type serviceType, bool includeInterfaceTypes)
        {
            if (_registeredServiceTypes.Contains(serviceType))
                return true;

            if (includeInterfaceTypes && _implementationTypes.ContainsKey(serviceType))
                return true;

            return false;
        }

        public bool TryGetImplementationType(
            Type serviceType,
            [MaybeNullWhen(false)] out Type implementationType)
        {
            return _implementationTypes.TryGetValue(serviceType, out implementationType);
        }

        public bool TryGetRegisteredInstance(Type serviceType, [MaybeNullWhen(false)] out object instance)
        {
            return _registeredInstances.TryGetValue(serviceType, out instance);
        }

        public void RegisterInstance(
            Type implementationType,
            object instance,
            IReadOnlyCollection<Type> serviceTypes)
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
                if (!serviceType.IsAssignableFrom(implementationType) && serviceType != implementationType)
                {
                    throw new InvalidOperationException(
                        $"Service type {serviceType.Name} is not assignable from instance type {implementationType.Name}.");
                }
            }

            foreach (var serviceType in exposedTypes)
            {
                _registeredServiceTypes.Add(serviceType);
                _implementationTypes[serviceType] = implementationType;
                _registeredInstances[serviceType] = instance;
            }

            if (_instanceRegistrations.TryGetValue(implementationType, out var existing))
            {
                var interfaces = existing.interfaces;
                foreach (var t in exposedTypes)
                {
                    if (!interfaces.Contains(t))
                        interfaces.Add(t);
                }

                // The entry must be written back: tuples are value types and TryGetValue
                // returns a copy, so mutating it would silently drop the replacement.
                _instanceRegistrations[implementationType] = (instance, interfaces);
            }
            else
            {
                _instanceRegistrations[implementationType] = (instance, new List<Type>(exposedTypes));
            }
        }

        public void ApplyRegistrations(RuntimeFlowContainerBuilder builder)
        {
            foreach (var (implType, (lifetime, interfaces)) in _typedRegistrations)
            {
                var registrationBuilder = builder.Register(implType, lifetime);
                foreach (var iface in interfaces)
                    registrationBuilder.As(iface);
            }

            foreach (var (implType, (instance, interfaces)) in _instanceRegistrations)
            {
                var registrationBuilder = new FixedInstanceRegistrationBuilder(
                    implType,
                    new FixedInstanceProvider(instance));
                if (interfaces.Count == 0)
                {
                    registrationBuilder.AsSelf();
                }
                else
                {
                    foreach (var serviceType in interfaces)
                        registrationBuilder.As(serviceType);
                }

                builder.Register(registrationBuilder);
            }

            foreach (var registration in _registrations)
                registration(builder);
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
                // Last registration wins: re-registering an implementation type updates
                // its lifetime rather than silently keeping the first one. Tuples are
                // value types, so the entry must be written back explicitly.
                var interfaces = existing.interfaces;
                if (!interfaces.Contains(serviceType))
                    interfaces.Add(serviceType);
                _typedRegistrations[implementationType] = (lifetime, interfaces);
            }
            else
            {
                _typedRegistrations[implementationType] = (lifetime, new List<Type> { serviceType });
            }
        }
    }
}