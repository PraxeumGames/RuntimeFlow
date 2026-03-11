using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RuntimeFlow.Generators
{
    [Generator(LanguageNames.CSharp)]
    public sealed class InitializationGraphGenerator : IIncrementalGenerator
    {
        private const string AsyncInitInterface = "RuntimeFlow.Contexts.IAsyncInitializableService";
        private const string GlobalMarkerInterface = "RuntimeFlow.Contexts.IGlobalInitializableService";
        private const string SessionMarkerInterface = "RuntimeFlow.Contexts.ISessionInitializableService";
        private const string SceneMarkerInterface = "RuntimeFlow.Contexts.ISceneInitializableService";
        private const string ModuleMarkerInterface = "RuntimeFlow.Contexts.IModuleInitializableService";
        private const string GraphRulesVersion = "compiled-constructor-v1";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterSourceOutput(context.CompilationProvider, static (spc, compilation) =>
            {
                var model = BuildModel(compilation, spc);
                spc.AddSource("CompiledInitializationGraph.g.cs", SourceText.From(Render(model), Encoding.UTF8));
            });
        }

        private static GenerationModel BuildModel(Compilation compilation, SourceProductionContext context)
        {
            var symbols = GeneratorSymbols.Create(compilation);
            if (!symbols.IsValid)
            {
                return new GenerationModel(Array.Empty<ServiceNode>());
            }

            var nodes = new Dictionary<INamedTypeSymbol, ServiceNode>(SymbolEqualityComparer.Default);
            foreach (var implementation in EnumerateNamedTypes(compilation.Assembly.GlobalNamespace)
                         .Where(type => type.TypeKind == TypeKind.Class && !type.IsAbstract))
            {
                var serviceContracts = implementation.AllInterfaces
                    .Where(contract => IsScopedAsyncContract(contract, symbols))
                    .ToArray();

                foreach (var serviceContract in serviceContracts)
                {
                    if (!TryResolveScope(serviceContract, symbols, out var scope))
                        continue;

                    if (nodes.TryGetValue(serviceContract, out var existing)
                        && !SymbolEqualityComparer.Default.Equals(existing.ImplementationType, implementation))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            Diagnostics.DuplicateImplementation,
                            serviceContract.Locations.FirstOrDefault(),
                            serviceContract.ToDisplayString(),
                            existing.ImplementationType.ToDisplayString(),
                            implementation.ToDisplayString()));
                        continue;
                    }

                    nodes[serviceContract] = new ServiceNode(serviceContract, implementation, scope);
                }
            }

            foreach (var node in nodes.Values)
            {
                var constructor = SelectConstructor(node.ImplementationType);
                if (constructor == null)
                    continue;

                foreach (var parameter in constructor.Parameters)
                {
                    if (parameter.Type is not INamedTypeSymbol parameterType)
                        continue;
                    if (!IsAsyncDependency(parameterType, symbols))
                        continue;

                    if (!nodes.TryGetValue(parameterType, out var dependencyNode))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            Diagnostics.MissingDependency,
                            node.ServiceType.Locations.FirstOrDefault(),
                            node.ServiceType.ToDisplayString(),
                            parameterType.ToDisplayString()));
                        continue;
                    }

                    if ((int)dependencyNode.Scope > (int)node.Scope)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            Diagnostics.ScopeViolation,
                            node.ServiceType.Locations.FirstOrDefault(),
                            node.ServiceType.ToDisplayString(),
                            dependencyNode.ServiceType.ToDisplayString(),
                            dependencyNode.Scope.ToString(),
                            node.Scope.ToString()));
                        continue;
                    }

                    node.AddDependency(parameterType);
                }
            }

            DetectCycles(nodes, context);
            return new GenerationModel(nodes.Values
                .OrderBy(node => node.Scope)
                .ThenBy(node => node.ServiceType.ToDisplayString())
                .ToArray());
        }

        private static void DetectCycles(
            IReadOnlyDictionary<INamedTypeSymbol, ServiceNode> nodes,
            SourceProductionContext context)
        {
            var state = new Dictionary<INamedTypeSymbol, VisitState>(SymbolEqualityComparer.Default);
            var stack = new Stack<INamedTypeSymbol>();

            foreach (var node in nodes.Keys)
            {
                if (state.TryGetValue(node, out var visitState) && visitState != VisitState.NotVisited)
                    continue;

                Visit(node, nodes, state, stack, context);
            }
        }

        private static void Visit(
            INamedTypeSymbol current,
            IReadOnlyDictionary<INamedTypeSymbol, ServiceNode> nodes,
            IDictionary<INamedTypeSymbol, VisitState> state,
            Stack<INamedTypeSymbol> stack,
            SourceProductionContext context)
        {
            state[current] = VisitState.Visiting;
            stack.Push(current);

            foreach (var dependency in nodes[current].Dependencies)
            {
                if (!nodes.ContainsKey(dependency))
                    continue;

                if (!state.TryGetValue(dependency, out var dependencyState))
                {
                    Visit(dependency, nodes, state, stack, context);
                    continue;
                }

                if (dependencyState == VisitState.Visiting)
                {
                    var cycle = string.Join(" -> ", stack.Reverse().Select(type => type.ToDisplayString()));
                    context.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.CycleDetected,
                        current.Locations.FirstOrDefault(),
                        cycle));
                }
            }

            stack.Pop();
            state[current] = VisitState.Visited;
        }

        private static string Render(GenerationModel model)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("namespace RuntimeFlow.Contexts.Generated");
            sb.AppendLine("{");
            sb.AppendLine("    internal static class CompiledInitializationGraph");
            sb.AppendLine("    {");
            sb.AppendLine($"        internal const string RuleVersion = \"{GraphRulesVersion}\";");
            sb.AppendLine();
            sb.AppendLine("        internal sealed class Node");
            sb.AppendLine("        {");
            sb.AppendLine("            internal Node(global::System.Type serviceType, global::System.Type implementationType, global::RuntimeFlow.Contexts.GameContextType scope, global::System.Type[] dependencies)");
            sb.AppendLine("            {");
            sb.AppendLine("                ServiceType = serviceType;");
            sb.AppendLine("                ImplementationType = implementationType;");
            sb.AppendLine("                Scope = scope;");
            sb.AppendLine("                Dependencies = dependencies;");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            internal global::System.Type ServiceType { get; }");
            sb.AppendLine("            internal global::System.Type ImplementationType { get; }");
            sb.AppendLine("            internal global::RuntimeFlow.Contexts.GameContextType Scope { get; }");
            sb.AppendLine("            internal global::System.Type[] Dependencies { get; }");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        internal static readonly Node[] Nodes = new Node[]");
            sb.AppendLine("        {");

            foreach (var node in model.Nodes)
            {
                var dependencies = node.Dependencies
                    .Select(dependency => $"typeof({ToTypeOfDisplay(dependency)})")
                    .ToArray();

                sb.Append("            new Node(");
                sb.Append($"typeof({ToTypeOfDisplay(node.ServiceType)}), ");
                sb.Append($"typeof({ToTypeOfDisplay(node.ImplementationType)}), ");
                sb.Append($"global::RuntimeFlow.Contexts.GameContextType.{node.Scope}, ");
                sb.Append($"new global::System.Type[] {{ {string.Join(", ", dependencies)} }}");
                sb.AppendLine("),");
            }

            sb.AppendLine("        };");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string ToTypeOfDisplay(INamedTypeSymbol symbol)
        {
            return symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

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
            if (SymbolEqualityComparer.Default.Equals(type, symbols.AsyncInitializable))
                return false;
            if (SymbolEqualityComparer.Default.Equals(type, symbols.GlobalMarker)
                || SymbolEqualityComparer.Default.Equals(type, symbols.SessionMarker)
                || SymbolEqualityComparer.Default.Equals(type, symbols.SceneMarker)
                || SymbolEqualityComparer.Default.Equals(type, symbols.ModuleMarker))
            {
                return false;
            }

            return type.TypeKind == TypeKind.Interface
                   && symbols.AsyncInitializable != null
                   && type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, symbols.AsyncInitializable));
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
                INamedTypeSymbol? moduleMarker)
            {
                AsyncInitializable = asyncInitializable;
                GlobalMarker = globalMarker;
                SessionMarker = sessionMarker;
                SceneMarker = sceneMarker;
                ModuleMarker = moduleMarker;
            }

            public INamedTypeSymbol? AsyncInitializable { get; }
            public INamedTypeSymbol? GlobalMarker { get; }
            public INamedTypeSymbol? SessionMarker { get; }
            public INamedTypeSymbol? SceneMarker { get; }
            public INamedTypeSymbol? ModuleMarker { get; }
            public bool IsValid => AsyncInitializable != null;

            public static GeneratorSymbols Create(Compilation compilation)
            {
                return new GeneratorSymbols(
                    compilation.GetTypeByMetadataName(AsyncInitInterface),
                    compilation.GetTypeByMetadataName(GlobalMarkerInterface),
                    compilation.GetTypeByMetadataName(SessionMarkerInterface),
                    compilation.GetTypeByMetadataName(SceneMarkerInterface),
                    compilation.GetTypeByMetadataName(ModuleMarkerInterface));
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
