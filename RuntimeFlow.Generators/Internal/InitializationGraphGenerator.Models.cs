using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace RuntimeFlow.Generators
{
    public sealed partial class InitializationGraphGenerator
    {
        private enum ScopeKind
        {
            Global = 0,
            Session = 1,
            Scene = 2,
            Module = 3
        }

        private enum VisitState
        {
            NotVisited = 0,
            Visiting = 1,
            Visited = 2
        }

        private sealed class ServiceNode
        {
            private readonly HashSet<INamedTypeSymbol> _dependencies = new(SymbolEqualityComparer.Default);

            public ServiceNode(INamedTypeSymbol serviceType, INamedTypeSymbol implementationType, ScopeKind scope)
            {
                ServiceType = serviceType;
                ImplementationType = implementationType;
                Scope = scope;
            }

            public INamedTypeSymbol ServiceType { get; }
            public INamedTypeSymbol ImplementationType { get; }
            public ScopeKind Scope { get; }
            public IEnumerable<INamedTypeSymbol> Dependencies => _dependencies;

            public void AddDependency(INamedTypeSymbol dependency)
            {
                if (!SymbolEqualityComparer.Default.Equals(ServiceType, dependency))
                    _dependencies.Add(dependency);
            }
        }

        private sealed class GenerationModel
        {
            public GenerationModel(IReadOnlyCollection<ServiceNode> nodes)
            {
                Nodes = nodes;
            }

            public IReadOnlyCollection<ServiceNode> Nodes { get; }
        }

        private sealed class GeneratorSymbols
        {
            private GeneratorSymbols(
                INamedTypeSymbol? asyncInitializable,
                INamedTypeSymbol? globalMarker,
                INamedTypeSymbol? sessionMarker,
                INamedTypeSymbol? sceneMarker,
                INamedTypeSymbol? moduleMarker,
                INamedTypeSymbol? gameContextTypeSymbol,
                INamedTypeSymbol? generateGraphAttribute,
                IReadOnlyCollection<INamedTypeSymbol> markerOnlyAsyncContracts)
            {
                AsyncInitializable = asyncInitializable;
                GlobalMarker = globalMarker;
                SessionMarker = sessionMarker;
                SceneMarker = sceneMarker;
                ModuleMarker = moduleMarker;
                GameContextTypeSymbol = gameContextTypeSymbol;
                GenerateGraphAttribute = generateGraphAttribute;
                MarkerOnlyAsyncContracts = markerOnlyAsyncContracts;
            }

            public INamedTypeSymbol? AsyncInitializable { get; }
            public INamedTypeSymbol? GlobalMarker { get; }
            public INamedTypeSymbol? SessionMarker { get; }
            public INamedTypeSymbol? SceneMarker { get; }
            public INamedTypeSymbol? ModuleMarker { get; }
            public INamedTypeSymbol? GameContextTypeSymbol { get; }
            public INamedTypeSymbol? GenerateGraphAttribute { get; }
            public IReadOnlyCollection<INamedTypeSymbol> MarkerOnlyAsyncContracts { get; }
            public bool IsValid =>
                AsyncInitializable != null
                && GameContextTypeSymbol != null
                && GenerateGraphAttribute != null;

            public bool IsMarkerOnlyAsyncContract(INamedTypeSymbol type)
            {
                return MarkerOnlyAsyncContracts.Any(marker =>
                    SymbolEqualityComparer.Default.Equals(type, marker));
            }

            public static GeneratorSymbols Create(Compilation compilation)
            {
                return new GeneratorSymbols(
                    compilation.GetTypeByMetadataName(AsyncInitInterface),
                    compilation.GetTypeByMetadataName(GlobalMarkerInterface),
                    compilation.GetTypeByMetadataName(SessionMarkerInterface),
                    compilation.GetTypeByMetadataName(SceneMarkerInterface),
                    compilation.GetTypeByMetadataName(ModuleMarkerInterface),
                    compilation.GetTypeByMetadataName(GameContextType),
                    compilation.GetTypeByMetadataName(GenerateGraphAttributeMetadataName),
                    MarkerOnlyAsyncContractInterfaces
                        .Select(compilation.GetTypeByMetadataName)
                        .Where(symbol => symbol != null)
                        .Select(symbol => symbol!)
                        .ToArray());
            }
        }

        private static class Diagnostics
        {
            public static readonly DiagnosticDescriptor DuplicateImplementation = new(
                id: "RF0001",
                title: "Duplicate async service implementation",
                messageFormat: "Service contract '{0}' has multiple implementations: '{1}' and '{2}'",
                category: "InitializationGraph",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true);

            public static readonly DiagnosticDescriptor MissingDependency = new(
                id: "RF0002",
                title: "Missing async dependency",
                messageFormat: "Service '{0}' depends on async service '{1}', but no implementation is registered",
                category: "InitializationGraph",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true);

            public static readonly DiagnosticDescriptor ScopeViolation = new(
                id: "RF0003",
                title: "Scope dependency violation",
                messageFormat: "Service '{0}' in scope '{3}' cannot depend on service '{1}' from later scope '{2}'",
                category: "InitializationGraph",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true);

            public static readonly DiagnosticDescriptor CycleDetected = new(
                id: "RF0004",
                title: "Initialization cycle detected",
                messageFormat: "Initialization graph contains a dependency cycle: {0}",
                category: "InitializationGraph",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true);
        }
    }
}
