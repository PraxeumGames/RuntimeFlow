using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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

        private static IReadOnlyList<IRuntimeFlowGuard>? ComposeGuardsWithRestartPreparationHooks(
            IReadOnlyList<IRuntimeFlowGuard>? guards,
            IReadOnlyList<IRuntimeSessionRestartPreparationHook>? hooks)
        {
            if (hooks == null || hooks.Count == 0)
            {
                return guards;
            }

            var effectiveHooks = hooks.Where(hook => hook != null).ToArray();
            if (effectiveHooks.Length == 0)
            {
                return guards;
            }

            var combined = guards == null
                ? new List<IRuntimeFlowGuard>()
                : new List<IRuntimeFlowGuard>(guards);
            combined.Insert(0, new RuntimeSessionRestartPreparationGuardBridge(effectiveHooks));
            return combined;
        }
    }
}
