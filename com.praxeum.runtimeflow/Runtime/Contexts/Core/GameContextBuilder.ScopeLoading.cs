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

            if (_moduleContext != null)
            {
                SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Deactivating, _activeModuleScopeKey);
                await ExecuteScopeActivationExitAsync(GameContextType.Module, _moduleContext, progressNotifier, cancellationToken).ConfigureAwait(false);
                await DisposeScopeContextAsync(
                        GameContextType.Module,
                        _moduleContext,
                        cancellationToken,
                        _activeModuleScopeKey,
                        () => SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Disposed, _activeModuleScopeKey))
                    .ConfigureAwait(false);
                _moduleContext = null;
            }

            if (_sceneContext != null)
            {
                SetScopeStateIfTracked(GameContextType.Scene, ScopeLifecycleState.Deactivating, _activeSceneScopeKey);
                await ExecuteScopeActivationExitAsync(GameContextType.Scene, _sceneContext, progressNotifier, cancellationToken).ConfigureAwait(false);
                await DisposeScopeContextAsync(
                        GameContextType.Scene,
                        _sceneContext,
                        cancellationToken,
                        _activeSceneScopeKey,
                        () => SetScopeStateIfTracked(GameContextType.Scene, ScopeLifecycleState.Disposed, _activeSceneScopeKey))
                    .ConfigureAwait(false);
                _sceneContext = null;
            }

            if (_preloadedContexts.TryGetValue(sceneScopeKey, out var preloaded))
            {
                _preloadedContexts.Remove(sceneScopeKey);
                await ExecuteScopeActivationEnterAsync(GameContextType.Scene, preloaded, progressNotifier, 0, cancellationToken).ConfigureAwait(false);
                ThrowIfStaleGeneration(generation, cancellationToken);
                _sceneContext = preloaded;
                _moduleContext = null;
                _activeSceneScopeKey = sceneScopeKey;
                _activeModuleScopeKey = null;
                SetScopeStateIfTracked(GameContextType.Scene, ScopeLifecycleState.Active, sceneScopeKey);
                return;
            }

            var (initializedServices, availableServices) = CreateSeededInitializationState(_globalContext, _sessionContext);

            GameContext? sceneContext = null;
            try
            {
                sceneContext = await CreateAndInitializeScopeContextAsync(
                        GameContextType.Scene,
                        _sessionContext!,
                        sceneProfile.Registrations,
                        sceneProfile.Services,
                        _onSceneInitialized,
                        initializedServices,
                        availableServices,
                        progressNotifier,
                        generation,
                        cancellationToken,
                        sceneScopeKey)
                    .ConfigureAwait(false);

                ThrowIfStaleGeneration(generation, cancellationToken);
                _sceneContext = sceneContext;
                _moduleContext = null;
                _activeSceneScopeKey = sceneScopeKey;
                _activeModuleScopeKey = null;
            }
            catch (Exception ex)
            {
                var cleanupFailures = await CaptureCleanupFailuresAsync(
                        cancellationToken,
                        async () =>
                        {
                            await DisposeScopeContextAsync(
                                    GameContextType.Scene,
                                    sceneContext,
                                    cancellationToken,
                                    sceneScopeKey)
                                .ConfigureAwait(false);
                            sceneContext = null;
                        })
                    .ConfigureAwait(false);

                if (cleanupFailures.Count > 0)
                    throw CreateCleanupAggregateException("LoadScene", ex, cleanupFailures);

                throw;
            }
        }

        private async Task LoadModuleAsyncCore(
            Type moduleScopeKey,
            long generation,
            IInitializationProgressNotifier progressNotifier,
            CancellationToken cancellationToken)
        {
            var moduleProfile = _scopeProfiles.GetModuleProfile(moduleScopeKey);

            if (_moduleContext != null)
            {
                SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Deactivating, _activeModuleScopeKey);
                await ExecuteScopeActivationExitAsync(GameContextType.Module, _moduleContext, progressNotifier, cancellationToken).ConfigureAwait(false);
                await DisposeScopeContextAsync(
                        GameContextType.Module,
                        _moduleContext,
                        cancellationToken,
                        _activeModuleScopeKey,
                        () => SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Disposed, _activeModuleScopeKey))
                    .ConfigureAwait(false);
                _moduleContext = null;
            }

            if (_preloadedContexts.TryGetValue(moduleScopeKey, out var preloaded))
            {
                _preloadedContexts.Remove(moduleScopeKey);
                await ExecuteScopeActivationEnterAsync(GameContextType.Module, preloaded, progressNotifier, 0, cancellationToken).ConfigureAwait(false);
                ThrowIfStaleGeneration(generation, cancellationToken);
                _moduleContext = preloaded;
                _activeModuleScopeKey = moduleScopeKey;
                SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Active, moduleScopeKey);
                return;
            }

            var (initializedServices, availableServices) = CreateSeededInitializationState(_globalContext, _sessionContext, _sceneContext);

            GameContext? moduleContext = null;
            try
            {
                moduleContext = await CreateAndInitializeScopeContextAsync(
                        GameContextType.Module,
                        _sceneContext!,
                        moduleProfile.Registrations,
                        moduleProfile.Services,
                        _onModuleInitialized,
                        initializedServices,
                        availableServices,
                        progressNotifier,
                        generation,
                        cancellationToken,
                        moduleScopeKey)
                    .ConfigureAwait(false);

                ThrowIfStaleGeneration(generation, cancellationToken);
                _moduleContext = moduleContext;
                _activeModuleScopeKey = moduleScopeKey;
            }
            catch (Exception ex)
            {
                var cleanupFailures = await CaptureCleanupFailuresAsync(
                        cancellationToken,
                        async () =>
                        {
                            await DisposeScopeContextAsync(
                                    GameContextType.Module,
                                    moduleContext,
                                    cancellationToken,
                                    moduleScopeKey)
                                .ConfigureAwait(false);
                            moduleContext = null;
                        })
                    .ConfigureAwait(false);

                if (cleanupFailures.Count > 0)
                    throw CreateCleanupAggregateException("LoadModule", ex, cleanupFailures);

                throw;
            }
        }

        private Task ReloadModuleAsyncCore(
            Type moduleScopeKey,
            long generation,
            IInitializationProgressNotifier progressNotifier,
            CancellationToken cancellationToken)
        {
            return LoadModuleAsyncCore(moduleScopeKey, generation, progressNotifier, cancellationToken);
        }
    }
}
