using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using VContainer;

namespace RuntimeFlow.Internal
{
    /// <summary>One member VContainer injects: a constructor or method parameter, a field or a property.</summary>
    internal readonly struct InjectionPoint
    {
        public InjectionPoint(Type type, string name, string origin, (MemberInfo Member, int Position) key, bool isConstructorParameter,
            bool keyed = false, object? serviceKey = null)
        {
            Type = type;
            Name = name;
            Origin = origin;
            Key = key;
            IsConstructorParameter = isConstructorParameter;
            Keyed = keyed;
            ServiceKey = serviceKey;
        }

        /// <summary>True for a parameter of the injected constructor; false for [Inject] methods, fields, properties.</summary>
        public bool IsConstructorParameter { get; }

        /// <summary>True when the member carries VContainer's <c>[Key(…)]</c> (VContainer 1.19+).</summary>
        public bool Keyed { get; }

        /// <summary>The key of <c>[Key(…)]</c>, which selects the keyed registration VContainer injects.</summary>
        public object? ServiceKey { get; }

        /// <summary>The injected type as declared.</summary>
        public Type Type { get; }

        /// <summary>Parameter, field or property name; what VContainer matches WithParameter(name) against.</summary>
        public string Name { get; }

        /// <summary>Edge origin shown by diagnostics, for example "ctor: IFoo foo" or "[Inject] Construct(IFoo foo)".</summary>
        public string Origin { get; }

        /// <summary>Identity used to skip the same member reached through two inspected types.</summary>
        public (MemberInfo Member, int Position) Key { get; }
    }

    /// <summary>
    /// Reads the injectable constructor of a type the way VContainer does and classifies its parameters
    /// into ordering edges, deliberately lazy parameters, and framework parameters that are ignored.
    /// </summary>
    internal static class ConstructorEdges
    {
        // The cache outlives any single run and callers' tokens may be cancelled from any thread,
        // so the container itself is concurrent even though graph building is single-threaded.
        private static readonly ConcurrentDictionary<Type, Selection> Cache =
            new ConcurrentDictionary<Type, Selection>();

        private static readonly ConcurrentDictionary<Type, IReadOnlyList<InjectionPoint>> Points =
            new ConcurrentDictionary<Type, IReadOnlyList<InjectionPoint>>();

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
        public static ParameterInfo[] Parameters(Type type) => Cache.GetOrAdd(type, Select).Parameters;

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

        /// <summary>
        /// Element type of a collection parameter (the barrier idiom), or null. Only the two shapes VContainer
        /// resolves count: <c>IEnumerable&lt;T&gt;</c> and <c>IReadOnlyList&lt;T&gt;</c>; any other collection
        /// type is not resolvable and fails construction instead.
        /// </summary>
        public static Type? ElementType(Type type)
        {
            if (!type.IsGenericType) return null;
            var definition = type.GetGenericTypeDefinition();
            return definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyList<>)
                ? type.GetGenericArguments()[0]
                : null;
        }

        /// <summary><c>ContainerLocal&lt;T&gt;</c> resolves T from the resolving scope; the dependency is T.</summary>
        public static Type Unwrap(Type type)
        {
            if (!type.IsGenericType) return type;
            // ContainerLocal lives in VContainer's internal namespace, which the package does not reference.
            var definition = type.GetGenericTypeDefinition();
            return definition.Name == "ContainerLocal`1" && definition.Assembly == typeof(IObjectResolver).Assembly
                ? type.GetGenericArguments()[0]
                : type;
        }

        /// <summary>
        /// Everything VContainer injects into <paramref name="type"/>, in the order its injector does:
        /// the parameters of the selected constructor, then [Inject] methods, fields and properties from the
        /// type up its base chain (mirroring VContainer's TypeAnalyzer, including how it skips duplicates).
        /// </summary>
        public static IReadOnlyList<InjectionPoint> InjectionPoints(Type type) => Points.GetOrAdd(type, AnalyzePoints);

