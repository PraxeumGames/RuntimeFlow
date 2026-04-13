using System;
using System.Collections.Generic;
using System.Linq;

namespace VContainer
{
    public sealed class ContainerBuilder : IContainerBuilder
    {
        private readonly List<RegistrationBuilder> _builders = new();
        private readonly List<(Type serviceType, Type decoratorType)> _decorations = new();
        private readonly List<Action<IObjectResolver>> _buildCallbacks = new();

        public RegistrationBuilder Register(Type type, Lifetime lifetime)
        {
            var rb = new RegistrationBuilder(type, lifetime);
            _builders.Add(rb);
            return rb;
        }

        public void Register(RegistrationBuilder registrationBuilder)
        {
            if (registrationBuilder == null) throw new ArgumentNullException(nameof(registrationBuilder));
            _builders.Add(registrationBuilder);
        }

        public void Decorate(Type serviceType, Type decoratorType)
        {
            _decorations.Add((serviceType, decoratorType));
        }

        public IObjectResolver Build()
        {
            var registrations = new List<Registration>();
            foreach (var rb in _builders)
                registrations.Add(rb.Build());
            var container = new Container(registrations, _decorations);
            foreach (var callback in _buildCallbacks)
                callback(container);

            return container;
        }

        internal List<Registration> BuildRegistrations()
        {
            var registrations = new List<Registration>();
            foreach (var rb in _builders)
                registrations.Add(rb.Build());
            return registrations;
        }

        internal List<(Type serviceType, Type decoratorType)> BuildDecorations() =>
            new List<(Type, Type)>(_decorations);

        internal void AddBuildCallback(Action<IObjectResolver> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            _buildCallbacks.Add(callback);
        }
    }

    internal sealed class Container : IObjectResolver, VContainer.Unity.IScopedObjectResolver
    {
        private readonly Container? _parent;
        private readonly Dictionary<Type, Registration> _ownRegistrations = new();
        private readonly Dictionary<Type, object> _singletonCache;
        private readonly Dictionary<Type, object> _scopedCache = new();
        private readonly List<(Type serviceType, Type decoratorType)> _decorations;
        private readonly Dictionary<Type, object> _decoratedCache = new();
        private bool _disposed;

        internal Container(List<Registration> registrations, List<(Type, Type)> decorations)
        {
            _parent = null;
            _singletonCache = new Dictionary<Type, object>();
            _decorations = decorations;
            IndexRegistrations(registrations);
            ApplyDecorations();
        }

        private Container(Container parent, List<Registration> additionalRegistrations, List<(Type, Type)> decorations)
        {
            _parent = parent;
            _singletonCache = new Dictionary<Type, object>();
            _decorations = decorations;
            IndexRegistrations(additionalRegistrations);
            ApplyDecorations();
        }

        private void IndexRegistrations(List<Registration> registrations)
        {
            foreach (var reg in registrations)
            {
                if (reg.InterfaceTypes.Count > 0)
                {
                    foreach (var iface in reg.InterfaceTypes)
                        _ownRegistrations[iface] = reg;
                }
                else
                {
                    _ownRegistrations[reg.ImplementationType] = reg;
                }
            }
        }

        public IObjectResolver? Parent => _parent;

        public bool TryGetRegistration(Type type, out Registration? registration)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            if (_ownRegistrations.TryGetValue(type, out var own))
            {
                registration = own;
                return true;
            }

            if (_parent != null)
                return _parent.TryGetRegistration(type, out registration);

            registration = null;
            return false;
        }

        public object Resolve(Type type)
        {
            if (_decoratedCache.TryGetValue(type, out var decorated))
                return decorated;

            if (_ownRegistrations.TryGetValue(type, out var reg))
                return ResolveRegistration(reg);

            if (_parent != null)
                return _parent.Resolve(type);

            throw new VContainerException(type);
        }

        public bool TryResolve(Type type, out object instance)
        {
            try
            {
                instance = Resolve(type);
                return true;
            }
            catch (VContainerException)
            {
                instance = null!;
                return false;
            }
        }

        public IObjectResolver CreateScope(Action<IContainerBuilder> configuration)
        {
            var builder = new ContainerBuilder();
            configuration(builder);
            return new Container(this, builder.BuildRegistrations(), builder.BuildDecorations());
        }

        private object ResolveRegistration(Registration reg)
        {
            switch (reg.Lifetime)
            {
                case Lifetime.Transient:
                    return reg.Provider.SpawnInstance(this);

                case Lifetime.Singleton:
                    lock (_singletonCache)
                    {
                        if (!_singletonCache.TryGetValue(reg.ImplementationType, out var singleton))
                        {
                            singleton = reg.Provider.SpawnInstance(this);
                            _singletonCache[reg.ImplementationType] = singleton;
                        }
                        return singleton;
                    }

                case Lifetime.Scoped:
                    if (!_scopedCache.TryGetValue(reg.ImplementationType, out var scoped))
                    {
                        scoped = reg.Provider.SpawnInstance(this);
                        _scopedCache[reg.ImplementationType] = scoped;
                    }
                    return scoped;

                default:
                    throw new InvalidOperationException($"Unknown lifetime: {reg.Lifetime}");
            }
        }

        private void ApplyDecorations()
        {
            foreach (var (serviceType, decoratorType) in _decorations)
            {
                if (!_ownRegistrations.ContainsKey(serviceType))
                    continue;

                var inner = _decoratedCache.TryGetValue(serviceType, out var prev)
                    ? prev
                    : Resolve(serviceType);

                var ctor = decoratorType.GetConstructors()
                    .OrderByDescending(c => c.GetParameters().Length).First();
                var parameters = ctor.GetParameters();
                var args = new object[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (serviceType.IsAssignableFrom(parameters[i].ParameterType))
                        args[i] = inner;
                    else
                        args[i] = Resolve(parameters[i].ParameterType);
                }
                _decoratedCache[serviceType] = ctor.Invoke(args)!;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var instance in _scopedCache.Values)
            {
                if (instance is IDisposable disposable)
                    disposable.Dispose();
            }
            _scopedCache.Clear();

            if (_parent == null)
            {
                foreach (var instance in _singletonCache.Values)
                {
                    if (instance is IDisposable disposable)
                        disposable.Dispose();
                }
                _singletonCache.Clear();
            }
        }
    }
}
