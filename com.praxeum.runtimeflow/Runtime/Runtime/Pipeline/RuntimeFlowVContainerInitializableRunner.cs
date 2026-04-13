using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.Contexts
{
    internal static class RuntimeFlowVContainerInitializableRunner
    {
        internal static void InitializeSequential(
            IObjectResolver resolver,
            IReadOnlyList<Registration> initializableRegistrations,
            CancellationToken cancellationToken)
        {
            var initializedInstances = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var registration in initializableRegistrations)
            {
                InitializeRegistration(resolver, registration, initializedInstances, cancellationToken);
            }
        }

        internal static void InitializeSessionByStages(
            IObjectResolver resolver,
            IReadOnlyList<Registration> initializableRegistrations,
            string scopeName,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            if (initializableRegistrations.Count == 0)
                return;

            var initializedInstances = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var initializedImplementationTypes = new HashSet<Type>();
            var stagedRegistrationSet = new HashSet<Registration>();

            logger.LogInformation(
                "Initializing staged startup entry points for {Scope} scope. Stages={StageCount}",
                scopeName,
                RuntimeFlowStartupStageContracts.Ordered.Count);

            foreach (var stageContract in RuntimeFlowStartupStageContracts.Ordered)
            {
                var stageRegistrations = initializableRegistrations
                    .Where(registration => IsStageRegistration(registration, stageContract.ServiceMarkerType))
                    .ToArray();

                logger.LogInformation(
                    "Startup stage {Stage} begin for {Scope} scope. Candidates={Count}",
                    stageContract.Stage,
                    scopeName,
                    stageRegistrations.Length);

                var initializedInStage = 0;
                foreach (var registration in stageRegistrations)
                {
                    stagedRegistrationSet.Add(registration);
                    if (TryInitializeRegistration(
                            resolver,
                            registration,
                            initializedInstances,
                            initializedImplementationTypes,
                            cancellationToken))
                    {
                        initializedInStage++;
                    }
                }

                logger.LogInformation(
                    "Startup stage {Stage} end for {Scope} scope. Initialized={Initialized}, Candidates={Count}",
                    stageContract.Stage,
                    scopeName,
                    initializedInStage,
                    stageRegistrations.Length);
            }

            var remainingRegistrations = initializableRegistrations
                .Where(registration => !stagedRegistrationSet.Contains(registration))
                .ToArray();

            logger.LogInformation(
                "Startup non-staged begin for {Scope} scope. Candidates={Count}",
                scopeName,
                remainingRegistrations.Length);

            var initializedNonStaged = 0;
            foreach (var registration in remainingRegistrations)
            {
                if (TryInitializeRegistration(
                        resolver,
                        registration,
                        initializedInstances,
                        initializedImplementationTypes,
                        cancellationToken))
                {
                    initializedNonStaged++;
                }
            }

            logger.LogInformation(
                "Startup non-staged end for {Scope} scope. Initialized={Initialized}, Candidates={Count}",
                scopeName,
                initializedNonStaged,
                remainingRegistrations.Length);
        }

        private static bool IsStageRegistration(Registration registration, Type stageMarkerType)
        {
            if (registration?.ImplementationType == null || stageMarkerType == null)
                return false;

            return stageMarkerType.IsAssignableFrom(registration.ImplementationType);
        }

        private static IInitializable ResolveInitializableOrThrow(IObjectResolver resolver, Registration registration)
        {
            if (resolver.Resolve(registration) is IInitializable initializable)
                return initializable;

            throw new InvalidOperationException(
                $"Resolved entry point '{registration.ImplementationType?.FullName ?? registration.ImplementationType?.Name ?? "<unknown>"}' " +
                $"does not implement '{typeof(IInitializable).FullName}'.");
        }

        private static void InitializeRegistration(
            IObjectResolver resolver,
            Registration registration,
            ISet<object> initializedInstances,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var initializable = ResolveInitializableOrThrow(resolver, registration);
            if (!initializedInstances.Add(initializable))
                return;

            initializable.Initialize();
        }

        private static bool TryInitializeRegistration(
            IObjectResolver resolver,
            Registration registration,
            ISet<object> initializedInstances,
            ISet<Type> initializedImplementationTypes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var initializable = ResolveInitializableOrThrow(resolver, registration);
            var implementationType = registration.ImplementationType ?? initializable.GetType();

            if (!initializedImplementationTypes.Add(implementationType))
                return false;

            if (!initializedInstances.Add(initializable))
                return false;

            initializable.Initialize();
            return true;
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new();

            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
