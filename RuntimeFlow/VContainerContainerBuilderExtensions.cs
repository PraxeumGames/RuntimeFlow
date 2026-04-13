using System;
using System.Collections.Generic;
using System.Linq;

namespace VContainer
{
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
