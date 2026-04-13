using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace RuntimeFlow.Contexts
{
    public enum RuntimeRestartLifecycleStage
    {
        Idle = 0,
        WaitingReadiness = 1,
        GuardValidation = 2,
        Restarting = 3,
        ReplayingFlow = 4,
        Completed = 5,
        Failed = 6
    }

    public sealed class RuntimeRestartRequest
    {
        public RuntimeRestartRequest(string reasonCode = null, string diagnostic = null)
        {
            ReasonCode = Normalize(reasonCode);
            Diagnostic = Normalize(diagnostic);
        }

        public string ReasonCode { get; }
        public string Diagnostic { get; }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public sealed class RuntimeRestartLifecycleSnapshot
    {
        public RuntimeRestartLifecycleSnapshot(
            RuntimeRestartLifecycleStage stage,
            DateTimeOffset updatedAtUtc,
            string reasonCode = null,
            string diagnostic = null,
            string errorType = null,
            string errorMessage = null)
        {
            Stage = stage;
            UpdatedAtUtc = updatedAtUtc;
            ReasonCode = Normalize(reasonCode);
            Diagnostic = Normalize(diagnostic);
            ErrorType = Normalize(errorType);
            ErrorMessage = Normalize(errorMessage);
        }

        public RuntimeRestartLifecycleStage Stage { get; }
        public DateTimeOffset UpdatedAtUtc { get; }
        public string ReasonCode { get; }
        public string Diagnostic { get; }
        public string ErrorType { get; }
        public string ErrorMessage { get; }

        public bool IsInProgress => Stage == RuntimeRestartLifecycleStage.WaitingReadiness
                                    || Stage == RuntimeRestartLifecycleStage.GuardValidation
                                    || Stage == RuntimeRestartLifecycleStage.Restarting
                                    || Stage == RuntimeRestartLifecycleStage.ReplayingFlow;

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public sealed class RuntimeRestartReadiness
    {
        public RuntimeRestartReadiness(
            bool isReady,
            DateTimeOffset updatedAtUtc,
            string blockingReasonCode = null,
            string blockingReason = null)
        {
            IsReady = isReady;
            UpdatedAtUtc = updatedAtUtc;
            BlockingReasonCode = Normalize(blockingReasonCode);
            BlockingReason = Normalize(blockingReason);
        }

        public bool IsReady { get; }
        public DateTimeOffset UpdatedAtUtc { get; }
        public string BlockingReasonCode { get; }
        public string BlockingReason { get; }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public sealed class RuntimeRestartGuardContext
    {
        public RuntimeRestartGuardContext(
            RuntimeRestartRequest request,
            RuntimeRestartLifecycleSnapshot snapshot = null,
            IRuntimeExecutionContext executionContext = null,
            RuntimeStatus runtimeStatus = null,
            RuntimeReadinessStatus runtimeReadinessStatus = null,
            RuntimeRestartReadiness restartReadiness = null)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
            Snapshot = snapshot;
            ExecutionContext = executionContext;
            RuntimeStatus = runtimeStatus;
            RuntimeReadinessStatus = runtimeReadinessStatus;
            RestartReadiness = restartReadiness;
        }

        public RuntimeRestartRequest Request { get; }
        public RuntimeRestartLifecycleSnapshot Snapshot { get; }
        public IRuntimeExecutionContext ExecutionContext { get; }
        public RuntimeStatus RuntimeStatus { get; }
        public RuntimeReadinessStatus RuntimeReadinessStatus { get; }
        public RuntimeRestartReadiness RestartReadiness { get; }
    }

    public interface IRuntimeRestartReadinessProvider
    {
        RuntimeRestartReadiness GetRestartReadiness();
    }

    public interface IRuntimeRestartGuard
    {
        Task<RuntimeFlowGuardResult> EvaluateAsync(
            RuntimeRestartGuardContext context,
            CancellationToken cancellationToken = default);
    }

    public interface IRuntimeRestartLifecycleManager : IRuntimeRestartReadinessProvider
    {
        RuntimeRestartLifecycleSnapshot Snapshot { get; }
        Task RestartAsync(RuntimeRestartRequest request, CancellationToken cancellationToken = default);
    }

    public interface IRuntimeReadinessGate : IRuntimeRestartReadinessProvider
    {
        IDisposable Block(string reasonCode, string reason = null);
        Task<RuntimeRestartReadiness> WaitUntilReadyAsync(
            TimeSpan timeout,
            TimeSpan? pollInterval = null,
            CancellationToken cancellationToken = default);
    }

}
