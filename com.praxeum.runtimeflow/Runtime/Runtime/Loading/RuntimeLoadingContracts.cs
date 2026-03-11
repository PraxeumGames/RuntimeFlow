using System;

namespace RuntimeFlow.Contexts
{
    public enum RuntimeLoadingOperationKind
    {
        Initialize = 0,
        LoadScene = 1,
        LoadModule = 2,
        ReloadModule = 3,
        RunFlow = 4,
        RestartSession = 5,
        ReloadScene = 6
    }

    public static class RuntimeScopeRestartabilityPolicy
    {
        public const string GlobalScopeNonRestartableMessage = "Global scope is non-restartable by design.";

        public static bool IsRestartable(GameContextType scopeType)
        {
            return scopeType is GameContextType.Session or GameContextType.Scene or GameContextType.Module;
        }

        public static RuntimeLoadingOperationKind ResolveOperationKind(GameContextType scopeType)
        {
            return scopeType switch
            {
                GameContextType.Session => RuntimeLoadingOperationKind.RestartSession,
                GameContextType.Scene => RuntimeLoadingOperationKind.ReloadScene,
                GameContextType.Module => RuntimeLoadingOperationKind.ReloadModule,
                GameContextType.Global => throw new InvalidOperationException(GlobalScopeNonRestartableMessage),
                _ => throw new ArgumentOutOfRangeException(nameof(scopeType), scopeType, "Unsupported scope type.")
            };
        }
    }

    public enum RuntimeLoadingOperationStage
    {
        Preparing = 0,
        SceneLoading = 1,
        ScopeInitializing = 2,
        Finalizing = 3,
        Completed = 4,
        Failed = 5,
        Canceled = 6
    }

    public enum RuntimeLoadingOperationState
    {
        Pending = 0,
        Running = 1,
        Completed = 2,
        Failed = 3,
        Canceled = 4
    }

    public sealed class RuntimeLoadingOperationSnapshot
    {
        public RuntimeLoadingOperationSnapshot(
            string operationId,
            RuntimeLoadingOperationKind operationKind,
            RuntimeLoadingOperationStage stage,
            RuntimeLoadingOperationState state,
            Type? scopeKey,
            string? scopeName,
            double percent,
            int currentStep,
            int totalSteps,
            string? message,
            DateTimeOffset timestampUtc,
            string? errorType = null,
            string? errorMessage = null)
        {
            if (string.IsNullOrWhiteSpace(operationId))
                throw new ArgumentException("Operation id is required.", nameof(operationId));
            if (double.IsNaN(percent) || double.IsInfinity(percent) || percent < 0d || percent > 100d)
                throw new ArgumentOutOfRangeException(nameof(percent), percent, "Percent must be between 0 and 100.");
            if (currentStep < 0)
                throw new ArgumentOutOfRangeException(nameof(currentStep), currentStep, "Current step cannot be negative.");
            if (totalSteps < 0)
                throw new ArgumentOutOfRangeException(nameof(totalSteps), totalSteps, "Total steps cannot be negative.");
            if (currentStep > totalSteps)
                throw new ArgumentOutOfRangeException(nameof(currentStep), currentStep, "Current step cannot exceed total steps.");

            OperationId = operationId;
            OperationKind = operationKind;
            Stage = stage;
            State = state;
            ScopeKey = scopeKey;
            ScopeName = scopeName;
            Percent = percent;
            CurrentStep = currentStep;
            TotalSteps = totalSteps;
            Message = message;
            TimestampUtc = timestampUtc;
            ErrorType = errorType;
            ErrorMessage = errorMessage;
        }

        public string OperationId { get; }
        public RuntimeLoadingOperationKind OperationKind { get; }
        public RuntimeLoadingOperationStage Stage { get; }
        public RuntimeLoadingOperationState State { get; }
        public Type? ScopeKey { get; }
        public string? ScopeName { get; }
        public double Percent { get; }
        public int CurrentStep { get; }
        public int TotalSteps { get; }
        public string? Message { get; }
        public DateTimeOffset TimestampUtc { get; }
        public string? ErrorType { get; }
        public string? ErrorMessage { get; }
    }

    public interface IRuntimeLoadingProgressObserver
    {
        void OnLoadingProgress(RuntimeLoadingOperationSnapshot snapshot);
    }

    internal sealed class NullRuntimeLoadingProgressObserver : IRuntimeLoadingProgressObserver
    {
        public static readonly IRuntimeLoadingProgressObserver Instance = new NullRuntimeLoadingProgressObserver();

        public void OnLoadingProgress(RuntimeLoadingOperationSnapshot snapshot)
        {
        }
    }
}
