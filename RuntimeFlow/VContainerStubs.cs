// Functional stubs for hadashiA's VContainer (Unity DI framework).
// These provide the same API surface so RuntimeFlow compiles and tests
// run under plain .NET without a Unity reference.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace VContainer
{
    public enum Lifetime
    {
        Singleton,
        Scoped,
        Transient
    }

    [AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class InjectAttribute : Attribute { }

    public interface IInstanceProvider
    {
        object SpawnInstance(IObjectResolver resolver);
    }

    public interface IObjectResolver : IDisposable
    {
        object Resolve(Type type);
        bool TryResolve(Type type, out object instance);
        IObjectResolver CreateScope(Action<IContainerBuilder> configuration);
    }

    public static class ObjectResolverExtensions
    {
        public static T Resolve<T>(this IObjectResolver resolver) => (T)resolver.Resolve(typeof(T));

        public static bool TryResolve<T>(this IObjectResolver resolver, out T instance)
            where T : class
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));

            if (resolver.TryResolve(typeof(T), out var raw) && raw is T typed)
            {
                instance = typed;
                return true;
            }

            instance = null!;
            return false;
        }

        public static bool TryGetRegistration(
            this IObjectResolver resolver,
            Type serviceType,
            out Registration? registration)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));

            if (resolver is Container container)
                return container.TryGetRegistration(serviceType, out registration);

            registration = null;
            return false;
        }

        public static object Resolve(this IObjectResolver resolver, Registration registration)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            if (registration == null) throw new ArgumentNullException(nameof(registration));

            foreach (var serviceType in registration.InterfaceTypes)
            {
                if (resolver.TryResolve(serviceType, out var resolved))
                    return resolved;
            }

            return resolver.Resolve(registration.ImplementationType);
        }
    }

    public interface IContainerBuilder
    {
        RegistrationBuilder Register(Type type, Lifetime lifetime);
        void Register(RegistrationBuilder registrationBuilder);
        IObjectResolver Build();
    }

    public sealed class Registration
    {
        public Type ImplementationType { get; }
        public Lifetime Lifetime { get; }
        public IReadOnlyList<Type> InterfaceTypes { get; }
        public IInstanceProvider Provider { get; }

        public Registration(Type implementationType, Lifetime lifetime, IReadOnlyList<Type> interfaceTypes, IInstanceProvider provider)
        {
            ImplementationType = implementationType;
            Lifetime = lifetime;
            InterfaceTypes = interfaceTypes;
            Provider = provider;
        }
    }

    public class RegistrationBuilder
    {
        public Type ImplementationType { get; }
        public Lifetime Lifetime { get; }
        internal List<Type> InterfaceTypes { get; } = new();

        public RegistrationBuilder(Type implementationType, Lifetime lifetime)
        {
            ImplementationType = implementationType;
            Lifetime = lifetime;
        }

        public RegistrationBuilder As<T>() => As(typeof(T));

        public RegistrationBuilder As(Type interfaceType)
        {
            if (!InterfaceTypes.Contains(interfaceType))
                InterfaceTypes.Add(interfaceType);
            return this;
        }

        public RegistrationBuilder AsSelf()
        {
            return As(ImplementationType);
        }

        public RegistrationBuilder AsImplementedInterfaces()
        {
            foreach (var iface in ImplementationType.GetInterfaces())
                As(iface);
            return this;
        }

        public virtual Registration Build()
        {
            var types = InterfaceTypes.Count > 0
                ? (IReadOnlyList<Type>)InterfaceTypes.ToList()
                : new List<Type> { ImplementationType };
            return new Registration(ImplementationType, Lifetime, types, new ReflectionInstanceProvider(ImplementationType));
        }
    }

    public sealed class VContainerException : Exception
    {
        public Type TargetType { get; }

        public VContainerException(Type targetType, string message)
            : base(message)
        {
            TargetType = targetType;
        }

        public VContainerException(Type targetType)
            : base($"No registration found for type '{targetType.FullName}'.")
        {
            TargetType = targetType;
        }
    }

    internal sealed class ReflectionInstanceProvider : IInstanceProvider
    {
        private readonly Type _implementationType;

        public ReflectionInstanceProvider(Type implementationType)
        {
            _implementationType = implementationType;
        }

        public object SpawnInstance(IObjectResolver resolver)
        {
            var constructor = SelectConstructor(_implementationType);
            if (constructor == null)
                return Activator.CreateInstance(_implementationType)!;

            var parameters = constructor.GetParameters();
            var args = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                args[i] = resolver.Resolve(parameters[i].ParameterType);
            }
            return constructor.Invoke(args);
        }

        private static ConstructorInfo? SelectConstructor(Type type)
        {
            var constructors = type.GetConstructors();
            if (constructors.Length == 0)
                return null;

            var injectCtor = constructors.FirstOrDefault(c =>
                c.GetCustomAttributes(typeof(InjectAttribute), true).Length > 0);

            return injectCtor ?? constructors
                .OrderByDescending(c => c.GetParameters().Length)
                .First();
        }
    }

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

    public static class ContainerBuilderExtensions
    {
        public static RegistrationBuilder Register<T>(this IContainerBuilder builder, Lifetime lifetime)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            return builder.Register(typeof(T), lifetime);
        }

        public static void RegisterBuildCallback(this IContainerBuilder builder, Action<IObjectResolver> callback)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            if (builder is ContainerBuilder concreteBuilder)
            {
                concreteBuilder.AddBuildCallback(callback);
                return;
            }

            throw new NotSupportedException(
                $"Build callbacks are not supported for builder type '{builder.GetType().FullName}'.");
        }

        public static void RegisterInstance<T>(this IContainerBuilder builder, T instance)
            where T : class
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (instance == null) throw new ArgumentNullException(nameof(instance));

            var registrationBuilder = new InstanceRegistrationBuilder(typeof(T), instance).As(typeof(T));
            builder.Register(registrationBuilder);
        }

        public static void RegisterInstance(this IContainerBuilder builder, object instance)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (instance == null) throw new ArgumentNullException(nameof(instance));

            var implementationType = instance.GetType();
            var registrationBuilder = new InstanceRegistrationBuilder(implementationType, instance)
                .As(implementationType);
            builder.Register(registrationBuilder);
        }

        private sealed class InstanceRegistrationBuilder : RegistrationBuilder
        {
            private readonly object _instance;

            public InstanceRegistrationBuilder(Type implementationType, object instance)
                : base(implementationType, Lifetime.Singleton)
            {
                _instance = instance ?? throw new ArgumentNullException(nameof(instance));
            }

            public override Registration Build()
            {
                var types = InterfaceTypes.Count > 0
                    ? (IReadOnlyList<Type>)InterfaceTypes.ToList()
                    : new List<Type> { ImplementationType };

                return new Registration(
                    ImplementationType,
                    Lifetime.Singleton,
                    types,
                    new FixedInstanceProvider(_instance));
            }
        }

        private sealed class FixedInstanceProvider : IInstanceProvider
        {
            private readonly object _instance;

            public FixedInstanceProvider(object instance)
            {
                _instance = instance ?? throw new ArgumentNullException(nameof(instance));
            }

            public object SpawnInstance(IObjectResolver resolver)
            {
                return _instance;
            }
        }
    }
}

namespace VContainer.Unity
{
    using System;
    using VContainer;

    public interface IInitializable
    {
        void Initialize();
    }

    public interface IStartable
    {
        void Start();
    }

    public interface IScopedObjectResolver : IObjectResolver
    {
        IObjectResolver? Parent { get; }
    }

    public sealed class LifetimeScope
    {
        public IObjectResolver? Container { get; set; }
    }
}

namespace UnityEngine
{
    public class Object
    {
    }

    public class Component : Object
    {
        public T? GetComponentInChildren<T>(bool includeInactive = false)
            where T : class
        {
            return default;
        }
    }
}
