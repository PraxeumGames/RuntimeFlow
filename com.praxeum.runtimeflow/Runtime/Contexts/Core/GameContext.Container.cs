using System;
using System.Collections;
using System.Collections.Generic;
using VContainer;
using VContainer.Diagnostics;
using VContainer.Internal;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// GameContext is a lifecycle-native container: it owns the registration graph
    /// (<see cref="Registry"/>), the shared-instance cache, and the disposal of every
    /// instance it constructs or registers. There is no separate container object and
    /// no second ownership model to reconcile.
    /// </summary>
    public partial class GameContext
    {
        /// <summary>Identity: this context is its own resolver.</summary>
        public object ApplicationOrigin => this;

        /// <summary>
        /// Real VContainer diagnostics integration: registrations are traced when the
        /// registry is built and every resolve is traced with call depth and timing.
        /// A <c>null</c> assignment resets to a fresh collector so resolution never breaks.
        /// </summary>
        public DiagnosticsCollector Diagnostics
        {
            get => _diagnostics;
            set => _diagnostics = value ?? new($"GameContext-{Guid.NewGuid():N}");
        }

        /// <summary>
        /// Resolves a registration from this context's own graph. Registrations obtained
        /// through <see cref="GetRegistrationsForServiceType"/> always belong to the
        /// context that returned them, so cross-context resolution is not expected.
        /// </summary>
        public object Resolve(Registration registration)
        {
            if (registration == null) throw new ArgumentNullException(nameof(registration));
            if (!_initialized || _registry == null) throw new InvalidOperationException("Context not initialized");

            return DispatchToMainThread(
                () => ResolveRegistration(registration),
                $"resolve '{registration.ImplementationType?.FullName ?? registration.ImplementationType?.Name ?? "<unknown>"}'");
        }

        public bool TryResolve(Type serviceType, out object resolved)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (!_initialized || _registry == null)
            {
                resolved = null!;
                return false;
            }

            if (!TryFindRegistrationAcrossChain(serviceType, out _))
            {
                resolved = null!;
                return false;
            }

            resolved = Resolve(serviceType);
            return true;
        }

        public void Inject(object instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            if (!_initialized || _registry == null) throw new InvalidOperationException("Context not initialized");

            InjectorCache.GetOrBuild(instance.GetType()).Inject(instance, this, null);
        }

        public bool TryGetRegistration(Type type, out Registration registration)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            if (_registry != null && _registry.TryGet(type, out registration))
                return true;

            registration = null!;
            return false;
        }

        /// <summary>
        /// Scope creation is the builder's job in RuntimeFlow: scopes are created and
        /// activated by <c>GameContextBuilder</c>, not by individual contexts.
        /// </summary>
        IScopedObjectResolver IObjectResolver.CreateScope(Action<IContainerBuilder> installation)
        {
            throw new NotSupportedException(
                "GameContext owns scope lifecycle; scopes are created by GameContextBuilder and activated through the RuntimeFlow pipeline.");
        }

        private object ResolveRegistration(Registration registration)
        {
            return _diagnostics.TraceResolve(registration, ResolveRegistrationCore);
        }

        private object ResolveRegistrationCore(Registration registration)
        {
            switch (registration.Lifetime)
            {
                case Lifetime.Singleton:
                case Lifetime.Scoped:
                    return GetOrCreateSharedInstance(registration);
                default:
                    return registration.SpawnInstance(this);
            }
        }

        private object GetOrCreateSharedInstance(Registration registration)
        {
            if (_sharedInstances.TryGetValue(registration, out var existing))
                return existing;

            var instance = registration.SpawnInstance(this);
            _sharedInstances[registration] = instance;

            // Instance registrations are tracked when they are registered; only instances
            // the context constructs itself are tracked here.
            if (registration.Provider is not FixedInstanceProvider)
                TrackOwnedResolvedInstance(instance);

            return instance;
        }

        private bool TryFindRegistrationAcrossChain(Type serviceType, out Registration registration)
        {
            GameContext? current = this;
            while (current != null)
            {
                if (current._registry != null && current._registry.TryGet(serviceType, out registration))
                    return true;
                current = current._parent as GameContext;
            }

            registration = null!;
            return false;
        }

        private void TrackOwnedRegisteredInstance(object instance)
        {
            if (instance is not IDisposable)
                return;

            if (_ownedRegisteredInstancesLookup.Add(instance))
                _ownedRegisteredInstances.Add(instance);
        }

        private void TrackOwnedResolvedInstance(object instance)
        {
            if (instance is not IDisposable)
                return;

            if (_ownedResolvedInstancesLookup.Add(instance))
                _ownedResolvedInstances.Add(instance);
        }
    }
}