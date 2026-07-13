using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace RuntimeFlow.Generators
{
    public sealed partial class InitializationGraphGenerator
    {
        private static GenerationModel? BuildModel(Compilation compilation, SourceProductionContext context)
        {
            var symbols = GeneratorSymbols.Create(compilation);
            if (!symbols.IsValid)
                return null;

            if (!IsGraphGenerationEnabled(compilation, symbols))
                return null;

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

            var implementationToNode = nodes.Values
                .GroupBy(node => node.ImplementationType, SymbolEqualityComparer.Default)
                .ToDictionary(group => group.Key, group => group.First(), SymbolEqualityComparer.Default);

            foreach (var node in nodes.Values)
            {
                var constructor = SelectConstructor(node.ImplementationType);
                var dependencies = constructor == null
                    ? Enumerable.Empty<INamedTypeSymbol>()
                    : constructor.Parameters
                        .Select(parameter => parameter.Type)
                        .OfType<INamedTypeSymbol>()
                        .Where(parameterType => IsAsyncDependency(parameterType, symbols));

                dependencies = dependencies
                    .Concat(ResolveExplicitAttributeDependencies(node.ImplementationType, symbols))
                    .Distinct(SymbolEqualityComparer.Default)
                    .Cast<INamedTypeSymbol>();

                foreach (var dependencyType in dependencies)
                {
                    if (!nodes.TryGetValue(dependencyType, out var dependencyNode))
                    {
                        if (IsEntryPointCompletionDependency(dependencyType))
                        {
                            node.AddDependency(dependencyType);
                            continue;
                        }

                        if (dependencyType.TypeKind == TypeKind.Class)
                        {
                            if (implementationToNode.TryGetValue(dependencyType, out dependencyNode))
                            {
                                AddDependency(node, dependencyType, dependencyNode, context);
                                continue;
                            }

                            node.AddDependency(dependencyType);
                            continue;
                        }

                        context.ReportDiagnostic(Diagnostic.Create(
                            Diagnostics.MissingDependency,
                            node.ServiceType.Locations.FirstOrDefault(),
                            node.ServiceType.ToDisplayString(),
                            dependencyType.ToDisplayString()));
                        continue;
                    }

                    AddDependency(node, dependencyType, dependencyNode, context);
                }
            }

            DetectCycles(nodes, context);
            return new GenerationModel(nodes.Values
                .OrderBy(node => node.Scope)
                .ThenBy(node => node.ServiceType.ToDisplayString())
                .ToArray());
        }

        private static bool IsGraphGenerationEnabled(Compilation compilation, GeneratorSymbols symbols)
        {
            return compilation.Assembly.GetAttributes()
                .Any(attribute => SymbolEqualityComparer.Default.Equals(
                    attribute.AttributeClass,
                    symbols.GenerateGraphAttribute));
        }

        private static void AddDependency(
            ServiceNode node,
            INamedTypeSymbol dependencyType,
            ServiceNode dependencyNode,
            SourceProductionContext context)
        {
            if ((int)dependencyNode.Scope > (int)node.Scope)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.ScopeViolation,
                    node.ServiceType.Locations.FirstOrDefault(),
                    node.ServiceType.ToDisplayString(),
                    dependencyNode.ServiceType.ToDisplayString(),
                    dependencyNode.Scope.ToString(),
                    node.Scope.ToString()));
                return;
            }

            node.AddDependency(dependencyType);
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
    }
}
