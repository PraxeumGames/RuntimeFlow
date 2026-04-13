using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    public partial class GameContextBuilder
    {
        private ScopeActivationExecutionPlan DiscoverScopeActivationExecutionPlan(
            GameContextType scope,
            GameContext context)
        {
            var markerType = ResolveScopeActivationMarker(scope);
            var participants = new List<ScopeActivationParticipantBinding>();

            foreach (var serviceType in context.RegisteredServiceTypes.Distinct())
            {
                Type implementationType;
                if (!context.TryGetImplementationType(serviceType, out implementationType))
                {
                    if (serviceType.IsInterface)
                        continue;

                    implementationType = serviceType;
                }

                if (!markerType.IsAssignableFrom(serviceType) && !markerType.IsAssignableFrom(implementationType))
                    continue;

                participants.Add(new ScopeActivationParticipantBinding(serviceType, implementationType));
            }

            var ordered = participants
                .GroupBy(participant => participant.ImplementationType)
                .Select(group => group
                    .OrderBy(participant => GetDeterministicTypeName(participant.ServiceType), StringComparer.Ordinal)
                    .First())
                .OrderBy(participant => GetDeterministicTypeName(participant.ImplementationType), StringComparer.Ordinal)
                .ThenBy(participant => GetDeterministicTypeName(participant.ServiceType), StringComparer.Ordinal)
                .ToArray();

            return new ScopeActivationExecutionPlan(ordered);
        }

        private Task ExecuteScopeActivationEnterAsync(
            GameContextType scope,
            GameContext context,
            CancellationToken cancellationToken)
        {
            return ExecuteScopeActivationEnterAsync(
                scope,
                context,
                NullInitializationProgressNotifier.Instance,
                totalServices: 0,
                cancellationToken);
        }

        private Task ExecuteScopeActivationEnterAsync(
            GameContextType scope,
            GameContext context,
            IInitializationProgressNotifier progressNotifier,
            int totalServices,
            CancellationToken cancellationToken)
        {
            var executionPlan = DiscoverScopeActivationExecutionPlan(scope, context);
            return ExecuteScopeActivationEnterAsync(scope, context, executionPlan, progressNotifier, totalServices, cancellationToken);
        }

        private Task ExecuteScopeActivationExitAsync(
            GameContextType scope,
            GameContext context,
            CancellationToken cancellationToken)
        {
            return ExecuteScopeActivationExitAsync(
                scope,
                context,
                NullInitializationProgressNotifier.Instance,
                cancellationToken);
        }

        private Task ExecuteScopeActivationExitAsync(
            GameContextType scope,
            GameContext context,
            IInitializationProgressNotifier progressNotifier,
            CancellationToken cancellationToken)
        {
            var executionPlan = DiscoverScopeActivationExecutionPlan(scope, context);
            return ExecuteScopeActivationExitAsync(scope, context, executionPlan, progressNotifier, cancellationToken);
        }

        private Task ExecuteScopeActivationEnterAsync(
            GameContextType scope,
            GameContext context,
            ScopeActivationExecutionPlan executionPlan,
            IInitializationProgressNotifier progressNotifier,
            int totalServices,
            CancellationToken cancellationToken)
        {
            var completedStep = Math.Max(0, totalServices);
            return ExecuteScopeActivationPhaseAsync(
                context,
                executionPlan.EnterOrder,
                static (service, token) => service.OnScopeActivatedAsync(token),
                onPhaseStarted: () => NotifyScopeActivationStarted(progressNotifier, scope, completedStep),
                onPhaseCompleted: () => NotifyScopeActivationCompleted(progressNotifier, scope, completedStep),
                cancellationToken);
        }

        private Task ExecuteScopeActivationExitAsync(
            GameContextType scope,
            GameContext context,
            ScopeActivationExecutionPlan executionPlan,
            IInitializationProgressNotifier progressNotifier,
            CancellationToken cancellationToken)
        {
            return ExecuteScopeActivationPhaseAsync(
                context,
                executionPlan.ExitOrder,
                static (service, token) => service.OnScopeDeactivatingAsync(token),
                onPhaseStarted: () => NotifyScopeDeactivationStarted(progressNotifier, scope),
                onPhaseCompleted: () => NotifyScopeDeactivationCompleted(progressNotifier, scope),
                cancellationToken);
        }

        private async Task ExecuteScopeActivationPhaseAsync(
            GameContext context,
            IReadOnlyList<ScopeActivationParticipantBinding> participants,
            Func<IAsyncScopeActivationService, CancellationToken, Task> callback,
            Action? onPhaseStarted,
            Action? onPhaseCompleted,
            CancellationToken cancellationToken)
        {
            onPhaseStarted?.Invoke();
            foreach (var participant in participants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolved = context.Resolve(participant.ServiceType);
                if (resolved is not IAsyncScopeActivationService activationService)
                {
                    throw new InvalidOperationException(
                        $"Service {participant.ServiceType.Name} is expected to implement {nameof(IAsyncScopeActivationService)}.");
                }

                var affinity = resolved is IInitializationThreadAffinityProvider affinityProvider
                    ? affinityProvider.ThreadAffinity
                    : InitializationThreadAffinity.MainThread;

                await _executionScheduler.ExecuteAsync(
                        affinity,
                        token => callback(activationService, token),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            onPhaseCompleted?.Invoke();
        }

        private static void NotifyScopeActivationStarted(
            IInitializationProgressNotifier progressNotifier,
            GameContextType scope,
            int totalServices)
        {
            if (progressNotifier is IRuntimeScopeLifecycleProgressNotifier lifecycleProgressNotifier)
                lifecycleProgressNotifier.OnScopeActivationStarted(scope, totalServices, totalServices);
        }

        private static void NotifyScopeActivationCompleted(
            IInitializationProgressNotifier progressNotifier,
            GameContextType scope,
            int totalServices)
        {
            if (progressNotifier is IRuntimeScopeLifecycleProgressNotifier lifecycleProgressNotifier)
                lifecycleProgressNotifier.OnScopeActivationCompleted(scope, totalServices, totalServices);
        }

        private static void NotifyScopeDeactivationStarted(
            IInitializationProgressNotifier progressNotifier,
            GameContextType scope)
        {
            if (progressNotifier is IRuntimeScopeLifecycleProgressNotifier lifecycleProgressNotifier)
                lifecycleProgressNotifier.OnScopeDeactivationStarted(scope);
        }

        private static void NotifyScopeDeactivationCompleted(
            IInitializationProgressNotifier progressNotifier,
            GameContextType scope)
        {
            if (progressNotifier is IRuntimeScopeLifecycleProgressNotifier lifecycleProgressNotifier)
                lifecycleProgressNotifier.OnScopeDeactivationCompleted(scope);
        }

        private static Type ResolveScopeActivationMarker(GameContextType scope)
        {
            return scope switch
            {
                GameContextType.Session => typeof(ISessionScopeActivationService),
                GameContextType.Scene => typeof(ISceneScopeActivationService),
                GameContextType.Module => typeof(IModuleScopeActivationService),
                _ => throw new InvalidOperationException(
                    $"Scope activation hooks are available only for Session/Scene/Module scopes. Requested scope: {scope}.")
            };
        }

        private static string GetDeterministicTypeName(Type type)
        {
            return type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
        }
    }
}
