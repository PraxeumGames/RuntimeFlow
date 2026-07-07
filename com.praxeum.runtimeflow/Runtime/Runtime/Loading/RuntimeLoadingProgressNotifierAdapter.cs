using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    public sealed class RuntimeLoadingProgressNotifierAdapter :
        IInitializationProgressNotifier,
        IRuntimeScopeLifecycleProgressNotifier,
        IStartupOperationProgressNotifier
    {
        private readonly IRuntimeLoadingProgressObserver _observer;
        private readonly RuntimeLoadingOperationKind _operationKind;
        private readonly string _operationId;
        private readonly Func<DateTimeOffset> _timestampProvider;
        private readonly bool _splitOperationPerScope;
        private readonly object _sync = new();
        private int _scopeSequence;
        private string? _activeScopeOperationId;
        private GameContextType? _activeScope;
        private RuntimeStartupSnapshot? _currentStartupOperation;
        private RuntimeStartupSnapshot? _lastStartupOperation;

        public RuntimeLoadingProgressNotifierAdapter(
            IRuntimeLoadingProgressObserver? observer,
            RuntimeLoadingOperationKind operationKind = RuntimeLoadingOperationKind.Initialize,
            string? operationId = null,
            Func<DateTimeOffset>? timestampProvider = null,
            bool splitOperationPerScope = false)
        {
            _observer = observer ?? NullRuntimeLoadingProgressObserver.Instance;
            _operationKind = operationKind;
            _operationId = string.IsNullOrWhiteSpace(operationId) ? Guid.NewGuid().ToString("N") : operationId;
            _timestampProvider = timestampProvider ?? (() => DateTimeOffset.UtcNow);
            _splitOperationPerScope = splitOperationPerScope;
        }

        public RuntimeStartupSnapshot? CurrentStartupOperation
        {
            get
            {
                lock (_sync)
                    return _currentStartupOperation;
            }
        }

        public RuntimeStartupSnapshot? LastStartupOperation
        {
            get
            {
                lock (_sync)
                    return _lastStartupOperation;
            }
        }

        public void OnScopeStarted(GameContextType scope, int totalServices)
        {
            var totalSteps = Math.Max(0, totalServices);
            var operationId = BeginScopeOperation(scope);
            if (_splitOperationPerScope && !string.Equals(operationId, _operationId, StringComparison.Ordinal))
            {
                PublishSnapshot(
                    operationId,
                    scope,
                    stage: RuntimeLoadingOperationStage.Preparing,
                    state: RuntimeLoadingOperationState.Running,
                    currentStep: 0,
                    totalSteps: totalSteps,
                    message: $"Scope '{scope}' initialization preparing.");
            }
            PublishSnapshot(
                operationId,
                scope,
                stage: RuntimeLoadingOperationStage.ScopeInitializing,
                state: RuntimeLoadingOperationState.Running,
                currentStep: 0,
                totalSteps: totalSteps,
                message: $"Scope '{scope}' initialization started.");
        }

        public void OnServiceStarted(GameContextType scope, Type serviceType, int completedServices, int totalServices)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));

            var totalSteps = Math.Max(0, totalServices);
            var currentStep = Math.Clamp(completedServices, 0, totalSteps);
            var operationId = ResolveScopeOperationId(scope);
            PublishSnapshot(
                operationId,
                scope,
                stage: RuntimeLoadingOperationStage.ScopeInitializing,
                state: RuntimeLoadingOperationState.Running,
                currentStep: currentStep,
                totalSteps: totalSteps,
                message: $"Service '{serviceType.Name}' initialization started.");
        }

        public void OnServiceCompleted(GameContextType scope, Type serviceType, int completedServices, int totalServices)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));

            var totalSteps = Math.Max(0, totalServices);
            var currentStep = Math.Clamp(completedServices, 0, totalSteps);
            var operationId = ResolveScopeOperationId(scope);
            PublishSnapshot(
                operationId,
                scope,
                stage: RuntimeLoadingOperationStage.ScopeInitializing,
                state: RuntimeLoadingOperationState.Running,
                currentStep: currentStep,
                totalSteps: totalSteps,
                message: $"Service '{serviceType.Name}' initialization completed.");
        }

        public void OnScopeCompleted(GameContextType scope, int totalServices)
        {
            var totalSteps = Math.Max(0, totalServices);
            var operationId = ResolveScopeOperationId(scope);
            PublishSnapshot(
                operationId,
                scope,
                stage: RuntimeLoadingOperationStage.Completed,
                state: RuntimeLoadingOperationState.Completed,
                currentStep: totalSteps,
                totalSteps: totalSteps,
                message: $"Scope '{scope}' initialization completed.");
            CompleteScopeOperation(scope);
        }

        public void OnServiceProgress(GameContextType scope, Type serviceType, float progress, string? message, int completedServices, int totalServices)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));

            var totalSteps = Math.Max(0, totalServices);
            var fractionalStep = Math.Clamp(completedServices + progress, 0, totalSteps);
            var operationId = ResolveScopeOperationId(scope);
            PublishSnapshot(
                operationId,
                scope,
                stage: RuntimeLoadingOperationStage.ScopeInitializing,
                state: RuntimeLoadingOperationState.Running,
                currentStep: (int)fractionalStep,
                totalSteps: totalSteps,
                message: message ?? $"Service '{serviceType.Name}' progress {(int)(progress * 100)}%.");
        }

        public Task OnGlobalContextReadyForSessionInitializationAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task OnSessionRestartTeardownCompletedAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public void OnScopeActivationStarted(GameContextType scope, int currentStep, int totalSteps)
        {
            var operationId = ResolveScopeOperationId(scope);
            var normalized = NormalizeLifecycleProgress(currentStep, totalSteps);
            PublishSnapshot(
                operationId,
                scope,
                stage: RuntimeLoadingOperationStage.Finalizing,
                state: RuntimeLoadingOperationState.Running,
                currentStep: normalized.CurrentStep,
                totalSteps: normalized.TotalSteps,
                message: $"Scope '{scope}' activation started.");
        }

        public void OnScopeActivationCompleted(GameContextType scope, int currentStep, int totalSteps)
        {
            var operationId = ResolveScopeOperationId(scope);
            var normalized = NormalizeLifecycleProgress(currentStep, totalSteps);
            PublishSnapshot(
                operationId,
                scope,
                stage: RuntimeLoadingOperationStage.Finalizing,
                state: RuntimeLoadingOperationState.Running,
                currentStep: normalized.CurrentStep,
                totalSteps: normalized.TotalSteps,
                message: $"Scope '{scope}' activation completed.");
        }

        public void OnScopeDeactivationStarted(GameContextType scope)
        {
            PublishSnapshot(
                ResolveDeactivationOperationId(scope),
                scope,
                stage: RuntimeLoadingOperationStage.ScopeInitializing,
                state: RuntimeLoadingOperationState.Running,
                currentStep: 0,
                totalSteps: 0,
                message: $"Scope '{scope}' deactivation started.");
        }

        public void OnScopeDeactivationCompleted(GameContextType scope)
        {
            PublishSnapshot(
                ResolveDeactivationOperationId(scope),
                scope,
                stage: RuntimeLoadingOperationStage.ScopeInitializing,
                state: RuntimeLoadingOperationState.Running,
                currentStep: 0,
                totalSteps: 0,
                message: $"Scope '{scope}' deactivation completed.");
        }

        public void OnStartupOperationStarted(
            GameContextType scope,
            string phase,
            string operationName,
            int completedOperations,
            int totalOperations,
            TimeSpan elapsed)
        {
            SetCurrentStartupOperation(
                scope,
                phase,
                operationName,
                step: null,
                detail: null,
                elapsed,
                RuntimeStartupOperationState.Started);
            var normalized = NormalizeLifecycleProgress(completedOperations, totalOperations);
            PublishSnapshot(
                ResolveScopeOperationId(scope),
                scope,
                stage: RuntimeLoadingOperationStage.ScopeInitializing,
                state: RuntimeLoadingOperationState.Running,
                currentStep: normalized.CurrentStep,
                totalSteps: normalized.TotalSteps,
                message: $"phase={phase} operation={operationName} started");
        }

        public void OnStartupOperationStep(
            GameContextType scope,
            string phase,
            string operationName,
            string step,
            string? detail,
            int completedOperations,
            int totalOperations,
            TimeSpan elapsed)
        {
            SetCurrentStartupOperation(
                scope,
                phase,
                operationName,
                step,
                detail,
                elapsed,
                RuntimeStartupOperationState.Step);
            var normalized = NormalizeLifecycleProgress(completedOperations, totalOperations);
            PublishSnapshot(
                ResolveScopeOperationId(scope),
                scope,
                stage: RuntimeLoadingOperationStage.ScopeInitializing,
                state: RuntimeLoadingOperationState.Running,
                currentStep: normalized.CurrentStep,
                totalSteps: normalized.TotalSteps,
                message: FormatStartupOperationMessage(phase, operationName, step, detail));
        }

        public void OnStartupOperationCompleted(
            GameContextType scope,
            string phase,
            string operationName,
            int completedOperations,
            int totalOperations,
            TimeSpan elapsed)
        {
            CompleteStartupOperationPreservingCurrent(
                scope,
                phase,
                operationName,
                elapsed);
            var normalized = NormalizeLifecycleProgress(completedOperations, totalOperations);
            PublishSnapshot(
                ResolveScopeOperationId(scope),
                scope,
                stage: RuntimeLoadingOperationStage.ScopeInitializing,
                state: RuntimeLoadingOperationState.Running,
                currentStep: normalized.CurrentStep,
                totalSteps: normalized.TotalSteps,
                message: $"phase={phase} operation={operationName} completed");
        }

        public void OnStartupOperationFailed(
            GameContextType scope,
            string phase,
            string operationName,
            string? step,
            string? detail,
            Exception exception,
            int completedOperations,
            int totalOperations,
            TimeSpan elapsed)
        {
            var isCanceled = IsCancellationFailure(exception);
            CompleteStartupOperation(
                new RuntimeStartupSnapshot(
                    scope,
                    phase,
                    operationName,
                    step,
                    detail,
                    elapsed,
                    isCanceled
                        ? RuntimeStartupOperationState.Canceled
                        : RuntimeStartupOperationState.Failed,
                    exception.GetType().Name,
                    exception.Message));
            var normalized = NormalizeLifecycleProgress(completedOperations, totalOperations);
            PublishSnapshot(
                ResolveScopeOperationId(scope),
                scope,
                stage: isCanceled ? RuntimeLoadingOperationStage.Canceled : RuntimeLoadingOperationStage.Failed,
                state: isCanceled ? RuntimeLoadingOperationState.Canceled : RuntimeLoadingOperationState.Failed,
                currentStep: normalized.CurrentStep,
                totalSteps: normalized.TotalSteps,
                message: FormatStartupOperationMessage(phase, operationName, step ?? "<none>", detail),
                error: exception);
        }

        private void SetCurrentStartupOperation(
            GameContextType scope,
            string phase,
            string operationName,
            string? step,
            string? detail,
            TimeSpan elapsed,
            RuntimeStartupOperationState state)
        {
            lock (_sync)
            {
                _currentStartupOperation = new RuntimeStartupSnapshot(
                    scope,
                    phase,
                    operationName,
                    step,
                    detail,
                    elapsed,
                    state);
            }
        }

        private void CompleteStartupOperation(RuntimeStartupSnapshot snapshot)
        {
            lock (_sync)
            {
                _lastStartupOperation = snapshot;
                _currentStartupOperation = null;
            }
        }

        private void CompleteStartupOperationPreservingCurrent(
            GameContextType scope,
            string phase,
            string operationName,
            TimeSpan elapsed)
        {
            lock (_sync)
            {
                _lastStartupOperation = new RuntimeStartupSnapshot(
                    scope,
                    phase,
                    operationName,
                    _currentStartupOperation?.Step ?? "completed",
                    _currentStartupOperation?.Detail,
                    elapsed,
                    RuntimeStartupOperationState.Completed);
                _currentStartupOperation = null;
            }
        }

        private void PublishSnapshot(
            string operationId,
            GameContextType scope,
            RuntimeLoadingOperationStage stage,
            RuntimeLoadingOperationState state,
            int currentStep,
            int totalSteps,
            string message,
            Exception? error = null)
        {
            var snapshot = new RuntimeLoadingOperationSnapshot(
                operationId: operationId,
                operationKind: _operationKind,
                stage: stage,
                state: state,
                scopeKey: ResolveScopeKey(scope),
                scopeName: scope.ToString(),
                percent: CalculatePercent(currentStep, totalSteps, state),
                currentStep: currentStep,
                totalSteps: totalSteps,
                message: message,
                timestampUtc: _timestampProvider(),
                errorType: error?.GetType().Name,
                errorMessage: error?.Message);

            _observer.OnLoadingProgress(snapshot);
        }

        private string BeginScopeOperation(GameContextType scope)
        {
            if (!_splitOperationPerScope)
                return _operationId;

            lock (_sync)
            {
                _scopeSequence++;
                _activeScope = scope;
                _activeScopeOperationId = _scopeSequence == 1
                    ? _operationId
                    : $"{_operationId}-s{_scopeSequence:D2}";
                return _activeScopeOperationId;
            }
        }

        private string ResolveScopeOperationId(GameContextType scope)
        {
            if (!_splitOperationPerScope)
                return _operationId;

            lock (_sync)
            {
                if (_activeScopeOperationId != null && _activeScope == scope)
                    return _activeScopeOperationId;

                _scopeSequence++;
                _activeScope = scope;
                _activeScopeOperationId = $"{_operationId}-s{_scopeSequence:D2}";
                return _activeScopeOperationId;
            }
        }

        private void CompleteScopeOperation(GameContextType scope)
        {
            if (!_splitOperationPerScope)
                return;

            lock (_sync)
            {
                if (_activeScope != scope)
                    return;

                _activeScope = null;
                _activeScopeOperationId = null;
            }
        }

        private string ResolveDeactivationOperationId(GameContextType scope)
        {
            if (!_splitOperationPerScope)
                return _operationId;

            lock (_sync)
            {
                if (_activeScope == scope && _activeScopeOperationId != null)
                    return _activeScopeOperationId;

                return _operationId;
            }
        }

        private static (int CurrentStep, int TotalSteps) NormalizeLifecycleProgress(int currentStep, int totalSteps)
        {
            var normalizedTotalSteps = Math.Max(0, totalSteps);
            var normalizedCurrentStep = Math.Clamp(currentStep, 0, normalizedTotalSteps);
            return (normalizedCurrentStep, normalizedTotalSteps);
        }

        private static double CalculatePercent(int currentStep, int totalSteps, RuntimeLoadingOperationState state)
        {
            if (totalSteps == 0)
                return state == RuntimeLoadingOperationState.Completed ? 100d : 0d;

            return currentStep * 100d / totalSteps;
        }

        private static string FormatStartupOperationMessage(
            string phase,
            string operationName,
            string step,
            string? detail)
        {
            var message = $"phase={phase} operation={operationName} step={step}";
            return string.IsNullOrWhiteSpace(detail)
                ? message
                : $"{message} detail={detail.Trim()}";
        }

        private static bool IsCancellationFailure(Exception exception)
        {
            return exception is OperationCanceledException
                   || exception is RuntimeStartupOperationException { InnerException: OperationCanceledException };
        }

        private static Type ResolveScopeKey(GameContextType scope)
        {
            return scope switch
            {
                GameContextType.Global => typeof(IGlobalInitializableService),
                GameContextType.Session => typeof(ISessionInitializableService),
                GameContextType.Scene => typeof(ISceneInitializableService),
                GameContextType.Module => typeof(IModuleInitializableService),
                _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported scope type.")
            };
        }
    }
}
