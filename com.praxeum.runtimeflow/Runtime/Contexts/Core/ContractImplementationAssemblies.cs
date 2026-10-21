using System;
using System.Collections.Generic;
using System.Reflection;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Assemblies that can declare a type implementing or extending a given contract: the contract's own assembly and
    /// the ones that reference it directly. A type can't implement an interface or derive from a type whose assembly
    /// it doesn't reference, so the other assemblies have nothing to find.
    /// Reflection over a type (even <see cref="Type.IsInterface"/>) makes IL2CPP initialize its class, and that class
    /// metadata stays for the process lifetime: scanning every loaded assembly initialized all ~30k types of a player.
    /// </summary>
    internal static class ContractImplementationAssemblies
    {
        internal static IEnumerable<Assembly> For(Type contract)
        {
            var contractAssembly = contract.Assembly;
            var contractAssemblyName = contractAssembly.GetName().Name;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == contractAssembly || References(assembly, contractAssemblyName))
                    yield return assembly;
            }
        }

        private static bool References(Assembly assembly, string? assemblyName)
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (string.Equals(reference.Name, assemblyName, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }
}
