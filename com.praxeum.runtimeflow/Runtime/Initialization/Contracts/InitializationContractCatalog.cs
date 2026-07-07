using System;
using System.Collections.Generic;
using System.Linq;

namespace RuntimeFlow.Contexts
{
    internal static class InitializationContractCatalog
    {
        private static readonly Type[] ScopeMarkerTypeArray =
        {
            typeof(IGlobalInitializableService),
            typeof(ISessionInitializableService),
            typeof(ISceneInitializableService),
            typeof(IModuleInitializableService)
        };

        private static readonly Type[] StartupStageMarkerTypeArray = RuntimeFlowStartupStageContracts.Ordered
            .Select(contract => contract.ServiceMarkerType)
            .ToArray();

        private static readonly Type[] DiscoverableAsyncInitializationMarkerTypeArray =
            ScopeMarkerTypeArray
                .Concat(new[] { typeof(IStartupStageInitializableService) })
                .Concat(StartupStageMarkerTypeArray)
                .Distinct()
                .ToArray();

        private static readonly Type[] MarkerOnlyAsyncInitializationTypeArray =
            DiscoverableAsyncInitializationMarkerTypeArray
                .Concat(new[] { typeof(IAsyncInitializableService) })
                .Distinct()
                .ToArray();

        private static readonly HashSet<Type> MarkerOnlyAsyncInitializationTypes = new(
            MarkerOnlyAsyncInitializationTypeArray);

        internal static IReadOnlyList<Type> ScopeMarkerTypes => ScopeMarkerTypeArray;

        internal static IReadOnlyList<Type> StartupStageMarkerTypes => StartupStageMarkerTypeArray;

        internal static IReadOnlyList<Type> DiscoverableAsyncInitializationMarkerTypes =>
            DiscoverableAsyncInitializationMarkerTypeArray;

        internal static IReadOnlyList<Type> MarkerOnlyAsyncInitializationTypesForDiagnostics =>
            MarkerOnlyAsyncInitializationTypeArray;

        internal static IReadOnlyList<RuntimeFlowStartupStageContract> OrderedStartupStageContracts =>
            RuntimeFlowStartupStageContracts.Ordered;

        internal static bool IsMarkerOnlyAsyncInitializationType(Type serviceType)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            return MarkerOnlyAsyncInitializationTypes.Contains(serviceType);
        }

        internal static bool IsExplicitDependencyType(Type serviceType)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            return !IsMarkerOnlyAsyncInitializationType(serviceType)
                   && typeof(IAsyncInitializableService).IsAssignableFrom(serviceType);
        }

        internal static bool IsConstructorDependencyType(Type serviceType)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            return serviceType.IsInterface && IsExplicitDependencyType(serviceType);
        }
    }
}
