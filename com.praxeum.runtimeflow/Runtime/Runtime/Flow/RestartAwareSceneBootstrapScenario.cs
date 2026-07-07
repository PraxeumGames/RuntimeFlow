using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.SceneManagement;

namespace RuntimeFlow.Contexts
{
    internal sealed class RestartAwareSceneBootstrapScenario : IRestartAwareSceneBootstrapScenario
    {
        private readonly string _sceneName;
        private readonly IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>? _loadingState;
        private readonly Type _replayReloadScopeType;
        private readonly string _runStageName;
        private readonly string _runStartReasonCode;
        private readonly string _replayRunStartReasonCode;
        private readonly string _runCompleteReasonCode;
        private readonly string _runFailReasonCode;

        public RestartAwareSceneBootstrapScenario(RestartAwareSceneBootstrapScenarioOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            options.Validate();

            _sceneName = options.SceneName!;
            _loadingState = options.LoadingState;
            _replayReloadScopeType = options.ReplayReloadScopeType!;
            _runStageName = options.RunStageName;
            _runStartReasonCode = options.RunStartReasonCode;
            _replayRunStartReasonCode = options.ReplayRunStartReasonCode;
            _runCompleteReasonCode = options.RunCompleteReasonCode;
            _runFailReasonCode = options.RunFailReasonCode;
        }

        public async Task ExecuteAsync(IRuntimeFlowContext context, CancellationToken cancellationToken = default)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var loadingState = ResolveLoadingState(context) ?? _loadingState;
            var runReasonCode = RuntimeFlowReplayScope.IsActive
                ? _replayRunStartReasonCode
                : _runStartReasonCode;
            loadingState?.StartStage(_runStageName, runReasonCode);

            try
            {
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

        private static IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>? ResolveLoadingState(
            IRuntimeFlowContext context)
        {
            return RuntimeFlowServiceResolver.TryResolveFromContext(
                context,
                out IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>> service)
                ? service
                : null;
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
