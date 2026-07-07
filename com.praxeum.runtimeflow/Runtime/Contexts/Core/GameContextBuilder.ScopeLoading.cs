using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    public partial class GameContextBuilder
    {
        private async Task LoadSceneAsyncCore(
            Type sceneScopeKey,
            long generation,
            IInitializationProgressNotifier progressNotifier,
            CancellationToken cancellationToken)
        {
            var sceneProfile = _scopeProfiles.GetSceneProfile(sceneScopeKey);

            await DisposeAdditiveModulesAsync(cancellationToken).ConfigureAwait(false);

            await _scopeTransitions.ExitActivatedScopeAsync(
                    GameContextType.Module,
                    _moduleContext,
                    _activeModuleScopeKey,
                    ScopeLifecycleState.Deactivating,
                    progressNotifier,
                    cancellationToken,
                    () => _moduleContext = null)
                .ConfigureAwait(false);

            await _scopeTransitions.ExitActivatedScopeAsync(
                    GameContextType.Scene,
                    _sceneContext,
                    _activeSceneScopeKey,
                    ScopeLifecycleState.Deactivating,
                    progressNotifier,
                    cancellationToken,
                    () => _sceneContext = null)
                .ConfigureAwait(false);

            if (await _scopeTransitions.TryActivatePreloadedScopeAsync(
                    GameContextType.Scene,
                    sceneScopeKey,
                    progressNotifier,
                    generation,
                    cancellationToken,
                    preloaded =>
                    {
                        _sceneContext = preloaded;
                        _moduleContext = null;
                        _activeSceneScopeKey = sceneScopeKey;
                        _activeModuleScopeKey = null;
                    }).ConfigureAwait(false))
            {
                return;
            }

            var (initializedServices, availableServices) = CreateSeededInitializationState(_globalContext, _sessionContext);

            var sceneContext = await _scopeTransitions.EnterScopeAsync(
                    "LoadScene",
                    GameContextType.Scene,
                    sceneScopeKey,
                    _sessionContext!,
                    sceneProfile,
                    _onSceneInitialized,
                    initializedServices,
                    availableServices,
                    progressNotifier,
                    generation,
                    cancellationToken)
                .ConfigureAwait(false);

            _sceneContext = sceneContext;
            _moduleContext = null;
            _activeSceneScopeKey = sceneScopeKey;
            _activeModuleScopeKey = null;
        }

        private async Task LoadModuleAsyncCore(
            Type moduleScopeKey,
            long generation,
            IInitializationProgressNotifier progressNotifier,
            CancellationToken cancellationToken)
        {
            var moduleProfile = _scopeProfiles.GetModuleProfile(moduleScopeKey);

            await _scopeTransitions.ExitActivatedScopeAsync(
                    GameContextType.Module,
                    _moduleContext,
                    _activeModuleScopeKey,
                    ScopeLifecycleState.Deactivating,
                    progressNotifier,
                    cancellationToken,
                    () => _moduleContext = null)
                .ConfigureAwait(false);

            if (await TryActivatePreloadedModuleScopeAsync(
                    moduleScopeKey,
                    progressNotifier,
                    generation,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return;
            }

            var (initializedServices, availableServices) = CreateSeededInitializationState(_globalContext, _sessionContext, _sceneContext);

            var moduleContext = await _scopeTransitions.EnterScopeAsync(
                    "LoadModule",
                    GameContextType.Module,
                    moduleScopeKey,
                    _sceneContext!,
                    moduleProfile,
                    _onModuleInitialized,
                    initializedServices,
                    availableServices,
                    progressNotifier,
                    generation,
                    cancellationToken)
                .ConfigureAwait(false);

            _moduleContext = moduleContext;
            _activeModuleScopeKey = moduleScopeKey;
        }

        private Task ReloadModuleAsyncCore(
            Type moduleScopeKey,
            long generation,
            IInitializationProgressNotifier progressNotifier,
            CancellationToken cancellationToken)
        {
            return LoadModuleAsyncCore(moduleScopeKey, generation, progressNotifier, cancellationToken);
        }

        private async Task<bool> TryActivatePreloadedModuleScopeAsync(
            Type moduleScopeKey,
            IInitializationProgressNotifier progressNotifier,
            long generation,
            CancellationToken cancellationToken)
        {
            await _sideScopeOperationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await _scopeTransitions.TryActivatePreloadedScopeAsync(
                        GameContextType.Module,
                        moduleScopeKey,
                        progressNotifier,
                        generation,
                        cancellationToken,
                        preloaded =>
                        {
                            _moduleContext = preloaded;
                            _activeModuleScopeKey = moduleScopeKey;
                        })
                    .ConfigureAwait(false);
            }
            finally
            {
                _sideScopeOperationLock.Release();
            }
        }
    }
}
