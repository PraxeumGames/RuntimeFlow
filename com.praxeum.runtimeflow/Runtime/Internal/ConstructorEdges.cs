using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Logging;
using VContainer;

namespace RuntimeFlow.Internal
{
    /// <summary>
    /// Reads the injectable constructor of a type the way VContainer does and classifies its parameters
    /// into ordering edges, deliberately lazy parameters, and framework parameters that are ignored.
    /// </summary>
    internal static class ConstructorEdges
    {
        // RuntimeFlow builds graphs on the Unity main thread only, so a plain dictionary is enough;
        // a concurrent one would only advertise a threading model the rest of the framework does not have.
        private static readonly Dictionary<Type, ParameterInfo[]> Cache = new Dictionary<Type, ParameterInfo[]>();

        private static readonly Type[] IgnoredTypes =
        {
            typeof(IObjectResolver),
            typeof(IScopedObjectResolver),
            typeof(IContainerBuilder),
            typeof(RuntimeFlowOptions),
            typeof(RuntimeFlowHost),
            typeof(ScopeRun),
            typeof(InitContext),
            typeof(ILogger),
            typeof(object),
            typeof(string)
        };

        /// <summary>Parameters of the constructor VContainer would inject, cached per type.</summary>
        public static ParameterInfo[] Parameters(Type type)
        {
            if (Cache.TryGetValue(type, out var cached)) return cached;

            var parameters = Select(type);
            Cache.Add(type, parameters);
            return parameters;
        }

        /// <summary>True when the parameter deliberately breaks an edge (Func, Lazy, ILazy).</summary>
        public static bool IsLazy(Type type)
        {
            if (!type.IsGenericType) return false;
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(Lazy<>)) return true;
            var name = definition.FullName ?? definition.Name;
            return name.StartsWith("System.Func`", StringComparison.Ordinal)
                   || definition.Name.StartsWith("ILazy`", StringComparison.Ordinal)
                   || definition.Name.StartsWith("LazyDependency`", StringComparison.Ordinal);
        }

        /// <summary>True when the parameter can never denote an initializable service.</summary>
        public static bool IsIgnored(Type type)
        {
            if (type.IsPrimitive || type.IsEnum || type == typeof(decimal)) return true;
            foreach (var ignored in IgnoredTypes)
            {
                if (type == ignored) return true;
            }
            return false;
        }

        /// <summary>Element type of a collection parameter (the barrier idiom), or null.</summary>
        public static Type? ElementType(Type type)
        {
            if (type.IsArray) return type.GetElementType();
            if (!type.IsGenericType) return null;

            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(IEnumerable<>)
                || definition == typeof(IReadOnlyList<>)
                || definition == typeof(IReadOnlyCollection<>)
                || definition == typeof(IList<>)
                || definition == typeof(ICollection<>)
                || definition == typeof(List<>))
            {
                return type.GetGenericArguments()[0];
            }
            return null;
        }

        private static ParameterInfo[] Select(Type type)
        {
            ConstructorInfo? chosen = null;
            var annotated = 0;
            var maxParameters = -1;

            foreach (var constructor in type.GetConstructors(
                         BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (constructor.IsDefined(typeof(InjectAttribute), false))
                {
                    if (++annotated > 1) return Array.Empty<ParameterInfo>();
                    chosen = constructor;
                }
                else if (annotated <= 0)
                {
                    var parameters = constructor.GetParameters();
                    if (parameters.Length > maxParameters)
                    {
                        chosen = constructor;
                        maxParameters = parameters.Length;
                    }
                }
            }

            return chosen?.GetParameters() ?? Array.Empty<ParameterInfo>();
        }
    }
}
