using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RuntimeFlow.Contexts
{
    public sealed partial class RuntimePipeline
    {
        public async Task<IGameContext> InitializeAsync(
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            _logger.LogInformation("Pipeline initializing");
            var result = await ExecuteScopeOperationAsync(
                RuntimeLoadingOperationKind.Initialize,
                RuntimeOperationCodes.Initialize,
                scopeKey: null,
                RuntimeExecutionState.Initializing,
                splitPerScope: true,
                startMessage: "Initializing runtime contexts.",
                successMessage: "Runtime contexts initialized.",
                cancelMessage: "Initialization canceled by caller.",
                failMessage: "Initialization failed.",
                (notifier, ct) => _builder.BuildAsync(notifier, ct),
                progressNotifier,
                cancellationToken).ConfigureAwait(false);
            return result;
        }

        public Task LoadSceneAsync<TSceneScope>(
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            return LoadSceneAsync(typeof(TSceneScope), progressNotifier, cancellationToken);
        }

        public async Task LoadSceneAsync(
            Type sceneScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (sceneScopeKey == null) throw new ArgumentNullException(nameof(sceneScopeKey));
            _logger.LogInformation("Loading scene {ScopeType}", sceneScopeKey.Name);
            await ExecuteTransitionScopeOperationAsync(
                    scopeKey: sceneScopeKey,
                    sourceScopeType: _builder.ActiveSceneScopeKey != null ? GameContextType.Scene : GameContextType.Session,
                    sourceScopeKey: _builder.ActiveSceneScopeKey,
                    targetScopeType: GameContextType.Scene,
                    guardStage: RuntimeFlowGuardStage.BeforeSceneLoad,
                    operationKind: RuntimeLoadingOperationKind.LoadScene,
                    operationCode: RuntimeOperationCodes.LoadScene,
                    startMessage: $"Loading scene scope '{sceneScopeKey.Name}'.",
                    successMessage: $"Scene scope '{sceneScopeKey.Name}' loaded.",
                    cancelMessage: "Scene loading canceled by caller.",
                    failMessage: $"Failed to load scene scope '{sceneScopeKey.Name}'.",
                    operation: (notifier, ct) => _builder.LoadSceneAsync(sceneScopeKey, notifier, ct),
                    progressNotifier: progressNotifier,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        public Task LoadModuleAsync<TModuleScope>(
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            return LoadModuleAsync(typeof(TModuleScope), progressNotifier, cancellationToken);
        }

        public async Task LoadModuleAsync(
            Type moduleScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
            _logger.LogInformation("Loading module {ScopeType}", moduleScopeKey.Name);
            await ExecuteTransitionScopeOperationAsync(
                    scopeKey: moduleScopeKey,
                    sourceScopeType: _builder.ActiveModuleScopeKey != null ? GameContextType.Module : GameContextType.Scene,
                    sourceScopeKey: _builder.ActiveModuleScopeKey ?? _builder.ActiveSceneScopeKey,
                    targetScopeType: GameContextType.Module,
                    guardStage: RuntimeFlowGuardStage.BeforeModuleLoad,
                    operationKind: RuntimeLoadingOperationKind.LoadModule,
                    operationCode: RuntimeOperationCodes.LoadModule,
                    startMessage: $"Loading module scope '{moduleScopeKey.Name}'.",
                    successMessage: $"Module scope '{moduleScopeKey.Name}' loaded.",
                    cancelMessage: "Module loading canceled by caller.",
                    failMessage: $"Failed to load module scope '{moduleScopeKey.Name}'.",
                    operation: (notifier, ct) => _builder.LoadModuleAsync(moduleScopeKey, notifier, ct),
                    progressNotifier: progressNotifier,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        public Task PreloadSceneAsync<TSceneScope>(
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            return PreloadSceneAsync(typeof(TSceneScope), progressNotifier, cancellationToken);
        }

        public async Task PreloadSceneAsync(
            Type sceneScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (sceneScopeKey == null) throw new ArgumentNullException(nameof(sceneScopeKey));
            await _builder.PreloadSceneAsync(sceneScopeKey, progressNotifier, cancellationToken).ConfigureAwait(false);
        }

        public Task PreloadModuleAsync<TModuleScope>(
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            return PreloadModuleAsync(typeof(TModuleScope), progressNotifier, cancellationToken);
        }

        public async Task PreloadModuleAsync(
            Type moduleScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
            await _builder.PreloadModuleAsync(moduleScopeKey, progressNotifier, cancellationToken).ConfigureAwait(false);
        }

        public bool HasPreloadedScope<TScope>()
        {
            return HasPreloadedScope(typeof(TScope));
        }

        public bool HasPreloadedScope(Type scopeKey)
        {
            if (scopeKey == null) throw new ArgumentNullException(nameof(scopeKey));
            return _builder.HasPreloadedScope(scopeKey);
        }

        public Task LoadAdditiveModuleAsync<TModuleScope>(
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            return LoadAdditiveModuleAsync(typeof(TModuleScope), progressNotifier, cancellationToken);
        }

        public async Task LoadAdditiveModuleAsync(
            Type moduleScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
            await _builder.LoadAdditiveModuleAsync(moduleScopeKey, progressNotifier, cancellationToken).ConfigureAwait(false);
        }

        public Task UnloadAdditiveModuleAsync<TModuleScope>(
            CancellationToken cancellationToken = default)
        {
            return UnloadAdditiveModuleAsync(typeof(TModuleScope), cancellationToken);
        }

        public async Task UnloadAdditiveModuleAsync(
            Type moduleScopeKey,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
            await _builder.UnloadAdditiveModuleAsync(moduleScopeKey, cancellationToken).ConfigureAwait(false);
        }

        public Task ReloadModuleAsync<TModuleScope>(
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            return ReloadModuleAsync(typeof(TModuleScope), progressNotifier, cancellationToken);
        }

        public Task ReloadScopeAsync<TScope>(
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            return ReloadScopeAsync(typeof(TScope), progressNotifier, cancellationToken);
        }

        public async Task ReloadScopeAsync(
            Type scopeType,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (scopeType == null) throw new ArgumentNullException(nameof(scopeType));
            if (!_builder.TryResolveScopeType(scopeType, out var scope))
                throw CreateScopeTypeNotDeclaredException(scopeType);
            _logger.LogInformation("Reloading scope {ScopeType}", scopeType.Name);
            await EvaluateGuardsAsync(RuntimeFlowGuardStage.BeforeScopeReload, scopeType, scope, cancellationToken).ConfigureAwait(false);

            await (scope switch
            {
                GameContextType.Session => RestartSessionAsync(progressNotifier, cancellationToken),
                GameContextType.Scene => ReloadSceneAsync(scopeType, progressNotifier, cancellationToken),
                GameContextType.Module => ReloadModuleAsync(scopeType, progressNotifier, cancellationToken),
                GameContextType.Global => throw new ScopeNotRestartableException(scopeType),
                _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported scope type.")
            }).ConfigureAwait(false);
        }

        public async Task ReloadModuleAsync(
            Type moduleScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
            await EvaluateGuardsAsync(RuntimeFlowGuardStage.BeforeScopeReload, moduleScopeKey, GameContextType.Module, cancellationToken).ConfigureAwait(false);
            await ExecuteScopeOperationAsync(
                RuntimeLoadingOperationKind.ReloadModule,
                RuntimeOperationCodes.ReloadModule,
                scopeKey: moduleScopeKey,
                RuntimeExecutionState.Initializing,
                splitPerScope: false,
                startMessage: $"Reloading module scope '{moduleScopeKey.Name}'.",
                successMessage: $"Module scope '{moduleScopeKey.Name}' reloaded.",
                cancelMessage: "Module reloading canceled by caller.",
                failMessage: $"Failed to reload module scope '{moduleScopeKey.Name}'.",
                (notifier, ct) => _builder.ReloadModuleAsync(moduleScopeKey, notifier, ct),
                progressNotifier,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task ReloadSceneAsync(
            Type sceneScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            if (sceneScopeKey == null) throw new ArgumentNullException(nameof(sceneScopeKey));
            await ExecuteScopeOperationAsync(
                RuntimeLoadingOperationKind.ReloadScene,
                RuntimeOperationCodes.ReloadScene,
                scopeKey: sceneScopeKey,
                RuntimeExecutionState.Initializing,
                splitPerScope: false,
                startMessage: $"Reloading scene scope '{sceneScopeKey.Name}'.",
                successMessage: $"Scene scope '{sceneScopeKey.Name}' reloaded.",
                cancelMessage: "Scene reloading canceled by caller.",
                failMessage: $"Failed to reload scene scope '{sceneScopeKey.Name}'.",
                (notifier, ct) => _builder.LoadSceneAsync(sceneScopeKey, notifier, ct),
                progressNotifier,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task RestartSessionAsync(
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            _logger.LogInformation("Restarting session");
            if (_replayFlowOnSessionRestart && _flow != null && _sceneLoader != null)
            {
                await EvaluateGuardsAsync(RuntimeFlowGuardStage.BeforeSessionRestart, null, GameContextType.Session, cancellationToken);
                await RestartSessionByReplayingFlowAsync(progressNotifier ?? _defaultProgressNotifier, cancellationToken);
                return;
            }

            await EvaluateGuardsAsync(RuntimeFlowGuardStage.BeforeSessionRestart, null, GameContextType.Session, cancellationToken).ConfigureAwait(false);
            await ExecuteScopeOperationAsync(
                RuntimeLoadingOperationKind.RestartSession,
                RuntimeOperationCodes.RestartSession,
                scopeKey: null,
                RuntimeExecutionState.Recovering,
                splitPerScope: true,
                startMessage: "Restarting session.",
                successMessage: "Session restarted.",
                cancelMessage: "Session restart canceled by caller.",
                failMessage: "Session restart failed.",
                (notifier, ct) => _builder.RestartSessionAsync(notifier, ct),
                progressNotifier,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task ExecuteTransitionScopeOperationAsync(
            Type scopeKey,
            GameContextType sourceScopeType,
            Type? sourceScopeKey,
            GameContextType targetScopeType,
            RuntimeFlowGuardStage guardStage,
            RuntimeLoadingOperationKind operationKind,
            string operationCode,
            string startMessage,
            string successMessage,
            string cancelMessage,
            string failMessage,
            Func<IInitializationProgressNotifier, CancellationToken, Task> operation,
            IInitializationProgressNotifier? progressNotifier,
            CancellationToken cancellationToken)
        {
            await EvaluateGuardsAsync(guardStage, scopeKey, targetScopeType, cancellationToken).ConfigureAwait(false);

            var transitionContext = new ScopeTransitionContext(
                sourceScopeType,
                sourceScopeKey,
                targetScopeType,
                scopeKey);

            await _transitionHandler.OnTransitionOutAsync(transitionContext, cancellationToken).ConfigureAwait(false);
            await _transitionHandler.OnTransitionProgressAsync(transitionContext, 0f, cancellationToken).ConfigureAwait(false);

            await ExecuteScopeOperationAsync(
                operationKind,
                operationCode,
                scopeKey,
                RuntimeExecutionState.Initializing,
                splitPerScope: false,
                startMessage,
                successMessage,
                cancelMessage,
                failMessage,
                operation,
                progressNotifier,
                cancellationToken).ConfigureAwait(false);

            await _transitionHandler.OnTransitionProgressAsync(transitionContext, 1f, cancellationToken).ConfigureAwait(false);
            await _transitionHandler.OnTransitionInAsync(transitionContext, cancellationToken).ConfigureAwait(false);
        }
    }
}
