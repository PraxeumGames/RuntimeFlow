using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    public static class RuntimeStartupOperationPhases
    {
        public const string GlobalBootstrapOperations = nameof(GlobalBootstrapOperations);
    }

    public interface IGlobalBootstrapOperation
    {
        string Name { get; }
        int Order { get; }
        Task ExecuteAsync(IStartupOperationContext context, CancellationToken cancellationToken);
    }

    public interface IStartupOperationContext
    {
        GameContextType Scope { get; }
        string Phase { get; }
        string OperationName { get; }
        int OperationIndex { get; }
        int TotalOperations { get; }
        string? LastStep { get; }
        string? LastDetail { get; }
        void ReportStep(string step, string? detail = null);
    }

    public enum RuntimeStartupOperationState
    {
        Started = 0,
        Step = 1,
        Completed = 2,
        Failed = 3,
        Canceled = 4
    }

    public sealed class RuntimeStartupSnapshot
    {
        public RuntimeStartupSnapshot(
            GameContextType scope,
            string phase,
            string operationName,
            string? step,
            string? detail,
            TimeSpan elapsed,
            RuntimeStartupOperationState state,
            string? exceptionType = null,
            string? exceptionMessage = null)
        {
            Scope = scope;
            Phase = phase ?? throw new ArgumentNullException(nameof(phase));
            OperationName = operationName ?? throw new ArgumentNullException(nameof(operationName));
            Step = step;
            Detail = detail;
            Elapsed = elapsed;
            State = state;
            ExceptionType = exceptionType;
            ExceptionMessage = exceptionMessage;
        }

        public GameContextType Scope { get; }
        public string Phase { get; }
        public string OperationName { get; }
        public string? Step { get; }
        public string? Detail { get; }
        public TimeSpan Elapsed { get; }
        public RuntimeStartupOperationState State { get; }
        public string? ExceptionType { get; }
        public string? ExceptionMessage { get; }
    }

    public interface IStartupOperationProgressNotifier
    {
        void OnStartupOperationStarted(
            GameContextType scope,
            string phase,
            string operationName,
            int completedOperations,
            int totalOperations,
            TimeSpan elapsed);

        void OnStartupOperationStep(
            GameContextType scope,
            string phase,
            string operationName,
            string step,
            string? detail,
            int completedOperations,
            int totalOperations,
            TimeSpan elapsed);

        void OnStartupOperationCompleted(
            GameContextType scope,
            string phase,
            string operationName,
            int completedOperations,
            int totalOperations,
            TimeSpan elapsed);

        void OnStartupOperationFailed(
            GameContextType scope,
            string phase,
            string operationName,
            string? step,
            string? detail,
            Exception exception,
            int completedOperations,
            int totalOperations,
            TimeSpan elapsed);
    }

    public sealed class RuntimeStartupOperationException : Exception
    {
        public RuntimeStartupOperationException(
            GameContextType scope,
            string phase,
            string operationName,
            string? step,
            Exception innerException)
            : this(scope, phase, operationName, step, detail: null, innerException)
        {
        }

        public RuntimeStartupOperationException(
            GameContextType scope,
            string phase,
            string operationName,
            string? step,
            string? detail,
            Exception innerException)
            : base(CreateMessage(scope, phase, operationName, step, detail), innerException)
        {
            Scope = scope;
            Phase = phase ?? throw new ArgumentNullException(nameof(phase));
            OperationName = operationName ?? throw new ArgumentNullException(nameof(operationName));
            Step = step;
            Detail = detail;
        }

        public GameContextType Scope { get; }
        public string Phase { get; }
        public string OperationName { get; }
        public string? Step { get; }
        public string? Detail { get; }

        private static string CreateMessage(
            GameContextType scope,
            string phase,
            string operationName,
            string? step,
            string? detail)
        {
            var normalizedStep = string.IsNullOrWhiteSpace(step) ? "<none>" : step!.Trim();
            var detailPart = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" detail={detail!.Trim()}.";
            return "Startup operation failed. " +
                   $"phase={phase}, scope={scope}, operation={operationName}, step={normalizedStep}.{detailPart}";
        }
    }

    public sealed class RuntimeStartupOperationCanceledException : OperationCanceledException
    {
        public RuntimeStartupOperationCanceledException(
            GameContextType scope,
            string phase,
            string operationName,
            string? step,
            string? detail,
            OperationCanceledException innerException)
            : base(CreateMessage(scope, phase, operationName, step, detail), innerException, innerException.CancellationToken)
        {
            Scope = scope;
            Phase = phase ?? throw new ArgumentNullException(nameof(phase));
            OperationName = operationName ?? throw new ArgumentNullException(nameof(operationName));
            Step = step;
            Detail = detail;
        }

        public GameContextType Scope { get; }
        public string Phase { get; }
        public string OperationName { get; }
        public string? Step { get; }
        public string? Detail { get; }

        private static string CreateMessage(
            GameContextType scope,
            string phase,
            string operationName,
            string? step,
            string? detail)
        {
            var normalizedStep = string.IsNullOrWhiteSpace(step) ? "<none>" : step!.Trim();
            var detailPart = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" detail={detail!.Trim()}.";
            return "Startup operation canceled. " +
                   $"phase={phase}, scope={scope}, operation={operationName}, step={normalizedStep}.{detailPart}";
        }
    }
}
