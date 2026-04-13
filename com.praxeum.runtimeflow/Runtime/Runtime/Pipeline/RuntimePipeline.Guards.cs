using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SFS.Core.GameLoading;
using VContainer;

namespace RuntimeFlow.Contexts
{
    public sealed partial class RuntimePipeline
    {
        private async Task EvaluateGuardsAsync(
            RuntimeFlowGuardStage stage,
            Type? scopeKey,
            GameContextType? targetScopeType,
            CancellationToken cancellationToken)
        {
            if (_guards == null || _guards.Count == 0) return;

            var context = new RuntimeFlowGuardContext(stage, null, scopeKey, targetScopeType);
            foreach (var guard in _guards)
            {
                var result = await guard.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
                if (!result.IsAllowed)
                    throw new RuntimeFlowGuardFailedException(stage, result.ReasonCode!, result.Reason);
            }
        }

        private static ScopeNotDeclaredException CreateScopeTypeNotDeclaredException(Type scopeType)
        {
            return new ScopeNotDeclaredException(scopeType);
        }

        private IReadOnlyList<IRuntimeFlowGuard>? ComposeGuardsWithRestartPreparationHooks(
            IReadOnlyList<IRuntimeFlowGuard>? guards,
            IReadOnlyList<IRuntimeSessionRestartPreparationHook>? hooks)
        {
            var configuredHooks = hooks?.Where(hook => hook != null).ToArray()
                                 ?? Array.Empty<IRuntimeSessionRestartPreparationHook>();
            var hasConfiguredHooks = configuredHooks.Length > 0;

            var combined = guards == null
                ? new List<IRuntimeFlowGuard>()
                : new List<IRuntimeFlowGuard>(guards);
            combined.Insert(
                0,
                new RuntimeSessionRestartPreparationGuardBridge(
                    configuredHooks,
                    () => ResolveSessionRestartPreparationHooks(hasConfiguredHooks)));
            return combined;
        }

        private IReadOnlyList<IRuntimeSessionRestartPreparationHook> ResolveSessionRestartPreparationHooks(
            bool hasConfiguredHooks)
        {
            var hooks = new List<IRuntimeSessionRestartPreparationHook>();

            TryAppendResolvedHooks(hooks, () => _builder.TryResolveFromSession<IRuntimeSessionRestartPreparationHook[]>(out var resolved) ? resolved : null);
            TryAppendResolvedHooks(hooks, () => _builder.TryResolveFromSession<IReadOnlyList<IRuntimeSessionRestartPreparationHook>>(out var resolved) ? resolved : null);
            TryAppendResolvedHooks(hooks, () => _builder.TryResolveFromSession<IEnumerable<IRuntimeSessionRestartPreparationHook>>(out var resolved) ? resolved : null);
            TryAppendResolvedSingleHook(hooks, () => _builder.TryResolveFromSession<IRuntimeSessionRestartPreparationHook>(out var resolved) ? resolved : null);

            if (hooks.Count > 0)
            {
                return hooks;
            }

            if (!HasRegisteredLegacyRestartAwareServices())
            {
                return hooks;
            }

            throw new RuntimeFlowGuardFailedException(
                RuntimeFlowGuardStage.BeforeSessionRestart,
                "restart.preparation.hooks.missing",
                hasConfiguredHooks
                    ? "Configured session restart preparation hooks resolved to an empty set."
                    : "No session restart preparation hooks are registered for legacy restart-aware services. Register IRuntimeSessionRestartPreparationHook in session scope or RuntimePipelineOptions.SessionRestartPreparationHooks.");
        }

        private static void TryAppendResolvedHooks(
            List<IRuntimeSessionRestartPreparationHook> hooks,
            Func<IEnumerable<IRuntimeSessionRestartPreparationHook>?> resolver)
        {
            if (hooks == null) throw new ArgumentNullException(nameof(hooks));
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));

            var resolvedHooks = resolver();
            if (resolvedHooks == null)
            {
                return;
            }

            foreach (var resolvedHook in resolvedHooks)
            {
                TryAppendResolvedSingleHook(hooks, () => resolvedHook);
            }
        }

        private static void TryAppendResolvedSingleHook(
            List<IRuntimeSessionRestartPreparationHook> hooks,
            Func<IRuntimeSessionRestartPreparationHook?> resolver)
        {
            if (hooks == null) throw new ArgumentNullException(nameof(hooks));
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));

            var hook = resolver();
            if (hook == null)
            {
                return;
            }

            for (var i = 0; i < hooks.Count; i++)
            {
                if (ReferenceEquals(hooks[i], hook))
                {
                    return;
                }
            }

            hooks.Add(hook);
        }

        private bool HasRegisteredLegacyRestartAwareServices()
        {
            IGameContext sessionContext;
            try
            {
                sessionContext = _builder.GetSessionContext();
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            var resolver = sessionContext.Resolver;
            return HasResolvedLegacyRestartAwareServices(
                       resolver,
                       typeof(IEnumerable<ISessionRestartAware>))
                   || HasResolvedLegacyRestartAwareServices(
                       resolver,
                       typeof(IReadOnlyList<ISessionRestartAware>))
                   || HasResolvedLegacyRestartAwareServices(
                       resolver,
                       typeof(ISessionRestartAware[]))
                   || HasResolvedLegacyRestartAwareServices(
                       resolver,
                       typeof(ISessionRestartAware));
        }

        private static bool HasResolvedLegacyRestartAwareServices(
            IObjectResolver resolver,
            Type serviceType)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));

            if (!resolver.TryResolve(serviceType, out var resolved) || resolved == null)
            {
                return false;
            }

            if (resolved is ISessionRestartAware)
            {
                return true;
            }

            if (resolved is not IEnumerable sequence)
            {
                return false;
            }

            foreach (var resolvedService in sequence)
            {
                if (resolvedService is ISessionRestartAware)
                {
                    return true;
                }
            }

            return false;
        }

    }
}
