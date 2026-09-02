using System;
using System.Collections.Generic;
using VContainer;
using VContainer.Diagnostics;

namespace RuntimeFlow.Testing
{
    /// <summary>One replacement requested through <see cref="TestFlow.Override{T}"/>.</summary>
    internal sealed class ServiceOverride
    {
        public ServiceOverride(Type serviceType, Action<IContainerBuilder> register)
        {
            ServiceType = serviceType;
            Register = register;
        }

        /// <summary>The service type whose registrations are dropped and replaced.</summary>
        public Type ServiceType { get; }

        /// <summary>Registers the replacement into a scope that had a matching registration.</summary>
        public Action<IContainerBuilder> Register { get; }

        /// <summary>Names of the scopes where a registration exposing <see cref="ServiceType"/> was found.</summary>
        public HashSet<string> MatchedScopes { get; } = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// An <see cref="IContainerBuilder"/> wrapper handed to a production installer so a test can replace
    /// what it registers. VContainer resolves collections by merging every registration, so a plain
    /// "register last, win" override would leave the real service in <c>IReadOnlyList&lt;T&gt;</c>;
    /// instead this builder buffers the installer's registrations, inspects each one through the public
    /// <see cref="RegistrationBuilder.Build"/>, drops those exposing an overridden service type, forwards
    /// the remaining <see cref="RegistrationBuilder"/> objects untouched (lifetimes and parameters
    /// survive), and finally registers the replacements.
    /// </summary>
    internal sealed class OverridingContainerBuilder : IContainerBuilder
    {
        private readonly IContainerBuilder _inner;
        private readonly IReadOnlyList<ServiceOverride> _overrides;
        private readonly string _scope;
        private readonly List<RegistrationBuilder> _buffered = new List<RegistrationBuilder>();
        private readonly Dictionary<RegistrationBuilder, Registration?> _peeked =
            new Dictionary<RegistrationBuilder, Registration?>();

        public OverridingContainerBuilder(IContainerBuilder inner, IReadOnlyList<ServiceOverride> overrides, string scope)
        {
            _inner = inner;
            _overrides = overrides;
            _scope = scope;
        }

        /// <inheritdoc />
        public object ApplicationOrigin
        {
            get => _inner.ApplicationOrigin;
            set => _inner.ApplicationOrigin = value;
        }

        /// <inheritdoc />
        public DiagnosticsCollector Diagnostics
        {
            get => _inner.Diagnostics;
            set => _inner.Diagnostics = value;
        }

        /// <summary>Registrations already forwarded plus the ones still buffered here.</summary>
        public int Count => _inner.Count + _buffered.Count;

        /// <summary>Indexes the forwarded registrations first, then the buffered ones.</summary>
        public RegistrationBuilder this[int index]
        {
            get => index < _inner.Count ? _inner[index] : _buffered[index - _inner.Count];
            set
            {
                if (index < _inner.Count) _inner[index] = value;
                else _buffered[index - _inner.Count] = value;
            }
        }

        /// <inheritdoc />
        public T Register<T>(T registrationBuilder) where T : RegistrationBuilder
        {
            _buffered.Add(registrationBuilder);
            return registrationBuilder;
        }

        /// <inheritdoc />
        public void RegisterBuildCallback(Action<IObjectResolver> container) => _inner.RegisterBuildCallback(container);

        /// <summary>
        /// Answers from the buffered registrations first, so helpers such as
        /// <c>EntryPointsBuilder.EnsureDispatcherRegistered</c> do not register a second dispatcher.
        /// </summary>
        public bool Exists(Type type, bool includeInterfaceTypes = false, bool findParentScopes = false)
        {
            foreach (var builder in _buffered)
            {
                var registration = Peek(builder);
                if (registration == null) continue;
                if (registration.ImplementationType == type) return true;
                if (includeInterfaceTypes && Exposes(registration, type)) return true;
            }
            return _inner.Exists(type, includeInterfaceTypes, findParentScopes);
        }

        /// <summary>
        /// Forwards everything the installer registered except the overridden services, then registers
        /// the replacements for the overrides that matched something in this scope.
        /// </summary>
        public void Flush()
        {
            foreach (var builder in _buffered)
            {
                var registration = Peek(builder);
                var replaced = registration != null && MatchOverride(registration);
                if (!replaced) _inner.Register(builder);
            }
            _buffered.Clear();

            foreach (var candidate in _overrides)
            {
                if (candidate.MatchedScopes.Contains(_scope)) candidate.Register(_inner);
            }
        }

        private bool MatchOverride(Registration registration)
        {
            var replaced = false;
            foreach (var candidate in _overrides)
            {
                if (!Exposes(registration, candidate.ServiceType)) continue;
                candidate.MatchedScopes.Add(_scope);
                replaced = true;
            }
            return replaced;
        }

        private static bool Exposes(Registration registration, Type serviceType)
        {
            var exposed = registration.InterfaceTypes;
            if (exposed == null) return registration.ImplementationType == serviceType;
            for (var i = 0; i < exposed.Count; i++)
            {
                if (exposed[i] == serviceType) return true;
            }
            return false;
        }

        private Registration? Peek(RegistrationBuilder builder)
        {
            if (_peeked.TryGetValue(builder, out var cached)) return cached;

            Registration? registration;
            try
            {
                registration = builder.Build();
            }
            catch (Exception)
            {
                // A registration this harness cannot inspect (a component builder needing a
                // LifetimeScope, for example) is forwarded untouched and never overridden.
                registration = null;
            }

            _peeked[builder] = registration;
            return registration;
        }
    }
}
