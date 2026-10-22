using System;
using System.Collections.Generic;
using System.Reflection;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Reads the type catalog RuntimeFlow.Generators emits into every assembly that references RuntimeFlow
    /// (<c>RuntimeFlow.Contexts.Generated.RuntimeFlowTypeCatalog</c>). The catalog lists metadata names, and resolving
    /// them by name doesn't touch the assembly's other types, so discovery initializes only the classes it lists.
    /// An assembly without a catalog (built without the generator) returns null, and the caller scans it.
    /// </summary>
    internal static class GeneratedTypeCatalog
    {
        private const string CatalogTypeName = "RuntimeFlow.Contexts.Generated.RuntimeFlowTypeCatalog";

        /// <summary>A superset of the assembly's types assignable to <see cref="IAsyncInitializableService"/>.</summary>
        internal static Type[]? AsyncInitializableTypes(Assembly assembly) => Read(assembly, "AsyncInitializableTypes");

        /// <summary>A superset of the assembly's interfaces that extend other interfaces.</summary>
        internal static Type[]? DerivedInterfaces(Assembly assembly) => Read(assembly, "DerivedInterfaces");

        private static Type[]? Read(Assembly assembly, string field)
        {
            if (assembly.GetType(CatalogTypeName, throwOnError: false)
                    ?.GetField(field, BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null) is not string[] names)
                return null;

            var types = new List<Type>(names.Length);
            foreach (var name in names)
            {
                // A type the player build stripped is used nowhere, so there is nothing to discover
                var type = assembly.GetType(name, throwOnError: false);
                if (type != null)
                    types.Add(type);
            }

            return types.ToArray();
        }
    }
}
