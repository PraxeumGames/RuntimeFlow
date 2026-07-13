using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace RuntimeFlow.Generators
{
    public sealed partial class InitializationGraphGenerator
    {
        private static IEnumerable<INamedTypeSymbol> EnumerateNamedTypes(INamespaceSymbol root)
        {
            foreach (var member in root.GetMembers())
            {
                if (member is INamespaceSymbol nestedNamespace)
                {
                    foreach (var nestedType in EnumerateNamedTypes(nestedNamespace))
                        yield return nestedType;
                }
                else if (member is INamedTypeSymbol namedType)
                {
                    yield return namedType;

                    foreach (var nested in EnumerateNestedTypes(namedType))
                        yield return nested;
                }
            }
        }

        private static IEnumerable<INamedTypeSymbol> EnumerateNestedTypes(INamedTypeSymbol root)
        {
            foreach (var nested in root.GetTypeMembers())
            {
                yield return nested;
                foreach (var child in EnumerateNestedTypes(nested))
                    yield return child;
            }
        }

        private static IMethodSymbol? SelectConstructor(INamedTypeSymbol implementationType)
        {
            var constructors = implementationType.InstanceConstructors
                .Where(ctor => !ctor.IsImplicitlyDeclared)
                .ToArray();
            if (constructors.Length == 0)
                return null;

            var injectConstructor = constructors.FirstOrDefault(ctor =>
                ctor.GetAttributes().Any(attribute => attribute.AttributeClass?.Name == "InjectAttribute"));
            return injectConstructor ?? constructors.OrderByDescending(ctor => ctor.Parameters.Length).First();
        }

        private static bool IsScopedAsyncContract(INamedTypeSymbol type, GeneratorSymbols symbols)
        {
            return type.TypeKind == TypeKind.Interface
                   && IsAsyncDependency(type, symbols)
                   && TryResolveScope(type, symbols, out _);
        }

        private static bool IsAsyncDependency(INamedTypeSymbol type, GeneratorSymbols symbols)
        {
            if (symbols.IsMarkerOnlyAsyncContract(type))
                return false;

            return type.TypeKind == TypeKind.Interface
                   && symbols.AsyncInitializable != null
                   && type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, symbols.AsyncInitializable));
        }

        private static bool IsExplicitDependency(INamedTypeSymbol type, GeneratorSymbols symbols)
        {
            if (IsEntryPointCompletionDependency(type))
                return true;

            if (symbols.IsMarkerOnlyAsyncContract(type))
                return false;

            if (symbols.AsyncInitializable == null)
                return false;

            return type.TypeKind switch
            {
                TypeKind.Interface => type.AllInterfaces.Any(i =>
                    SymbolEqualityComparer.Default.Equals(i, symbols.AsyncInitializable)),
                TypeKind.Class => type.AllInterfaces.Any(i =>
                    SymbolEqualityComparer.Default.Equals(i, symbols.AsyncInitializable)),
                _ => false
            };
        }

        private static bool IsEntryPointCompletionDependency(INamedTypeSymbol type)
        {
            var metadataName = type.ToDisplayString();
            return metadataName == EntryPointsStartupPhaseType
                   || metadataName == SessionSyncEntryPointsBootstrapServiceType;
        }

        private static IEnumerable<INamedTypeSymbol> ResolveExplicitAttributeDependencies(
            INamedTypeSymbol implementationType,
            GeneratorSymbols symbols)
        {
            foreach (var attribute in implementationType.GetAttributes())
            {
                var attributeClass = attribute.AttributeClass;
                if (attributeClass == null)
                    continue;

                if (attributeClass.ToDisplayString() != DependsOnAttribute)
                    continue;

                if (attribute.ConstructorArguments.Length != 1)
                    continue;

                if (attribute.ConstructorArguments[0].Value is not INamedTypeSymbol dependencyType)
                    continue;

                if (IsExplicitDependency(dependencyType, symbols))
                    yield return dependencyType;
            }
        }

        private static bool TryResolveScope(INamedTypeSymbol type, GeneratorSymbols symbols, out ScopeKind scope)
        {
            var scopes = 0;
            scope = ScopeKind.Global;

            if (symbols.GlobalMarker != null && type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, symbols.GlobalMarker)))
            {
                scope = ScopeKind.Global;
                scopes++;
            }

            if (symbols.SessionMarker != null && type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, symbols.SessionMarker)))
            {
                scope = ScopeKind.Session;
                scopes++;
            }

            if (symbols.SceneMarker != null && type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, symbols.SceneMarker)))
            {
                scope = ScopeKind.Scene;
                scopes++;
            }

            if (symbols.ModuleMarker != null && type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, symbols.ModuleMarker)))
            {
                scope = ScopeKind.Module;
                scopes++;
            }

            return scopes == 1;
        }
    }
}
