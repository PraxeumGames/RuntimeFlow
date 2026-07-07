using System;
using System.Threading;
using System.Threading.Tasks;
using VContainer;

namespace RuntimeFlow.Contexts
{
    public partial class GameContextBuilder
    {
        public async Task PreloadSceneAsync(
            Type sceneScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            await ExecuteGenerationBoundSideScopeOperationAsync(
                    progressNotifier,
                    cancellationToken,
                    async operation =>
                    {
                        FlushDeferredScopedRegistrations();
                        ValidateSceneScopeOperationPreconditions(sceneScopeKey);

                        var sceneProfile = _scopeProfiles.GetSceneProfile(sceneScopeKey);

                        var (initializedServices, availableServices) = CreateSeededInitializationState(_globalContext, _sessionContext);

                        var preloadedContext = await _scopeTransitions.EnterScopeAsync(
                                "PreloadScene",
                                GameContextType.Scene,
                                sceneScopeKey,
                                _sessionContext!,
                                sceneProfile,
                                _onSceneInitialized,
                                initializedServices,
                                availableServices,
                                operation.ProgressNotifier,
                                operation.Generation,
                                operation.CancellationToken,
                                skipActivation: true,
                                verifyGenerationAfterCreate: false)
                            .ConfigureAwait(false);

                        await _scopeTransitions.ReplacePreloadedScopeAsync(
                                "PreloadScene",
                                GameContextType.Scene,
                                sceneScopeKey,
                                preloadedContext,
                                operation.Generation,
                                operation.CancellationToken)
                            .ConfigureAwait(false);
                    })
                .ConfigureAwait(false);
        }

        public async Task PreloadModuleAsync(
            Type moduleScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            await ExecuteGenerationBoundSideScopeOperationAsync(
                    progressNotifier,
                    cancellationToken,
                    async operation =>
                    {
                        FlushDeferredScopedRegistrations();
                        ValidateModuleScopeOperationPreconditions(moduleScopeKey);

                        var moduleProfile = _scopeProfiles.GetModuleProfile(moduleScopeKey);

                        var (initializedServices, availableServices) = CreateSeededInitializationState(
                            _globalContext,
                            _sessionContext,
                            _sceneContext);

                        var preloadedContext = await _scopeTransitions.EnterScopeAsync(
                                "PreloadModule",
                                GameContextType.Module,
                                moduleScopeKey,
                                _sceneContext!,
                                moduleProfile,
                                _onModuleInitialized,
                                initializedServices,
                                availableServices,
                                operation.ProgressNotifier,
                                operation.Generation,
                                operation.CancellationToken,
                                skipActivation: true,
                                verifyGenerationAfterCreate: false)
                            .ConfigureAwait(false);

                        await _scopeTransitions.ReplacePreloadedScopeAsync(
                                "PreloadModule",
                                GameContextType.Module,
                                moduleScopeKey,
                                preloadedContext,
                                operation.Generation,
                                operation.CancellationToken)
                            .ConfigureAwait(false);
                    })
                .ConfigureAwait(false);
        }

        public bool HasPreloadedScope(Type scopeKey)
        {
            if (scopeKey == null) throw new ArgumentNullException(nameof(scopeKey));
            return _preloadedContexts.ContainsKey(scopeKey);
        }

        public async Task LoadAdditiveModuleAsync(
            Type moduleScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            await ExecuteGenerationBoundSideScopeOperationAsync(
                    progressNotifier,
                    cancellationToken,
                    async operation =>
                    {
                        FlushDeferredScopedRegistrations();
                        ValidateModuleScopeOperationPreconditions(moduleScopeKey);

                        if (_additiveModuleContexts.ContainsKey(moduleScopeKey))
                            throw new InvalidOperationException($"Additive module scope '{moduleScopeKey.Name}' is already loaded.");

                        var moduleProfile = _scopeProfiles.GetModuleProfile(moduleScopeKey);

                        var (initializedServices, availableServices) = CreateSeededInitializationState(
                            _globalContext,
                            _sessionContext,
                            _sceneContext);

                        var moduleContext = await _scopeTransitions.EnterScopeAsync(
                                "LoadAdditiveModule",
                                GameContextType.Module,
                                moduleScopeKey,
                                _sceneContext!,
                                moduleProfile,
                                _onModuleInitialized,
                                initializedServices,
                                availableServices,
                                operation.ProgressNotifier,
                                operation.Generation,
                                operation.CancellationToken,
                                verifyGenerationAfterCreate: false)
                            .ConfigureAwait(false);

                        await _scopeTransitions.PublishAdditiveModuleScopeAsync(
                                "LoadAdditiveModule",
                                moduleScopeKey,
                                moduleContext,
                                operation.Generation,
                                operation.CancellationToken)
                            .ConfigureAwait(false);
                    })
                .ConfigureAwait(false);
        }

        public async Task UnloadAdditiveModuleAsync(
            Type moduleScopeKey,
            CancellationToken cancellationToken = default)
        {
            await ExecuteGenerationBoundSideScopeOperationAsync(
                    NullInitializationProgressNotifier.Instance,
                    cancellationToken,
                    async operation =>
                    {
                        if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
                        if (!_additiveModuleContexts.TryGetValue(moduleScopeKey, out var context))
                            throw new InvalidOperationException($"Additive module scope '{moduleScopeKey.Name}' is not loaded.");

                        await _scopeTransitions.ExitActivatedScopeAsync(
                                GameContextType.Module,
                                context,
                                moduleScopeKey,
                                ScopeLifecycleState.Deactivating,
                                operation.ProgressNotifier,
                                operation.CancellationToken,
                                () => _additiveModuleContexts.Remove(moduleScopeKey))
                            .ConfigureAwait(false);
                    })
                .ConfigureAwait(false);
        }
    }
}
