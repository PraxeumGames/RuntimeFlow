using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private readonly Dictionary<Type, List<Type>> _layersByServiceType = new();
        private readonly Dictionary<Type, int> _appliedLayerCount = new();

        public void Add(Type serviceType, Type decoratorType)
        {
            for (var i = 0; i < _decorations.Count; i++)
            {
                if (_decorations[i].serviceType == serviceType && _decorations[i].decoratorType == decoratorType)
                    return;
            }
            _decorations.Add((serviceType, decoratorType));
        }

        public bool HasDecorationsFor(Type serviceType)
        {
            for (var i = 0; i < _decorations.Count; i++)
                if (_decorations[i].serviceType == serviceType)
                    return true;
            return false;
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

        /// <summary>
        /// Materializes decorated instances lazily at resolve time (main thread by the
        /// resolution contract) instead of eagerly at context-initialize time, which the
        /// pipeline may run on worker threads. Decoration layers apply in registration
        /// order, each wrapping the previous result.
        /// </summary>
        public object GetOrMaterializeDecorated(Type serviceType, IObjectResolver container, Func<Type, object> resolveUndecorated)
        {
            var layers = GetLayers(serviceType);
            var applied = _appliedLayerCount.TryGetValue(serviceType, out var stored) ? stored : 0;

            var current = applied == 0
                ? resolveUndecorated(serviceType)
                : _decoratedInstances[serviceType];

            for (var layer = applied; layer < layers.Count; layer++)
            {
                var factory = FactoryCache.GetOrAdd((serviceType, layers[layer]), CreateDecoratorFactory);
                current = factory(container, current);
                _decoratedInstances[serviceType] = current;
            }

            _appliedLayerCount[serviceType] = layers.Count;
            return current;
        }

        private List<Type> GetLayers(Type serviceType)
        {
            if (_layersByServiceType.TryGetValue(serviceType, out var cached))
                return cached;
            var layers = new List<Type>();
            for (var i = 0; i < _decorations.Count; i++)
                if (_decorations[i].serviceType == serviceType)
                    layers.Add(_decorations[i].decoratorType);
            _layersByServiceType[serviceType] = layers;
            return layers;
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
            _appliedLayerCount.Clear();
        }

        public void Clear()
        {
            _decoratedInstances.Clear();
            _appliedLayerCount.Clear();
            _layersByServiceType.Clear();
            _decorations.Clear();
        }
    }
}
