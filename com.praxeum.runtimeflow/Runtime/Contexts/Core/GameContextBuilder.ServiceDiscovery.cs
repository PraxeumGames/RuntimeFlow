using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using VContainer;

namespace RuntimeFlow.Contexts
{
    public partial class GameContextBuilder
    {
        private static void RegisterAutoServices(
            GameContext context,
            IReadOnlyCollection<ServiceDescriptor> autoServices,
            IDictionary<Type, object> availableServices)
        {
            if (autoServices.Count == 0)
                return;

            var pending = autoServices
                .Select(descriptor => new ServiceConstructionBinding(
                    descriptor.ServiceType,
                    descriptor.ImplementationType,
                    InitializationGraphRules.ResolveConstructorDependencies(descriptor.ImplementationType)))
                .ToList();

            foreach (var binding in pending)
            {
                foreach (var dependency in binding.Dependencies)
                {
                    var isKnown = pending.Any(item => item.ServiceType == dependency)
                                  || availableServices.ContainsKey(dependency)
                                  || context.TryGetRegisteredInstance(dependency, out _);
                    if (!isKnown)
                    {
                        var knownServices = string.Join(", ", pending.Select(item => item.ServiceType.Name).Distinct());
                        throw new InvalidOperationException(
                            $"Service {binding.ServiceType.Name} depends on {dependency.Name}, but dependency is not registered. Known services: {knownServices}");
                    }
                }
            }

            var createdInstances = new Dictionary<Type, object>();
            while (pending.Count > 0)
            {
                var ready = pending
                    .Where(binding => binding.Dependencies.All(dependency =>
                        availableServices.ContainsKey(dependency) || context.TryGetRegisteredInstance(dependency, out _)))
                    .ToArray();

                if (ready.Length == 0)
                {
                    var unresolved = string.Join(", ", pending.Select(binding => binding.ServiceType.Name).Distinct());
                    throw new InvalidOperationException($"Constructor dependency cycle detected. Remaining services: {unresolved}");
                }

                foreach (var group in ready.GroupBy(binding => binding.ImplementationType))
                {
                    var exemplar = group.First();
                    if (!createdInstances.TryGetValue(exemplar.ImplementationType, out var instance))
                    {
                        instance = CreateServiceInstance(context, exemplar, availableServices);
                        createdInstances[exemplar.ImplementationType] = instance;
                    }

                    var serviceTypes = group.Select(binding => binding.ServiceType).Distinct().ToArray();
                    context.RegisterInstanceEx(exemplar.ImplementationType, instance, serviceTypes, ownsLifetime: true);

                    foreach (var binding in group)
                    {
                        if (!availableServices.ContainsKey(binding.ServiceType))
                            availableServices[binding.ServiceType] = instance;
                        pending.Remove(binding);
                    }
                }
            }
        }

        private static object CreateServiceInstance(
            GameContext context,
            ServiceConstructionBinding binding,
            IDictionary<Type, object> availableServices)
        {
            var constructor = InitializationGraphRules.SelectConstructor(binding.ImplementationType);
            if (constructor == null)
            {
                return Activator.CreateInstance(binding.ImplementationType)
                       ?? throw new InvalidOperationException($"Failed to create {binding.ImplementationType.Name}.");
            }

            var arguments = constructor.GetParameters()
                .Select(parameter => ResolveConstructorParameter(context, parameter, availableServices))
                .ToArray();
            return constructor.Invoke(arguments);
        }

        private static object ResolveConstructorParameter(
            GameContext context,
            ParameterInfo parameter,
            IDictionary<Type, object> availableServices)
        {
            var parameterType = parameter.ParameterType;
            if (availableServices.TryGetValue(parameterType, out var available))
                return available;
            if (context.TryGetRegisteredInstance(parameterType, out var localInstance))
                return localInstance;
            if (TryResolveFromParent(context.Parent, parameterType, out var parentValue))
                return parentValue;
            if (parameter.HasDefaultValue)
                return parameter.DefaultValue!;

            throw new InvalidOperationException(
                $"Cannot resolve constructor dependency {parameterType.Name} for {context.GetType().Name}.");
        }

        private static bool TryResolveFromParent(IGameContext? parent, Type serviceType, [MaybeNullWhen(false)] out object resolved)
        {
            if (parent == null)
            {
                resolved = null;
                return false;
            }

            try
            {
                resolved = parent.Resolve(serviceType);
                return true;
            }
            catch (VContainerException)
            {
                resolved = null;
                return false;
            }
            catch (InvalidOperationException)
            {
                resolved = null;
                return false;
            }
        }

