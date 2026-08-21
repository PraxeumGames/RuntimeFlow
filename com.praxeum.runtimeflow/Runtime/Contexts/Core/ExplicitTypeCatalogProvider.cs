using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

namespace RuntimeFlow.Contexts
{
    public static class ExplicitTypeCatalogProvider
    {
        private static readonly ConcurrentDictionary<Assembly, Type[]> PerAssemblyCache = new();
        private static volatile Type[]? _generatedCatalog;

        public static Type[] GetExplicitDependencyTypes()
        {
            var generated = TryGetGeneratedCatalog();
            if (generated != null)
                return generated;

#if RUNTIMEFLOW_ENABLE_RUNTIME_SCAN
            return BuildScanFallback();
#else
            return Array.Empty<Type>();
#endif
        }

        private static Type[]? TryGetGeneratedCatalog()
        {
            var cached = _generatedCatalog;
            if (cached != null)
                return cached.Length == 0 ? null : cached;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType("RuntimeFlow.Contexts.Generated.RuntimeFlowGeneratedCatalog", throwOnError: false);
                if (type == null)
                    continue;

                var field = type.GetField("ExplicitDependencyTypes", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (field?.GetValue(null) is Type[] types)
                {
                    _generatedCatalog = types;
                    return types.Length == 0 ? null : types;
                }
            }

            _generatedCatalog = Array.Empty<Type>();
            return null;
        }

#if RUNTIMEFLOW_ENABLE_RUNTIME_SCAN
        private static Type[] BuildScanFallback()
        {
            var result = new ConcurrentDictionary<Type, byte>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!ReferencesRuntimeFlow(assembly))
                    continue;

                var types = PerAssemblyCache.GetOrAdd(assembly, LoadExplicitTypes);
                foreach (var type in types)
                    result.TryAdd(type, 0);
            }

            return result.Keys.ToArray();
        }

        private static bool ReferencesRuntimeFlow(Assembly assembly)
        {
            try
            {
                return assembly.GetReferencedAssemblies().Any(a => a.Name == "RuntimeFlow.Runtime")
                       || assembly.GetName().Name == "RuntimeFlow.Runtime"
                       || assembly.GetName().Name == "Assembly-CSharp";
            }
            catch
            {
                return true;
            }
        }

        private static Type[] LoadExplicitTypes(Assembly assembly)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
            }
            catch
            {
                return Array.Empty<Type>();
            }

            return types.Where(InitializationGraphRules.IsExplicitDependencyType).ToArray();
        }
#endif
    }
}
