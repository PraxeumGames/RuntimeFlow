using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    internal sealed class ScopeLoadingService
    {
        private readonly ActiveScopeState _activeState;
        private readonly GameContextScopeProfileStore _scopeProfiles;
        private readonly ScopeOperationCoordinator _coordinator;
        private readonly ScopeTransitionService _transitions;
        private readonly ScopeTransitionEngine _legacyTransitions;
        public ScopeLoadingService(
            ActiveScopeState activeState,
            GameContextScopeProfileStore scopeProfiles,
            ScopeOperationCoordinator coordinator,
            ScopeTransitionService transitions,
            ScopeTransitionEngine legacyTransitions)
        {
            _activeState = activeState;
            _scopeProfiles = scopeProfiles;
            _coordinator = coordinator;
            _transitions = transitions;
            _legacyTransitions = legacyTransitions;
        }

        public async Task LoadSceneAsyncCore(Type sceneScopeKey, long generation, IInitializationProgressNotifier progressNotifier, CancellationToken cancellationToken)
        {
            var sceneProfile = _scopeProfiles.GetSceneProfile(sceneScopeKey);

            await _transitions.TryActivatePreloadedScopeAsync(GameContextType.Module, sceneScopeKey, progressNotifier, generation, cancellationToken, _ => { }).ConfigureAwait(false);

            await _legacyTransitions.ExitActivatedScopeAsync(GameContextType.Module, _activeState.ModuleContext, _activeState.ActiveModuleScopeKey, ScopeLifecycleState.Deactivating, progressNotifier, cancellationToken, () => _activeState.ModuleContext = null).ConfigureAwait(false);
            await _legacyTransitions.ExitActivatedScopeAsync(GameContextType.Scene, _activeState.SceneContext, _activeState.ActiveSceneScopeKey, ScopeLifecycleState.Deactivating, progressNotifier, cancellationToken, () => _activeState.SceneContext = null).ConfigureAwait(false);

            if (await _transitions.TryActivatePreloadedScopeAsync(GameContextType.Scene, sceneScopeKey, progressNotifier, generation, cancellationToken, preloaded =>
            {
                _activeState.SceneContext = preloaded;
                _activeState.ModuleContext = null;
                _activeState.ActiveSceneScopeKey = sceneScopeKey;
                _activeState.ActiveModuleScopeKey = null;
            }).ConfigureAwait(false))
            {
                return;
            }

            var (initializedServices, availableServices) = CreateSeededState(_activeState.GlobalContext, _activeState.SessionContext);

            var sceneContext = await _transitions.EnterScopeAsync("LoadScene", GameContextType.Scene, sceneScopeKey, _activeState.SessionContext!, sceneProfile, _activeState.OnSceneInitialized, initializedServices, availableServices, progressNotifier, generation, cancellationToken).ConfigureAwait(false);

            _activeState.SceneContext = sceneContext;
            _activeState.ModuleContext = null;
            _activeState.ActiveSceneScopeKey = sceneScopeKey;
            _activeState.ActiveModuleScopeKey = null;
        }

        public async Task LoadModuleAsyncCore(Type moduleScopeKey, long generation, IInitializationProgressNotifier progressNotifier, CancellationToken cancellationToken)
        {
            var moduleProfile = _scopeProfiles.GetModuleProfile(moduleScopeKey);

            await _legacyTransitions.ExitActivatedScopeAsync(GameContextType.Module, _activeState.ModuleContext, _activeState.ActiveModuleScopeKey, ScopeLifecycleState.Deactivating, progressNotifier, cancellationToken, () => _activeState.ModuleContext = null).ConfigureAwait(false);

            if (await TryActivatePreloadedModuleScopeAsync(moduleScopeKey, progressNotifier, generation, cancellationToken).ConfigureAwait(false))
                return;

            var (initializedServices, availableServices) = CreateSeededState(_activeState.GlobalContext, _activeState.SessionContext, _activeState.SceneContext);

            var moduleContext = await _transitions.EnterScopeAsync("LoadModule", GameContextType.Module, moduleScopeKey, _activeState.SceneContext!, moduleProfile, _activeState.OnModuleInitialized, initializedServices, availableServices, progressNotifier, generation, cancellationToken).ConfigureAwait(false);

            _activeState.ModuleContext = moduleContext;
            _activeState.ActiveModuleScopeKey = moduleScopeKey;
        }

        public Task ReloadModuleAsyncCore(Type moduleScopeKey, long generation, IInitializationProgressNotifier progressNotifier, CancellationToken cancellationToken)
            => LoadModuleAsyncCore(moduleScopeKey, generation, progressNotifier, cancellationToken);

        private async Task<bool> TryActivatePreloadedModuleScopeAsync(Type moduleScopeKey, IInitializationProgressNotifier progressNotifier, long generation, CancellationToken cancellationToken)
        {
            await _coordinator.SideLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await _transitions.TryActivatePreloadedScopeAsync(GameContextType.Module, moduleScopeKey, progressNotifier, generation, cancellationToken, preloaded =>
                {
                    _activeState.ModuleContext = preloaded;
                    _activeState.ActiveModuleScopeKey = moduleScopeKey;
                }).ConfigureAwait(false);
            }
            finally { _coordinator.SideLock.Release(); }
        }

        private static (HashSet<Type> initialized, Dictionary<Type, object> available) CreateSeededState(params IGameContext?[] contexts)
        {
            var initialized = new HashSet<Type>();
            var available = new Dictionary<Type, object>();
            foreach (var ctx in contexts)
            {
                if (ctx is not GameContext gc) continue;
                foreach (var init in gc.InitializationOrder)
                {
                    initialized.Add(init.ServiceType);
                    available[init.ServiceType] = gc.Resolve(init);
                }
            }
            return (initialized, available);
        }
    }
}
