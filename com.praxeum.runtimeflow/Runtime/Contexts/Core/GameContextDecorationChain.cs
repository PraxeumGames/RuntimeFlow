using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using VContainer;

namespace RuntimeFlow.Contexts
{
    internal sealed class GameContextDecorationChain
    {
        private delegate object DecoratorFactory(IObjectResolver container, object innerInstance);

        private static readonly ConcurrentDictionary<(Type serviceType, Type decoratorType), DecoratorFactory> FactoryCache = new();
        private readonly List<(Type serviceType, Type decoratorType)> _decorations = new();
        private readonly Dictionary<Type, object> _decoratedInstances = new();

        public void Add(Type serviceType, Type decoratorType)
        {
            for (var i = 0; i < _decorations.Count; i++)
            {
                if (_decorations[i].serviceType == serviceType && _decorations[i].decoratorType == decoratorType)
                    return;
            }
            _decorations.Add((serviceType, decoratorType));
        }

        public bool TryGetDecoratedInstance(Type serviceType, [MaybeNullWhen(false)] out object instance)
        {
            return _decoratedInstances.TryGetValue(serviceType, out instance);
        }

        public void ValidateRegistrations(Func<Type, bool> isRegistered)
        {
            for (var i = 0; i < _decorations.Count; i++)
            {
                var serviceType = _decorations[i].serviceType;
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

            for (var d = 0; d < _decorations.Count; d++)
            {
                var (serviceType, decoratorType) = _decorations[d];
                var inner = _decoratedInstances.TryGetValue(serviceType, out var previous)
                    ? previous
                    : container.Resolve(serviceType);

                var factory = FactoryCache.GetOrAdd((serviceType, decoratorType), CreateDecoratorFactory);
                _decoratedInstances[serviceType] = factory(container, inner);
            }
        }

        private static DecoratorFactory CreateDecoratorFactory((Type serviceType, Type decoratorType) key)
        {
            var (serviceType, decoratorType) = key;
            var ctor = SelectDecoratorConstructor(decoratorType);
            var parameters = ctor.GetParameters();

            try
            {
                var containerParam = Expression.Parameter(typeof(IObjectResolver), "container");
                var innerParam = Expression.Parameter(typeof(object), "inner");
                var resolveMethod = typeof(IObjectResolver).GetMethod(nameof(IObjectResolver.Resolve), new[] { typeof(Type) })!;

                var argumentExpressions = new Expression[parameters.Length];
                for (var i = 0; i < parameters.Length; i++)
                {
                    var paramType = parameters[i].ParameterType;
                    if (serviceType.IsAssignableFrom(paramType))
                    {
                        argumentExpressions[i] = Expression.Convert(innerParam, paramType);
                    }
                    else
                    {
                        var resolveCall = Expression.Call(containerParam, resolveMethod, Expression.Constant(paramType));
                        argumentExpressions[i] = Expression.Convert(resolveCall, paramType);
                    }
                }

                var newExpression = Expression.New(ctor, argumentExpressions);
                var lambda = Expression.Lambda<DecoratorFactory>(Expression.Convert(newExpression, typeof(object)), containerParam, innerParam);
                return lambda.Compile();
            }
            catch
            {
                // Fallback for strict AOT / platforms where Expression.Compile is unavailable
                return (container, inner) =>
                {
                    var args = new object[parameters.Length];
                    for (var i = 0; i < parameters.Length; i++)
                    {
                        var paramType = parameters[i].ParameterType;
                        if (serviceType.IsAssignableFrom(paramType))
                            args[i] = inner;
                        else
                            args[i] = container.Resolve(paramType);
                    }
                    return ctor.Invoke(args)!;
                };
            }
        }

        private static ConstructorInfo SelectDecoratorConstructor(Type decoratorType)
        {
            var constructors = decoratorType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            if (constructors.Length == 0)
                throw new InvalidOperationException($"Decorator '{decoratorType.FullName}' has no public constructors.");

            ConstructorInfo? selected = null;
            var maxParamCount = -1;

            for (var i = 0; i < constructors.Length; i++)
            {
                var ctor = constructors[i];
                if (ctor.GetCustomAttributes(typeof(VContainer.InjectAttribute), inherit: false).Length > 0)
                    return ctor;

                var paramCount = ctor.GetParameters().Length;
                if (paramCount > maxParamCount)
                {
                    maxParamCount = paramCount;
                    selected = ctor;
                }
            }

            return selected ?? constructors[0];
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
