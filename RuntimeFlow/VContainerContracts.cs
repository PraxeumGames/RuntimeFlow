using System;
using System.Collections.Generic;
using System.Linq;

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
}
