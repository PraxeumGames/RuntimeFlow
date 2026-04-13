using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.SceneManagement;

namespace RuntimeFlow.Contexts
{
    internal sealed class RestartAwareSceneBootstrapScenario : IRuntimeFlowScenario
    {
        private static readonly PreBootstrapProjectionStatusMap<PreBootstrapStageStatus> PreBootstrapStatusMap =
            new(
                notStartedStatus: PreBootstrapStageStatus.NotStarted,
                runningStatus: PreBootstrapStageStatus.Running,
                succeededStatus: PreBootstrapStageStatus.Succeeded,
                failedStatus: PreBootstrapStageStatus.Failed);

        private readonly string _sceneName;
        private readonly IPreBootstrapStageService? _preBootstrapStageService;
        private readonly IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>? _loadingState;
        private readonly Type _replayReloadScopeType;
        private readonly string _runStageName;
        private readonly string _preBootstrapStageName;
        private readonly string _runStartReasonCode;
        private readonly string _replayRunStartReasonCode;
        private readonly string _runCompleteReasonCode;
        private readonly string _runFailReasonCode;
        private readonly Func<PreBootstrapStageStatus, string, string> _preBootstrapReasonCodeResolver;
        private readonly string? _preBootstrapFailedReasonCodeFallback;
        private readonly string? _preBootstrapFailedDiagnosticFallback;
        private bool _isPreBootstrapCompleted;
        private bool _isPreBootstrapProjected;

        public RestartAwareSceneBootstrapScenario(RestartAwareSceneBootstrapScenarioOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            options.Validate();

            _sceneName = options.SceneName!;
            _preBootstrapStageService = options.PreBootstrapStageService;
            _loadingState = options.LoadingState;
            _replayReloadScopeType = options.ReplayReloadScopeType!;
            _runStageName = options.RunStageName;
            _preBootstrapStageName = options.PreBootstrapStageName;
            _runStartReasonCode = options.RunStartReasonCode;
            _replayRunStartReasonCode = options.ReplayRunStartReasonCode;
            _runCompleteReasonCode = options.RunCompleteReasonCode;
            _runFailReasonCode = options.RunFailReasonCode;
            _preBootstrapReasonCodeResolver = options.PreBootstrapReasonCodeResolver!;
            _preBootstrapFailedReasonCodeFallback = options.PreBootstrapFailedReasonCodeFallback;
            _preBootstrapFailedDiagnosticFallback = options.PreBootstrapFailedDiagnosticFallback;
        }

        public async Task ExecuteAsync(IRuntimeFlowContext context, CancellationToken cancellationToken = default)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var loadingState = ResolveLoadingState(context) ?? _loadingState;
            EnsurePreBootstrapProjection(loadingState);
            var runReasonCode = RuntimeFlowReplayScope.IsActive
                ? _replayRunStartReasonCode
                : _runStartReasonCode;
            loadingState?.StartStage(_runStageName, runReasonCode);

            try
            {
                await EnsurePreBootstrapAsync(loadingState, cancellationToken).ConfigureAwait(false);

                var sceneWasUnloadedForReplay = false;
                if (RuntimeFlowReplayScope.IsActive)
                {
                    sceneWasUnloadedForReplay = await PrepareSceneForReplayAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                if (sceneWasUnloadedForReplay
                    || !await RuntimeFlowSceneUtilities.IsSceneLoadedAsync(_sceneName, cancellationToken).ConfigureAwait(false))
                {
                    await context.LoadSceneAdditiveAsync(_sceneName, cancellationToken).ConfigureAwait(false);
                }

                if (RuntimeFlowReplayScope.IsActive)
                {
                    await context.ReloadScopeAsync(_replayReloadScopeType, cancellationToken).ConfigureAwait(false);
                    loadingState = ResolveLoadingState(context) ?? loadingState;
                    loadingState?.CompleteStage(_runStageName, _runCompleteReasonCode);
                    return;
                }

                await context.InitializeAsync(cancellationToken).ConfigureAwait(false);
                loadingState?.CompleteStage(_runStageName, _runCompleteReasonCode);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                loadingState?.FailStage(_runStageName, _runFailReasonCode, ex, ex.Message);
                throw;
            }
        }

        private async Task EnsurePreBootstrapAsync(
            IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>? loadingState,
            CancellationToken cancellationToken)
        {
            if (_isPreBootstrapCompleted || _preBootstrapStageService == null)
            {
                return;
            }

            try
            {
                await _preBootstrapStageService.EnsureCompletedAsync(cancellationToken).ConfigureAwait(false);
                _isPreBootstrapCompleted = true;
                ProjectPreBootstrapState(loadingState);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                ProjectPreBootstrapState(loadingState);
                throw;
            }
        }

        private void EnsurePreBootstrapProjection(
            IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>? loadingState)
        {
            if (_isPreBootstrapProjected || loadingState == null || _preBootstrapStageService == null)
            {
                return;
            }

            ProjectPreBootstrapState(loadingState);
            _isPreBootstrapProjected = true;
        }

        private static IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>? ResolveLoadingState(
            IRuntimeFlowContext context)
        {
            return RuntimeFlowServiceResolver.TryResolveFromContext(
                context,
                out IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>> service)
                ? service
                : null;
        }

        private void ProjectPreBootstrapState(
            IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>? loadingState)
        {
            if (loadingState == null || _preBootstrapStageService == null)
            {
                return;
            }

            PreBootstrapPipelineStageProjector.Project(
                preBootstrapStageService: _preBootstrapStageService,
                pipelineState: loadingState,
                pipelineStage: _preBootstrapStageName,
                statusMap: PreBootstrapStatusMap,
                reasonCodeResolver: _preBootstrapReasonCodeResolver,
                failedReasonCodeFallback: _preBootstrapFailedReasonCodeFallback,
                failedDiagnosticFallback: _preBootstrapFailedDiagnosticFallback);
        }

        private async Task<bool> PrepareSceneForReplayAsync(CancellationToken cancellationToken)
        {
            var scene = await RuntimeFlowSceneUtilities.ExecuteOnMainThreadAsync(
                    () => SceneManager.GetSceneByName(_sceneName),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!scene.IsValid()
                || !scene.isLoaded
                || !await CanUnloadLoadedSceneAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            var unloadOperation = await RuntimeFlowSceneUtilities.ExecuteOnMainThreadAsync(
                    () => SceneManager.UnloadSceneAsync(scene),
                    cancellationToken)
                .ConfigureAwait(false);
            if (unloadOperation == null)
            {
                return false;
            }

            while (!await RuntimeFlowSceneUtilities.ExecuteOnMainThreadAsync(
                       () => unloadOperation.isDone,
                       cancellationToken)
                   .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            await Task.Yield();
            return true;
        }

        private static Task<bool> CanUnloadLoadedSceneAsync(CancellationToken cancellationToken)
        {
            return RuntimeFlowSceneUtilities.ExecuteOnMainThreadAsync(() =>
            {
                var loadedScenes = 0;
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    if (SceneManager.GetSceneAt(i).isLoaded)
                    {
                        loadedScenes++;
                    }
                }

                return loadedScenes > 1;
            }, cancellationToken);
        }
    }
}
