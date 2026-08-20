using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using VContainer;
using VContainer.Unity;

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
            [MaybeNullWhen(false)] out Type implementationType)
        {
            if (_implementationTypes.TryGetValue(serviceType, out implementationType))
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

            implementationType = null;
            return false;
        }

        public IReadOnlyList<Registration> GetRegistrationsForServiceType(
            Type serviceType,
            bool initialized,
            IObjectResolver? container)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));

            var registrations = new List<Registration>();

            if (initialized && container != null)
            {
                var collectionType = typeof(IReadOnlyList<>).MakeGenericType(serviceType);
                if (container.TryGetRegistration(collectionType, out var collectionRegistration)
                    && collectionRegistration?.Provider is IEnumerable collectionProvider)
                {
                    registrations.AddRange(collectionProvider
                        .Cast<object>()
                        .OfType<Registration>());
                }

                if (container.TryGetRegistration(serviceType, out var registration) && registration != null)
                {
                    registrations.Add(registration);
                }

                var parentRegistrations = GetParentRegistrationsForServiceType(serviceType, container);
                if (parentRegistrations.Count > 0)
                {
                    registrations = registrations
                        .Where(registration => !parentRegistrations.Contains(registration))
                        .ToList();
                }
            }

            return registrations
                .GroupBy(registration => registration.ImplementationType)
                .Select(group => group.First())
                .ToArray();
        }

        private static HashSet<Registration> GetParentRegistrationsForServiceType(
            Type serviceType,
            IObjectResolver container)
        {
            var registrations = new HashSet<Registration>();
            if (container is not IScopedObjectResolver scopedResolver || scopedResolver.Parent == null)
            {
                return registrations;
            }

            var parent = scopedResolver.Parent;
            var collectionType = typeof(IReadOnlyList<>).MakeGenericType(serviceType);
            if (parent.TryGetRegistration(collectionType, out var collectionRegistration)
                && collectionRegistration?.Provider is IEnumerable collectionProvider)
            {
                foreach (var registration in collectionProvider.Cast<object>().OfType<Registration>())
                {
                    registrations.Add(registration);
                }
            }

            if (parent.TryGetRegistration(serviceType, out var directRegistration) && directRegistration != null)
            {
                registrations.Add(directRegistration);
            }

            return registrations;
        }

        public bool TryGetRegisteredInstance(Type serviceType, [MaybeNullWhen(false)] out object instance)
        {
            return _registeredInstances.TryGetValue(serviceType, out instance);
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
                // Replacing a previously registered instance: release the previous one if it
                // was owned by this scope, otherwise it would leak (it is no longer reachable
                // through any registration). Instances that were resolved through the container
                // are tracked by VContainer and must be left to the container for disposal.
                if (existing.ownsLifetime
                    && existing.instance is IDisposable replacedDisposable
                    && !ReferenceEquals(existing.instance, instance)
                    && !WasSpawnedByContainer(existing.instance))
                {
                    try
                    {
                        replacedDisposable.Dispose();
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"Failed to dispose replaced instance registration for '{implementationType.Name}'.",
                            ex);
                    }
                }

                var interfaces = existing.interfaces;
                foreach (var t in exposedTypes)
                {
                    if (!interfaces.Contains(t))
                        interfaces.Add(t);
                }

                // Any registration claiming ownership keeps ownership: the instance is disposed
                // when the scope is torn down if at least one registration declared it owned.
                // The entry must be written back: tuples are value types and TryGetValue
                // returns a copy, so mutating it would silently drop the replacement.
                _instanceRegistrations[implementationType] =
                    (instance, interfaces, existing.ownsLifetime || ownsLifetime);
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
            var spawnedInstances = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var provider in _instanceProviders)
            {
                if (provider.WasSpawned)
                    spawnedInstances.Add(provider.Instance);
            }

            foreach (var (instance, _, ownsLifetime) in _instanceRegistrations.Values.Reverse())
            {
                if (!ownsLifetime || instance is not IDisposable disposable)
                    continue;

                // Instances that were resolved through the container are tracked and disposed
                // by the VContainer container itself; disposing them again here would be a
                // double-dispose. Only never-resolved owned instances need manual disposal.
                if (spawnedInstances.Contains(instance))
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

        private bool WasSpawnedByContainer(object instance)
        {
            foreach (var provider in _instanceProviders)
            {
                if (provider.WasSpawned && ReferenceEquals(provider.Instance, instance))
                    return true;
            }

            return false;
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

        private static void AddDisposeFailure(ref List<Exception>? failures, Exception exception)
        {
            failures ??= new List<Exception>();
            failures.Add(exception);
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new();

            public new bool Equals(object? x, object? y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
