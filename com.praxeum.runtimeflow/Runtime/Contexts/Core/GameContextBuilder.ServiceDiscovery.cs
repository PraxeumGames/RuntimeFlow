using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using VContainer;
using VContainer.Unity;

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

            var lifecycleIndex = new LifecycleRegistrationIndex();

            var localExplicitServiceTypes = context.RegisteredServiceTypes
                .Where(InitializationGraphRules.IsExplicitDependencyType)
                .OrderBy(GetDeterministicTypeName, StringComparer.Ordinal)
                .ToArray();

            foreach (var serviceType in localExplicitServiceTypes)
            {
                AddCandidateFromServiceType(
                    context,
                    compiledGraph,
                    serviceType,
                    lifecycleIndex,
                    LifecycleRegistrationOrigin.RuntimeFlowRegistration);
            }

            foreach (var discoveredType in DiscoverRegisteredAsyncServiceTypes(context)
                         .OrderBy(GetDeterministicTypeName, StringComparer.Ordinal))
            {
                AddCandidateFromServiceType(
                    context,
                    compiledGraph,
                    discoveredType,
                    lifecycleIndex,
                    LifecycleRegistrationOrigin.RuntimeFlowRegistration);
            }

            foreach (var discoveredRegistration in DiscoverRegisteredAsyncServiceRegistrations(context)
                         .OrderBy(candidate => GetDeterministicTypeName(candidate.ServiceType), StringComparer.Ordinal))
            {
                var implementationType = discoveredRegistration.Registration.ImplementationType;
                lifecycleIndex.Add(
                    discoveredRegistration.ServiceType,
                    implementationType,
                    InitializationGraphRules.ResolveConstructorDependencies(implementationType),
                    discoveredRegistration.ResolveServiceType,
                    discoveredRegistration.Registration,
                    LifecycleRegistrationOrigin.VContainerRegistration);
            }

            foreach (var node in compiledGraph.Values)
            {
                if (InitializationGraphRules.IsAsyncDependencyType(node.ServiceType)
                    && IsLocallyRegisteredForInitialization(context, node.ServiceType))
                {
                    lifecycleIndex.Add(
                        node.ServiceType,
                        node.ImplementationType,
                        node.Dependencies
                            .Where(InitializationGraphRules.IsExplicitDependencyType)
                            .Distinct()
                            .ToArray(),
                        node.ServiceType,
                        registration: null,
                        LifecycleRegistrationOrigin.CompiledGraph);
                }
            }

            var rawBindings = lifecycleIndex.SelectEffectiveDescriptors(context);
            var typeToServiceType = new Dictionary<Type, Type>();
            foreach (var binding in rawBindings)
            {
                typeToServiceType[binding.ImplementationType] = binding.ServiceType;
                typeToServiceType[binding.ServiceType] = binding.ServiceType;
                typeToServiceType[binding.ResolveServiceType] = binding.ServiceType;

                if (binding.Registration == null)
                    continue;

                foreach (var interfaceType in binding.Registration.InterfaceTypes)
                {
                    typeToServiceType[interfaceType] = binding.ServiceType;
                }
            }

            var initializers = new List<ServiceInitializerBinding>();
            foreach (var binding in rawBindings)
            {
                var resolvedDependencies = binding.RawDependencies
                    .Select(dep => ResolveToServiceType(dep, typeToServiceType))
                    .Where(dep => dep != null)
                    .Select(dep => dep!)
                    .Distinct()
                    .ToArray();

                initializers.Add(new ServiceInitializerBinding(
                    binding.ServiceType,
                    binding.ImplementationType,
                    resolvedDependencies,
                    binding.ResolveServiceType,
                    SelectInitializerResolveRegistration(binding)));
            }

            return initializers;
        }

        private static Registration? SelectInitializerResolveRegistration(LifecycleRegistrationDescriptor binding)
        {
            var registration = binding.Registration;
            if (registration == null)
                return null;

            // When a concrete service interface is exposed by VContainer, resolve through that
            // interface so later container overrides/decorators remain visible to RuntimeFlow
            // initialization. Exact Registration resolution is only needed for marker-only
            // services where resolving the marker interface would be ambiguous.
            return registration.InterfaceTypes.Contains(binding.ResolveServiceType)
                   && !InitializationContractCatalog.IsMarkerOnlyAsyncInitializationType(binding.ResolveServiceType)
                   && !IsVContainerInitializableType(binding.ResolveServiceType)
                   && !IsVContainerStartableType(binding.ResolveServiceType)
                ? null
                : registration;
        }

        private static void AddCandidateFromServiceType(
            GameContext context,
            IReadOnlyDictionary<Type, RuntimeFlowCompiledInitializationGraph.Node> compiledGraph,
            Type serviceType,
            LifecycleRegistrationIndex lifecycleIndex,
            LifecycleRegistrationOrigin origin)
        {
            Type? implementationType;
            IReadOnlyCollection<Type> rawDependencies;
            var effectiveOrigin = origin;

            if (compiledGraph.TryGetValue(serviceType, out var compiledNode))
            {
                implementationType = compiledNode.ImplementationType;
                rawDependencies = compiledNode.Dependencies
                    .Where(InitializationGraphRules.IsExplicitDependencyType)
                    .Distinct()
                    .ToArray();
                effectiveOrigin |= LifecycleRegistrationOrigin.CompiledGraph;
            }
            else
            {
                if (!context.TryGetImplementationType(serviceType, out implementationType))
                {
                    if (serviceType.IsInterface)
                        return;

                    implementationType = serviceType;
                }

                rawDependencies = InitializationGraphRules.ResolveConstructorDependencies(implementationType);
            }

            lifecycleIndex.Add(
                serviceType,
                implementationType,
                rawDependencies,
                serviceType,
                registration: null,
                effectiveOrigin);
        }

        private static IEnumerable<InitializerRegistrationCandidate> DiscoverRegisteredAsyncServiceRegistrations(GameContext context)
        {
            foreach (var markerType in InitializationContractCatalog.DiscoverableAsyncInitializationMarkerTypes)
            {
                foreach (var registration in context.GetRegistrationsForServiceType(markerType))
                {
                    var implementationType = registration.ImplementationType;
                    if (!typeof(IAsyncInitializableService).IsAssignableFrom(implementationType))
                        continue;

                    var serviceType = SelectLifecycleServiceType(registration, implementationType);
                    var resolveServiceType = SelectResolveServiceType(registration, serviceType);
                    yield return new InitializerRegistrationCandidate(serviceType, resolveServiceType, registration);
                }
            }
        }

        private static Type SelectLifecycleServiceType(Registration registration, Type implementationType)
        {
            if (registration.InterfaceTypes.Contains(implementationType))
                return implementationType;

            var explicitAsyncServiceType = registration.InterfaceTypes
                .Where(InitializationGraphRules.IsExplicitDependencyType)
                .OrderBy(GetDeterministicTypeName, StringComparer.Ordinal)
                .FirstOrDefault();
            if (explicitAsyncServiceType != null)
                return explicitAsyncServiceType;

            var ordinaryServiceType = registration.InterfaceTypes
                .Where(type => type != null)
                .Where(type => !InitializationContractCatalog.IsMarkerOnlyAsyncInitializationType(type))
                .Where(type => !IsVContainerInitializableType(type))
                .Where(type => !IsVContainerStartableType(type))
                .Where(type => type != typeof(IDisposable))
                .Where(type => type != typeof(IAsyncDisposable))
                .Where(type => type.IsAssignableFrom(implementationType))
                .OrderBy(GetDeterministicTypeName, StringComparer.Ordinal)
                .FirstOrDefault();
            return ordinaryServiceType ?? implementationType;
        }

        private static Type SelectResolveServiceType(Registration registration, Type serviceType)
        {
            if (registration.InterfaceTypes.Contains(serviceType))
                return serviceType;

            return registration.InterfaceTypes
                       .Where(type => type != null)
                       .Where(type => !IsVContainerInitializableType(type))
                       .Where(type => !IsVContainerStartableType(type))
                       .Where(type => type != typeof(IDisposable))
                       .Where(type => type != typeof(IAsyncDisposable))
                       .OrderBy(type => InitializationContractCatalog.IsMarkerOnlyAsyncInitializationType(type) ? 1 : 0)
                       .ThenBy(GetDeterministicTypeName, StringComparer.Ordinal)
                       .FirstOrDefault()
                   ?? serviceType;
        }

        private static bool IsVContainerInitializableType(Type serviceType)
        {
            return serviceType == typeof(IInitializable);
        }

        private static bool IsVContainerStartableType(Type serviceType)
        {
            return serviceType == typeof(IStartable);
        }

        [Flags]
        private enum LifecycleRegistrationOrigin
        {
            None = 0,
            RuntimeFlowRegistration = 1,
            VContainerRegistration = 2,
            CompiledGraph = 4
        }

        private sealed class LifecycleRegistrationIndex
        {
            private readonly Dictionary<Type, LifecycleRegistrationDescriptor> _descriptorsByImplementation = new();

            public void Add(
                Type serviceType,
                Type implementationType,
                IReadOnlyCollection<Type> rawDependencies,
                Type? resolveServiceType,
                Registration? registration,
                LifecycleRegistrationOrigin origin)
            {
                if (_descriptorsByImplementation.TryGetValue(implementationType, out var existingDescriptor))
                {
                    _descriptorsByImplementation[implementationType] = Merge(existingDescriptor);
                    return;
                }

                _descriptorsByImplementation[implementationType] = new LifecycleRegistrationDescriptor(
                    serviceType,
                    implementationType,
                    rawDependencies,
                    resolveServiceType ?? serviceType,
                    registration,
                    origin);
                return;

                LifecycleRegistrationDescriptor Merge(LifecycleRegistrationDescriptor existing)
                {
                    var mergedDependencies = existing.RawDependencies
                        .Concat(rawDependencies)
                        .Distinct()
                        .ToArray();

                    var preferredServiceType = IsPreferredServiceType(
                        serviceType,
                        existing.ServiceType,
                        implementationType)
                        ? serviceType
                        : existing.ServiceType;

                    var useNewDescriptorIdentity = preferredServiceType == serviceType;
                    return new LifecycleRegistrationDescriptor(
                        preferredServiceType,
                        implementationType,
                        mergedDependencies,
                        useNewDescriptorIdentity ? resolveServiceType ?? serviceType : existing.ResolveServiceType,
                        useNewDescriptorIdentity ? registration : existing.Registration,
                        existing.Origin | origin);
                }
            }

            public LifecycleRegistrationDescriptor[] SelectEffectiveDescriptors(GameContext context)
            {
                return _descriptorsByImplementation.Values
                    .GroupBy(binding => binding.ServiceType)
                    .Select(group =>
                    {
                        var candidates = group.ToArray();
                        if (candidates.Length == 1)
                            return candidates[0];

                        if (context.TryGetImplementationType(group.Key, out var effectiveImplementationType))
                        {
                            var effective = candidates
                                .LastOrDefault(binding => binding.ImplementationType == effectiveImplementationType);
                            if (effective != null)
                                return effective;
                        }

                        var implementations = string.Join(
                            ", ",
                            candidates
                                .Select(binding => GetDeterministicTypeName(binding.ImplementationType))
                                .OrderBy(name => name, StringComparer.Ordinal));
                        throw new InvalidOperationException(
                            $"Multiple initializer registrations resolved to service type {group.Key.Name}, " +
                            $"but RuntimeFlow could not determine the effective container registration. " +
                            $"Implementations: {implementations}");
                    })
                    .ToArray();
            }
        }

        private sealed class LifecycleRegistrationDescriptor
        {
            public LifecycleRegistrationDescriptor(
                Type serviceType,
                Type implementationType,
                IReadOnlyCollection<Type> rawDependencies,
                Type resolveServiceType,
                Registration? registration,
                LifecycleRegistrationOrigin origin)
            {
                ServiceType = serviceType;
                ImplementationType = implementationType;
                RawDependencies = rawDependencies;
                ResolveServiceType = resolveServiceType;
                Registration = registration;
                Origin = origin;
            }

            public Type ServiceType { get; }
            public Type ImplementationType { get; }
            public IReadOnlyCollection<Type> RawDependencies { get; }
            public Type ResolveServiceType { get; }
            public Registration? Registration { get; }
            public LifecycleRegistrationOrigin Origin { get; }
        }

        private sealed class InitializerRegistrationCandidate
        {
            public InitializerRegistrationCandidate(Type serviceType, Type resolveServiceType, Registration registration)
            {
                ServiceType = serviceType;
                ResolveServiceType = resolveServiceType;
                Registration = registration;
            }

            public Type ServiceType { get; }
            public Type ResolveServiceType { get; }
            public Registration Registration { get; }
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
                if (IsLocallyRegisteredForInitialization(context, type))
                    yield return type;
            }
        }

        private static bool IsLocallyRegisteredForInitialization(GameContext context, Type serviceType)
        {
            return context.RegisteredServiceTypes.Contains(serviceType)
                   || context.GetRegistrationsForServiceType(serviceType).Count > 0;
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
            Dictionary<Type, Type> typeToServiceType)
        {
            if (typeToServiceType.TryGetValue(dep, out var mappedServiceType))
                return mappedServiceType;

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
