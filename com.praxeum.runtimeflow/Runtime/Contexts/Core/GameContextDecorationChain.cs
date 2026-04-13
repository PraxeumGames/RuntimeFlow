using System;
using System.Collections.Generic;
using System.Linq;
using VContainer;

namespace RuntimeFlow.Contexts
{
    internal sealed class GameContextDecorationChain
    {
        private readonly List<(Type serviceType, Type decoratorType)> _decorations = new();
        private readonly Dictionary<Type, object> _decoratedInstances = new();

        public void Add(Type serviceType, Type decoratorType)
        {
            _decorations.Add((serviceType, decoratorType));
        }

        public bool TryGetDecoratedInstance(Type serviceType, out object instance)
        {
            return _decoratedInstances.TryGetValue(serviceType, out instance!);
        }

        public void ValidateRegistrations(Func<Type, bool> isRegistered)
        {
            foreach (var (serviceType, _) in _decorations)
            {
                if (!isRegistered(serviceType))
                {
                    throw new InvalidOperationException(
                        $"Cannot decorate service '{serviceType.FullName}' because it is not registered.");
                }
            }
        }

        public void Apply(IObjectResolver container)
        {
            if (_decorations.Count == 0)
                return;

            foreach (var (serviceType, decoratorType) in _decorations)
            {
                var inner = _decoratedInstances.TryGetValue(serviceType, out var previous)
                    ? previous
                    : container.Resolve(serviceType);

                var constructor = decoratorType.GetConstructors()
                    .OrderByDescending(candidate => candidate.GetParameters().Length)
                    .First();

                var parameters = constructor.GetParameters();
                var arguments = new object[parameters.Length];
                for (var i = 0; i < parameters.Length; i++)
                {
                    if (serviceType.IsAssignableFrom(parameters[i].ParameterType))
                        arguments[i] = inner;
                    else
                        arguments[i] = container.Resolve(parameters[i].ParameterType);
                }

                _decoratedInstances[serviceType] = constructor.Invoke(arguments)!;
            }
        }

        public void ClearResolvedInstances()
        {
            _decoratedInstances.Clear();
        }

        public void Clear()
        {
            _decoratedInstances.Clear();
            _decorations.Clear();
        }
    }
}
