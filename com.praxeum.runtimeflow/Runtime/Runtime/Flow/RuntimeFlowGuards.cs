using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    public enum RuntimeFlowGuardStage
    {
        BeforeInitialize,
        BeforeSessionSceneLoad,
        BeforeRouteResolution,
        BeforeNavigation,
        BeforeSceneLoad,
        BeforeModuleLoad,
        BeforeScopeReload,
        BeforeSessionRestart
    }

    public readonly struct RuntimeFlowGuardResult
    {
        private RuntimeFlowGuardResult(bool isAllowed, string? reasonCode, string? reason)
        {
            IsAllowed = isAllowed;
            ReasonCode = reasonCode;
            Reason = reason;
        }

        public bool IsAllowed { get; }
        public string? ReasonCode { get; }
        public string? Reason { get; }

        public static RuntimeFlowGuardResult Allow() => new RuntimeFlowGuardResult(true, null, null);

        public static RuntimeFlowGuardResult Deny(string reasonCode, string? reason = null)
        {
            if (string.IsNullOrWhiteSpace(reasonCode))
                throw new ArgumentException("Reason code is required.", nameof(reasonCode));
            return new RuntimeFlowGuardResult(false, reasonCode, reason);
        }
    }

    public sealed class RuntimeFlowGuardContext
    {
        public RuntimeFlowGuardContext(
            RuntimeFlowGuardStage stage,
            IRuntimeFlowContext flowContext,
            SceneRoute? targetRoute = null)
        {
            Stage = stage;
            FlowContext = flowContext ?? throw new ArgumentNullException(nameof(flowContext));
            TargetRoute = targetRoute;
        }

        public RuntimeFlowGuardContext(
            RuntimeFlowGuardStage stage,
            IRuntimeFlowContext? flowContext,
            Type? scopeKey,
            GameContextType? targetScopeType)
        {
            Stage = stage;
            FlowContext = flowContext;
            ScopeKey = scopeKey;
            TargetScopeType = targetScopeType;
        }

        public RuntimeFlowGuardStage Stage { get; }
        public IRuntimeFlowContext? FlowContext { get; }
        public SceneRoute? TargetRoute { get; }
        public Type? ScopeKey { get; }
        public GameContextType? TargetScopeType { get; }
    }

    public interface IRuntimeFlowGuard
    {
        Task<RuntimeFlowGuardResult> EvaluateAsync(
            RuntimeFlowGuardContext context,
            CancellationToken cancellationToken = default);
    }

    public sealed class RuntimeSessionRestartPreparationContext
    {
        public RuntimeSessionRestartPreparationContext(RuntimeFlowGuardContext guardContext)
        {
            GuardContext = guardContext ?? throw new ArgumentNullException(nameof(guardContext));
        }

        public RuntimeFlowGuardContext GuardContext { get; }
        public IRuntimeFlowContext? FlowContext => GuardContext.FlowContext;
        public Type? ScopeKey => GuardContext.ScopeKey;
        public GameContextType? TargetScopeType => GuardContext.TargetScopeType;
    }

    public interface IRuntimeSessionRestartPreparationHook
    {
        Task PrepareForSessionRestartAsync(
            RuntimeSessionRestartPreparationContext context,
            CancellationToken cancellationToken = default);
    }

    internal sealed class RuntimeSessionRestartPreparationGuardBridge : IRuntimeFlowGuard
    {
        private readonly IReadOnlyList<IRuntimeSessionRestartPreparationHook> _hooks;

        public RuntimeSessionRestartPreparationGuardBridge(
            IReadOnlyList<IRuntimeSessionRestartPreparationHook> hooks)
        {
            _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        }

        public async Task<RuntimeFlowGuardResult> EvaluateAsync(
            RuntimeFlowGuardContext context,
            CancellationToken cancellationToken = default)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (context.Stage != RuntimeFlowGuardStage.BeforeSessionRestart || _hooks.Count == 0)
                return RuntimeFlowGuardResult.Allow();

            var preparationContext = new RuntimeSessionRestartPreparationContext(context);
            foreach (var hook in _hooks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (hook == null)
                {
                    continue;
                }

                await hook.PrepareForSessionRestartAsync(preparationContext, cancellationToken)
                    .ConfigureAwait(false);
            }

            return RuntimeFlowGuardResult.Allow();
        }
    }

    public sealed class RuntimeFlowGuardFailedException : InvalidOperationException
    {
        public RuntimeFlowGuardFailedException(
            RuntimeFlowGuardStage stage,
            string reasonCode,
            string? reason)
            : base(
                $"Flow guard blocked runtime at stage \'{stage}\'. Code=\'{reasonCode}\'. " +
                $"{(string.IsNullOrWhiteSpace(reason) ? string.Empty : reason)}".Trim())
        {
            Stage = stage;
            ReasonCode = reasonCode;
            Reason = reason;
        }

        public RuntimeFlowGuardStage Stage { get; }
        public string ReasonCode { get; }
        public string? Reason { get; }
    }
}