        private static List<ServiceInitializerBinding> DiscoverInitializers(GameContext context)
        {
            var useCompiledGraph = ShouldUseCompiledInitializationGraph(RuntimeFlowCompiledInitializationGraph.RuleVersion);
            var compiledGraph = useCompiledGraph
                ? RuntimeFlowCompiledInitializationGraph.Nodes
                    .GroupBy(node => node.ServiceType)
                    .ToDictionary(group => group.Key, group => group.Last())
                : new Dictionary<Type, RuntimeFlowCompiledInitializationGraph.Node>();

            var candidateServiceTypes = new HashSet<Type>(
                context.RegisteredServiceTypes.Where(InitializationGraphRules.IsExplicitDependencyType));

            if (candidateServiceTypes.Count == 0)
            {
                foreach (var discoveredType in DiscoverRegisteredAsyncServiceTypes(context))
                {
                    candidateServiceTypes.Add(discoveredType);
                }
            }

            foreach (var node in compiledGraph.Values)
            {
                if (InitializationGraphRules.IsAsyncDependencyType(node.ServiceType)
                    && context.IsRegistered(node.ServiceType))
                {
                    candidateServiceTypes.Add(node.ServiceType);
                }
            }

            var rawBindingsByImplementation = new Dictionary<Type, (Type serviceType, Type implementationType, IReadOnlyCollection<Type> rawDependencies)>();
            foreach (var serviceType in candidateServiceTypes.OrderBy(GetDeterministicTypeName, StringComparer.Ordinal))
            {
                Type? implementationType;
                IReadOnlyCollection<Type> rawDependencies;

                if (compiledGraph.TryGetValue(serviceType, out var compiledNode))
                {
                    implementationType = compiledNode.ImplementationType;
                    rawDependencies = compiledNode.Dependencies
                        .Where(InitializationGraphRules.IsExplicitDependencyType)
                        .Distinct()
                        .ToArray();
                }
                else
                {
                    if (!context.TryGetImplementationType(serviceType, out implementationType))
                    {
                        if (serviceType.IsInterface)
                            continue;

                        implementationType = serviceType;
                    }

                    rawDependencies = InitializationGraphRules.ResolveConstructorDependencies(implementationType);
                }

                if (rawBindingsByImplementation.TryGetValue(implementationType, out var existingBinding))
                {
                    var mergedDependencies = existingBinding.rawDependencies
                        .Concat(rawDependencies)
                        .Distinct()
                        .ToArray();

                    var preferredServiceType = IsPreferredServiceType(
                        serviceType,
                        existingBinding.serviceType,
                        implementationType)
                        ? serviceType
                        : existingBinding.serviceType;

                    rawBindingsByImplementation[implementationType] =
                        (preferredServiceType, implementationType, mergedDependencies);
                    continue;
                }

                rawBindingsByImplementation[implementationType] =
                    (serviceType, implementationType, rawDependencies);
            }

            var rawBindings = rawBindingsByImplementation.Values.ToArray();
            var typeToServiceType = new Dictionary<Type, Type>();
            foreach (var (serviceType, implementationType, _) in rawBindings)
            {
                typeToServiceType[implementationType] = serviceType;
                typeToServiceType[serviceType] = serviceType;
            }

            foreach (var candidateServiceType in candidateServiceTypes)
            {
                if (!context.TryGetImplementationType(candidateServiceType, out var implementationType))
                    continue;

                if (typeToServiceType.TryGetValue(implementationType, out var canonicalServiceType))
                {
                    typeToServiceType[candidateServiceType] = canonicalServiceType;
                }
            }

            var initializers = new List<ServiceInitializerBinding>();
            foreach (var (serviceType, implementationType, rawDependencies) in rawBindings)
            {
                var resolvedDependencies = rawDependencies
                    .Select(dep => ResolveToServiceType(dep, typeToServiceType, candidateServiceTypes))
                    .Where(dep => dep != null)
                    .Select(dep => dep!)
                    .Distinct()
                    .ToArray();

                initializers.Add(new ServiceInitializerBinding(serviceType, implementationType, resolvedDependencies));
            }

            return initializers;
        }

        internal static bool ShouldUseCompiledInitializationGraph(string compiledRuleVersion)
        {
            if (string.IsNullOrEmpty(compiledRuleVersion))
                return false;

            if (!string.Equals(compiledRuleVersion, InitializationGraphRules.Version, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Compiled initialization graph rule version mismatch. Expected '{InitializationGraphRules.Version}', actual '{compiledRuleVersion}'.");
            }

            return true;
        }

        private static IEnumerable<Type> DiscoverRegisteredAsyncServiceTypes(GameContext context)
        {
            foreach (var type in ExplicitDependencyTypeCatalog.Value)
            {
                if (context.IsRegistered(type))
                    yield return type;
            }
        }

        private static Type[] BuildExplicitDependencyTypeCatalog()
        {
            var result = new HashSet<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = exception.Types.Where(type => type != null).Cast<Type>().ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (InitializationGraphRules.IsExplicitDependencyType(type))
                        result.Add(type);
                }
            }

            return result.ToArray();
        }

        private static Type? ResolveToServiceType(
            Type dep,
            Dictionary<Type, Type> typeToServiceType,
            HashSet<Type> candidateServiceTypes)
        {
            if (typeToServiceType.TryGetValue(dep, out var mappedServiceType))
                return mappedServiceType;

            if (dep.IsInterface && candidateServiceTypes.Contains(dep))
                return dep;

            if (dep.IsInterface)
                return null;

            return null;
        }

        private static bool IsPreferredServiceType(Type candidate, Type current, Type implementationType)
        {
            var candidateIsImplementation = candidate == implementationType;
            var currentIsImplementation = current == implementationType;
            if (candidateIsImplementation != currentIsImplementation)
                return candidateIsImplementation;

            if (candidate.IsInterface != current.IsInterface)
                return !candidate.IsInterface;

            return string.Compare(
                       GetDeterministicTypeName(candidate),
                       GetDeterministicTypeName(current),
                       StringComparison.Ordinal) < 0;
        }
    }
}
