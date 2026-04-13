using System;
using System.Linq;
using System.Reflection;

namespace VContainer
{
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
}
