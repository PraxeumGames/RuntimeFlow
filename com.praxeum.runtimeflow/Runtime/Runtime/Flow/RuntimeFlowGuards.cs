using System;
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
