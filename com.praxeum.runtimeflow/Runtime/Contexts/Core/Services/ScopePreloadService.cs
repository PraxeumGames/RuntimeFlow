using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Executes preloading and additive-module operations: creating scope contexts without
    /// activation, publishing them into the preloaded/additive registries under generation
    /// guards, and tearing additive modules down on unload.
    /// </summary>
    internal sealed class ScopePreloadService
    {
        private readonly ActiveScopeState _activeState;
        private readonly GameContextScopeRegistry _scopeRegistry;
        private readonly GameContextScopeProfileStore _scopeProfiles;
        private readonly ScopeInitializationService _initService;
        private readonly ScopeTransitionService _transitions;
        private readonly Action _flushDeferredRegistrations;

        public ScopePreloadService(
            ActiveScopeState activeState,
            GameContextScopeRegistry scopeRegistry,
            GameContextScopeProfileStore scopeProfiles,
            ScopeInitializationService initService,
            ScopeTransitionService transitions,
            Action flushDeferredRegistrations)
        {
            _activeState = activeState ?? throw new ArgumentNullException(nameof(activeState));
            _scopeRegistry = scopeRegistry ?? throw new ArgumentNullException(nameof(scopeRegistry));
            _scopeProfiles = scopeProfiles ?? throw new ArgumentNullException(nameof(scopeProfiles));
            _initService = initService ?? throw new ArgumentNullException(nameof(initService));
            _transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
            _flushDeferredRegistrations = flushDeferredRegistrations ?? throw new ArgumentNullException(nameof(flushDeferredRegistrations));
        }

        public async Task PreloadSceneCoreAsync(Type sceneScopeKey, ScopeOperationCoordinator.ScopeOperationContext operation)
        {
            if (sceneScopeKey == null) throw new ArgumentNullException(nameof(sceneScopeKey));
            _flushDeferredRegistrations();
            ValidateSceneScopePreconditions(sceneScopeKey);

            var sceneProfile = _scopeProfiles.GetSceneProfile(sceneScopeKey);
            var (initializedServices, availableServices) = _initService.CreateSeededState(_activeState.GlobalContext, _activeState.SessionContext);

            var preloadedContext = await _transitions.EnterScopeAsync(
                    "PreloadScene",
                    GameContextType.Scene,
                    sceneScopeKey,
                    _activeState.SessionContext!,
                    sceneProfile,
                    _activeState.OnSceneInitialized,
                    initializedServices,
                    availableServices,
                    operation.ProgressNotifier,
                    operation.Generation,
                    operation.CancellationToken,
                    skipActivation: true,
                    verifyGenerationAfterCreate: false)
                .ConfigureAwait(false);

            await _transitions.ReplacePreloadedScopeAsync(
                    "PreloadScene",
                    GameContextType.Scene,
                    sceneScopeKey,
                    preloadedContext,
                    operation.Generation,
                    operation.CancellationToken)
                .ConfigureAwait(false);
        }

        public async Task PreloadModuleCoreAsync(Type moduleScopeKey, ScopeOperationCoordinator.ScopeOperationContext operation)
        {
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
            _flushDeferredRegistrations();
            ValidateModuleScopePreconditions(moduleScopeKey);

            var moduleProfile = _scopeProfiles.GetModuleProfile(moduleScopeKey);
            var (initializedServices, availableServices) = _initService.CreateSeededState(
                _activeState.GlobalContext,
                _activeState.SessionContext,
                _activeState.SceneContext);

            var preloadedContext = await _transitions.EnterScopeAsync(
                    "PreloadModule",
                    GameContextType.Module,
                    moduleScopeKey,
                    _activeState.SceneContext!,
                    moduleProfile,
                    _activeState.OnModuleInitialized,
                    initializedServices,
                    availableServices,
                    operation.ProgressNotifier,
                    operation.Generation,
                    operation.CancellationToken,
                    skipActivation: true,
                    verifyGenerationAfterCreate: false)
                .ConfigureAwait(false);

            await _transitions.ReplacePreloadedScopeAsync(
                    "PreloadModule",
                    GameContextType.Module,
                    moduleScopeKey,
                    preloadedContext,
                    operation.Generation,
                    operation.CancellationToken)
                .ConfigureAwait(false);
        }

        public async Task LoadAdditiveModuleCoreAsync(Type moduleScopeKey, ScopeOperationCoordinator.ScopeOperationContext operation)
        {
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
            _flushDeferredRegistrations();
            ValidateModuleScopePreconditions(moduleScopeKey);

            if (_activeState.AdditiveModuleContexts.ContainsKey(moduleScopeKey))
                throw new InvalidOperationException($"Additive module scope '{moduleScopeKey.Name}' is already loaded.");

            var moduleProfile = _scopeProfiles.GetModuleProfile(moduleScopeKey);
            var (initializedServices, availableServices) = _initService.CreateSeededState(
                _activeState.GlobalContext,
                _activeState.SessionContext,
                _activeState.SceneContext);

            var moduleContext = await _transitions.EnterScopeAsync(
                    "LoadAdditiveModule",
                    GameContextType.Module,
                    moduleScopeKey,
                    _activeState.SceneContext!,
                    moduleProfile,
                    _activeState.OnModuleInitialized,
                    initializedServices,
                    availableServices,
                    operation.ProgressNotifier,
                    operation.Generation,
                    operation.CancellationToken,
                    verifyGenerationAfterCreate: false)
                .ConfigureAwait(false);

            await _transitions.PublishAdditiveModuleScopeAsync(
                    "LoadAdditiveModule",
                    moduleScopeKey,
                    moduleContext,
                    operation.Generation,
                    operation.CancellationToken)
                .ConfigureAwait(false);
        }

        public async Task UnloadAdditiveModuleCoreAsync(Type moduleScopeKey, ScopeOperationCoordinator.ScopeOperationContext operation)
        {
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
            if (!_activeState.AdditiveModuleContexts.TryGetValue(moduleScopeKey, out var context))
                throw new InvalidOperationException($"Additive module scope '{moduleScopeKey.Name}' is not loaded.");

            await _transitions.ExitActivatedScopeAsync(
                    GameContextType.Module,
                    context,
                    moduleScopeKey,
                    ScopeLifecycleState.Deactivating,
                    operation.ProgressNotifier,
                    operation.CancellationToken,
                    () => _activeState.AdditiveModuleContexts.Remove(moduleScopeKey))
                .ConfigureAwait(false);
        }

        private void ValidateSceneScopePreconditions(Type sceneScopeKey)
        {
            if (!_scopeRegistry.TryResolveScopeType(sceneScopeKey, out _))
                throw new InvalidOperationException($"Scene scope '{sceneScopeKey.Name}' is not declared.");
            if (_activeState.SessionContext == null)
                throw new InvalidOperationException("Session context is not initialized. Call LoadSessionAsync first.");
        }

        private void ValidateModuleScopePreconditions(Type moduleScopeKey)
        {
            if (!_scopeRegistry.TryResolveScopeType(moduleScopeKey, out _))
                throw new InvalidOperationException($"Module scope '{moduleScopeKey.Name}' is not declared.");
            if (_activeState.SceneContext == null)
                throw new InvalidOperationException("Scene context is not initialized. Call LoadSceneAsync first.");
        }
    }
}
