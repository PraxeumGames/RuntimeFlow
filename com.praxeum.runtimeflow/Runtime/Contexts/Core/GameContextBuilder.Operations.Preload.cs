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
            FlushDeferredScopedRegistrations();
            ValidateSceneScopeOperationPreconditions(sceneScopeKey);

            var notifier = progressNotifier ?? NullInitializationProgressNotifier.Instance;
            var sceneProfile = _scopeProfiles.GetSceneProfile(sceneScopeKey);

            var (initializedServices, availableServices) = CreateSeededInitializationState(_globalContext, _sessionContext);

            GameContext? preloadedContext = null;
            try
            {
                preloadedContext = await CreateAndInitializeScopeContextAsync(
                        GameContextType.Scene,
                        _sessionContext!,
                        sceneProfile.Registrations,
                        sceneProfile.Services,
                        _onSceneInitialized,
                        initializedServices,
                        availableServices,
                        notifier,
                        Volatile.Read(ref _runGeneration),
                        cancellationToken,
                        sceneScopeKey,
                        skipActivation: true)
                    .ConfigureAwait(false);

                if (_preloadedContexts.TryGetValue(sceneScopeKey, out var existingPreloadedSceneContext))
                {
                    await DisposeScopeContextAsync(
                            GameContextType.Scene,
                            existingPreloadedSceneContext,
                            cancellationToken,
                            sceneScopeKey)
                        .ConfigureAwait(false);
                }

                _preloadedContexts[sceneScopeKey] = preloadedContext;
            }
            catch (Exception ex)
            {
                var cleanupFailures = await CaptureCleanupFailuresAsync(
                        cancellationToken,
                        async () =>
                        {
                            await DisposeScopeContextAsync(
                                    GameContextType.Scene,
                                    preloadedContext,
                                    cancellationToken,
                                    sceneScopeKey)
                                .ConfigureAwait(false);
                            preloadedContext = null;
                        })
                    .ConfigureAwait(false);

                if (cleanupFailures.Count > 0)
                    throw CreateCleanupAggregateException("PreloadScene", ex, cleanupFailures);

                throw;
            }
        }

        public async Task PreloadModuleAsync(
            Type moduleScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            FlushDeferredScopedRegistrations();
            ValidateModuleScopeOperationPreconditions(moduleScopeKey);

            var notifier = progressNotifier ?? NullInitializationProgressNotifier.Instance;
            var moduleProfile = _scopeProfiles.GetModuleProfile(moduleScopeKey);

            var (initializedServices, availableServices) = CreateSeededInitializationState(
                _globalContext,
                _sessionContext,
                _sceneContext);

            GameContext? preloadedContext = null;
            try
            {
                preloadedContext = await CreateAndInitializeScopeContextAsync(
                        GameContextType.Module,
                        _sceneContext!,
                        moduleProfile.Registrations,
                        moduleProfile.Services,
                        _onModuleInitialized,
                        initializedServices,
                        availableServices,
                        notifier,
                        Volatile.Read(ref _runGeneration),
                        cancellationToken,
                        moduleScopeKey,
                        skipActivation: true)
                    .ConfigureAwait(false);

                if (_preloadedContexts.TryGetValue(moduleScopeKey, out var existingPreloadedModuleContext))
                {
                    await DisposeScopeContextAsync(
                            GameContextType.Module,
                            existingPreloadedModuleContext,
                            cancellationToken,
                            moduleScopeKey)
                        .ConfigureAwait(false);
                }

                _preloadedContexts[moduleScopeKey] = preloadedContext;
            }
            catch (Exception ex)
            {
                var cleanupFailures = await CaptureCleanupFailuresAsync(
                        cancellationToken,
                        async () =>
                        {
                            await DisposeScopeContextAsync(
                                    GameContextType.Module,
                                    preloadedContext,
                                    cancellationToken,
                                    moduleScopeKey)
                                .ConfigureAwait(false);
                            preloadedContext = null;
                        })
                    .ConfigureAwait(false);

                if (cleanupFailures.Count > 0)
                    throw CreateCleanupAggregateException("PreloadModule", ex, cleanupFailures);

                throw;
            }
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
            FlushDeferredScopedRegistrations();
            ValidateModuleScopeOperationPreconditions(moduleScopeKey);

            if (_additiveModuleContexts.ContainsKey(moduleScopeKey))
                throw new InvalidOperationException($"Additive module scope '{moduleScopeKey.Name}' is already loaded.");

            var notifier = progressNotifier ?? NullInitializationProgressNotifier.Instance;
            var moduleProfile = _scopeProfiles.GetModuleProfile(moduleScopeKey);

            var (initializedServices, availableServices) = CreateSeededInitializationState(
                _globalContext,
                _sessionContext,
                _sceneContext);

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
                        notifier,
                        Volatile.Read(ref _runGeneration),
                        cancellationToken,
                        moduleScopeKey)
                    .ConfigureAwait(false);

                _additiveModuleContexts[moduleScopeKey] = moduleContext;
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
                    throw CreateCleanupAggregateException("LoadAdditiveModule", ex, cleanupFailures);

                throw;
            }
        }

        public async Task UnloadAdditiveModuleAsync(
            Type moduleScopeKey,
            CancellationToken cancellationToken = default)
        {
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
            if (!_additiveModuleContexts.TryGetValue(moduleScopeKey, out var context))
                throw new InvalidOperationException($"Additive module scope '{moduleScopeKey.Name}' is not loaded.");

            SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Deactivating, moduleScopeKey);
            await ExecuteScopeActivationExitAsync(GameContextType.Module, context, NullInitializationProgressNotifier.Instance, cancellationToken).ConfigureAwait(false);
            await DisposeScopeContextAsync(
                    GameContextType.Module,
                    context,
                    cancellationToken,
                    moduleScopeKey,
                    () => SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Disposed, moduleScopeKey))
                .ConfigureAwait(false);
            _additiveModuleContexts.Remove(moduleScopeKey);
        }
    }
}