        private static IReadOnlyList<InjectionPoint> AnalyzePoints(Type type)
        {
            var points = new List<InjectionPoint>();
            foreach (var parameter in Parameters(type))
            {
                var keyed = KeyOf(parameter, out var key);
                points.Add(new InjectionPoint(parameter.ParameterType, parameter.Name ?? string.Empty,
                    $"ctor: {Fmt.Type(parameter.ParameterType)} {parameter.Name}",
                    (parameter.Member, parameter.Position), true, keyed, key));
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var methods = new List<MethodInfo>();
            var fields = new List<FieldInfo>();
            var properties = new List<PropertyInfo>();
            var current = type;
            while (current != null && current != typeof(object))
            {
                foreach (var method in current.GetMethods(flags))
                {
                    if (!method.IsDefined(typeof(InjectAttribute), false)) continue;
                    // VContainer stops scanning a type's methods at the first override it already saw.
                    if (methods.Exists(m => m.GetBaseDefinition() == method.GetBaseDefinition())) break;
                    methods.Add(method);
                }
                foreach (var field in current.GetFields(flags))
                {
                    if (!field.IsDefined(typeof(InjectAttribute), false)) continue;
                    if (fields.Exists(f => f.Name == field.Name)) break;
                    fields.Add(field);
                }
                foreach (var property in current.GetProperties(flags))
                {
                    if (!property.IsDefined(typeof(InjectAttribute), false)) continue;
                    if (properties.Exists(p => p.Name == property.Name)) break;
                    properties.Add(property);
                }
                current = current.BaseType;
            }

            foreach (var method in methods)
            {
                foreach (var parameter in method.GetParameters())
                {
                    var keyed = KeyOf(parameter, out var key);
                    points.Add(new InjectionPoint(parameter.ParameterType, parameter.Name ?? string.Empty,
                        $"[Inject] {method.Name}({Fmt.Type(parameter.ParameterType)} {parameter.Name})",
                        (method, parameter.Position), false, keyed, key));
                }
            }
            foreach (var field in fields)
            {
                var keyed = KeyOf(field, out var key);
                points.Add(new InjectionPoint(field.FieldType, field.Name,
                    $"[Inject] {Fmt.Type(field.FieldType)} {field.Name}", (field, -1),
                    false, keyed, key));
            }
            foreach (var property in properties)
            {
                var keyed = KeyOf(property, out var key);
                points.Add(new InjectionPoint(property.PropertyType, property.Name,
                    $"[Inject] {Fmt.Type(property.PropertyType)} {property.Name}", (property, -1),
                    false, keyed, key));
            }
            return points;
        }

        /// <summary>
        /// Reads VContainer's <c>[Key(…)]</c> from a parameter, field or property. The attribute exists only
        /// in VContainer versions with keyed registrations (the 1.19 fork), so it is found by name.
        /// </summary>
        private static bool KeyOf(ICustomAttributeProvider member, out object? key)
        {
            key = null;
            object[] attributes;
            try
            {
                attributes = member.GetCustomAttributes(true);
            }
            catch (Exception)
            {
                return false;
            }
            foreach (var attribute in attributes)
            {
                var type = attribute.GetType();
                if (type.FullName != "VContainer.KeyAttribute") continue;
                key = type.GetProperty("Key")?.GetValue(attribute);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Describes why the injected constructor of <paramref name="type"/> is ambiguous — several
        /// constructors share the highest parameter count and none carries [Inject] — or null when it is not.
        /// </summary>
        public static string? Ambiguity(Type type) => Cache.GetOrAdd(type, Select).Ambiguity;

        /// <summary>
        /// Mirrors VContainer's TypeAnalyzer exactly, loop and all: a single [Inject] constructor wins
        /// wherever it is declared; otherwise the first constructor reflection returns with strictly the
        /// most parameters. Using the very same reflection order in the very same process is what keeps the
        /// graph edges identical to the dependencies VContainer injects, even for an ambiguous type.
        /// </summary>
        private static Selection Select(Type type)
        {
            ConstructorInfo? chosen = null;
            ParameterInfo[]? chosenParameters = null;
            var annotated = 0;
            var maxParameters = -1;
            var tied = 1;

            foreach (var constructor in type.GetConstructors(
                         BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (constructor.IsDefined(typeof(InjectAttribute), false))
                {
                    // More than one [Inject] constructor makes VContainer throw at resolution; there is
                    // nothing to derive edges from.
                    if (++annotated > 1) return new Selection(Array.Empty<ParameterInfo>(), null);
                    chosen = constructor;
                    chosenParameters = null;
                }
                else if (annotated <= 0)
                {
                    var parameters = constructor.GetParameters();
                    if (parameters.Length > maxParameters)
                    {
                        chosen = constructor;
                        chosenParameters = parameters;
                        maxParameters = parameters.Length;
                        tied = 1;
                    }
                    else if (parameters.Length == maxParameters)
                    {
                        tied++;
                    }
                }
            }

            if (chosen == null) return new Selection(Array.Empty<ParameterInfo>(), null);
            var result = chosenParameters ?? chosen.GetParameters();
            if (annotated == 1 || tied < 2) return new Selection(result, null);

            var ambiguity =
                $"{type.Name} has {tied.ToString(CultureInfo.InvariantCulture)} constructors with " +
                $"{maxParameters.ToString(CultureInfo.InvariantCulture)} parameter{(maxParameters == 1 ? string.Empty : "s")} " +
                $"and none is marked [Inject]; VContainer injects the first one reflection returns ({Signature(result)}), " +
                "which is not guaranteed to be the same in every build. Mark the intended constructor with [Inject].";
            return new Selection(result, ambiguity);
        }

        private static string Signature(ParameterInfo[] parameters)
        {
            var text = new StringBuilder();
            text.Append('(');
            for (var i = 0; i < parameters.Length; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(Fmt.Type(parameters[i].ParameterType)).Append(' ').Append(parameters[i].Name);
            }
            return text.Append(')').ToString();
        }

        private sealed class Selection
        {
            public Selection(ParameterInfo[] parameters, string? ambiguity)
            {
                Parameters = parameters;
                Ambiguity = ambiguity;
            }

            public ParameterInfo[] Parameters { get; }
            public string? Ambiguity { get; }
        }
    }
}
