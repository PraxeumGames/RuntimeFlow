using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Loading;

namespace RuntimeFlow.Contexts
{
    internal sealed class ScopeActivationService
    {
        private readonly IInitializationExecutionScheduler _executionScheduler;
        private readonly ConditionalWeakTable<GameContext, ConcurrentDictionary<GameContextType, ScopeActivationExecutionPlan>> _planCache = new();

        public ScopeActivationService(IInitializationExecutionScheduler executionScheduler)
        {
            _executionScheduler = executionScheduler ?? throw new ArgumentNullException(nameof(executionScheduler));
        }

        internal ScopeActivationExecutionPlan DiscoverPlan(GameContextType scope, GameContext context)
        {
            var contextCache = _planCache.GetOrCreateValue(context);
            return contextCache.GetOrAdd(scope, s => BuildPlan(s, context));
        }

        private static ScopeActivationExecutionPlan BuildPlan(GameContextType scope, GameContext context)
        {
            var markerType = ResolveMarker(scope);
            var participants = new List<ScopeActivationParticipantBinding>();
            var registeredTypes = context.RegisteredServiceTypes;
            var seenServiceTypes = new HashSet<Type>();

            foreach (var serviceType in registeredTypes)
            {
                if (!seenServiceTypes.Add(serviceType)) continue;

                Type? impl;
                if (!context.TryGetImplementationType(serviceType, out impl))
                {
                    if (serviceType.IsInterface) continue;
                    impl = serviceType;
                }
                if (!markerType.IsAssignableFrom(serviceType) && !markerType.IsAssignableFrom(impl)) continue;
                participants.Add(new ScopeActivationParticipantBinding(serviceType, impl));
            }

            var ordered = participants
                .GroupBy(p => p.ImplementationType)
                .Select(g => g.OrderBy(p => GetDeterministicTypeName(p.ServiceType), StringComparer.Ordinal).First())
                .OrderBy(p => GetDeterministicTypeName(p.ImplementationType), StringComparer.Ordinal)
                .ThenBy(p => GetDeterministicTypeName(p.ServiceType), StringComparer.Ordinal)
                .ToArray();
            return new ScopeActivationExecutionPlan(ordered);
        }

        internal Task ExecuteEnterAsync(GameContextType scope, GameContext context, IInitializationProgressNotifier progressNotifier, int totalServices, CancellationToken ct, ScopeActivationExecutionPlan? plan = null)
        {
            plan ??= DiscoverPlan(scope, context);
            var completedStep = Math.Max(0, totalServices);
            return ExecutePhaseAsync(context, plan.EnterOrder, static (s, token) => s.OnScopeActivatedAsync(token),
                () => NotifyActivationStarted(progressNotifier, scope, completedStep),
                () => NotifyActivationCompleted(progressNotifier, scope, completedStep), ct);
        }

        internal Task ExecuteExitAsync(GameContextType scope, GameContext context, IInitializationProgressNotifier progressNotifier, CancellationToken ct, ScopeActivationExecutionPlan? plan = null)
        {
            plan ??= DiscoverPlan(scope, context);
            return ExecutePhaseAsync(context, plan.ExitOrder, static (s, token) => s.OnScopeDeactivatingAsync(token),
                () => NotifyDeactivationStarted(progressNotifier, scope),
                () => NotifyDeactivationCompleted(progressNotifier, scope), ct);
        }

        private async Task ExecutePhaseAsync(
            GameContext context,
            IReadOnlyList<ScopeActivationParticipantBinding> participants,
            Func<IAsyncScopeActivationService, CancellationToken, Task> callback,
            Action? onPhaseStarted,
            Action? onPhaseCompleted,
            CancellationToken cancellationToken)
        {
            onPhaseStarted?.Invoke();
            for (var i = 0; i < participants.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var participant = participants[i];
                var resolved = await context.ResolveAsync(participant.ServiceType, cancellationToken).ConfigureAwait(false);
                if (resolved is not IAsyncScopeActivationService svc)
                    throw new InvalidOperationException($"Service {participant.ServiceType.Name} is expected to implement {nameof(IAsyncScopeActivationService)}.");
                var affinity = resolved is IInitializationThreadAffinityProvider p ? p.ThreadAffinity : InitializationThreadAffinity.MainThread;
                await _executionScheduler.ExecuteAsync(affinity, token => callback(svc, token), cancellationToken).ConfigureAwait(false);
            }
            onPhaseCompleted?.Invoke();
        }

        private static void NotifyActivationStarted(IInitializationProgressNotifier n, GameContextType scope, int total)
        {
            if (n is IRuntimeScopeLifecycleProgressNotifier l) l.OnScopeActivationStarted(scope, total, total);
        }
        private static void NotifyActivationCompleted(IInitializationProgressNotifier n, GameContextType scope, int total)
        {
            if (n is IRuntimeScopeLifecycleProgressNotifier l) l.OnScopeActivationCompleted(scope, total, total);
        }
        private static void NotifyDeactivationStarted(IInitializationProgressNotifier n, GameContextType scope)
        {
            if (n is IRuntimeScopeLifecycleProgressNotifier l) l.OnScopeDeactivationStarted(scope);
        }
        private static void NotifyDeactivationCompleted(IInitializationProgressNotifier n, GameContextType scope)
        {
            if (n is IRuntimeScopeLifecycleProgressNotifier l) l.OnScopeDeactivationCompleted(scope);
        }
        private static Type ResolveMarker(GameContextType scope) => scope switch
        {
            GameContextType.Session => typeof(ISessionScopeActivationService),
            GameContextType.Scene => typeof(ISceneScopeActivationService),
            GameContextType.Module => typeof(IModuleScopeActivationService),
            _ => throw new InvalidOperationException($"Scope activation hooks are available only for Session/Scene/Module scopes. Requested scope: {scope}.")
        };
        private static string GetDeterministicTypeName(Type type) => type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
    }
}
