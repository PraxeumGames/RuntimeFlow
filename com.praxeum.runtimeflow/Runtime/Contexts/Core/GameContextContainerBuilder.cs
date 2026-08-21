using System;
using System.Collections.Generic;
using VContainer;
using VContainer.Diagnostics;
using VContainer.Internal;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Builds a VContainer <see cref="Registry"/> for a GameContext while keeping the
    /// context itself as the only resolver: the registry owns the graph, the context
    /// owns the lifecycle. Unlike VContainer's <see cref="Container"/>, no disposable
    /// tracking, shared-instance cache, or resolution-after-dispose behavior is
    /// baked in here — GameContext implements those natively.
    /// </summary>
    internal sealed class RuntimeFlowContainerBuilder : ContainerBuilder
    {
        public Registry BuildRegistry(IObjectResolver selfResolver, DiagnosticsCollector? diagnostics)
        {
            if (selfResolver == null) throw new ArgumentNullException(nameof(selfResolver));

            var registrations = new List<Registration>(Count + 1);
            for (var i = 0; i < Count; i++)
            {
                var registrationBuilder = this[i];
                diagnostics?.TraceRegister(new RegisterInfo(registrationBuilder));
                var registration = registrationBuilder.Build();
                diagnostics?.TraceBuild(registrationBuilder, registration);
                registrations.Add(registration);
            }

            registrations.Add(new Registration(
                typeof(IObjectResolver),
                Lifetime.Singleton,
                null,
                new GameContextSelfProvider(selfResolver)));

            var registry = Registry.Build(registrations.ToArray());
            EmitCallbacks(selfResolver);
            return registry;
        }
    }

    /// <summary>
    /// Returns the owning GameContext when a service injects <see cref="IObjectResolver"/>.
    /// The context is the container in RuntimeFlow, so self-resolution is identity.
    /// </summary>
    internal sealed class GameContextSelfProvider : IInstanceProvider
    {
        private readonly IObjectResolver _resolver;

        public GameContextSelfProvider(IObjectResolver resolver)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        }

        public object SpawnInstance(IObjectResolver resolver) => _resolver;
    }

    /// <summary>
    /// Stateless provider for pre-constructed instances: it always returns the registered
    /// instance and never tracks, spawns, or releases anything. Instance ownership is the
    /// context's concern, not the provider's.
    /// </summary>
    internal sealed class FixedInstanceProvider : IInstanceProvider
    {
        private readonly object _instance;

        public FixedInstanceProvider(object instance)
        {
            _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        }

        public object SpawnInstance(IObjectResolver resolver) => _instance;
    }

    internal sealed class FixedInstanceRegistrationBuilder : RegistrationBuilder
    {
        private readonly IInstanceProvider _provider;

        public FixedInstanceRegistrationBuilder(Type implementationType, IInstanceProvider provider)
            : base(implementationType, Lifetime.Singleton)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        public override Registration Build()
        {
            return new Registration(
                ImplementationType,
                Lifetime,
                InterfaceTypes,
                _provider);
        }
    }
}