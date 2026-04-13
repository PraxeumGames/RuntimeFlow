using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RuntimeFlow.Events;
using VContainer;

namespace RuntimeFlow.Contexts
{
    public partial class GameContextBuilder
    {
        private async Task BuildAsyncCore(long generation, IInitializationProgressNotifier progressNotifier, CancellationToken cancellationToken)
        {
            ValidateExternalGlobalConfiguration();
            ResetRuntimeLifecycleBookkeeping();

            await DisposeAdditiveModulesAsync(cancellationToken).ConfigureAwait(false);
            await DisposePreloadedContextsAsync(cancellationToken).ConfigureAwait(false);

            if (_moduleContext != null)
            {
                await DisposeActivatedScopeAsync(
                        GameContextType.Module,
                        _moduleContext,
                        progressNotifier,
                        cancellationToken,
                        _activeModuleScopeKey,
                        ScopeLifecycleState.Deactivating)
                    .ConfigureAwait(false);
                _moduleContext = null;
            }

            if (_sceneContext != null)
            {
                await DisposeActivatedScopeAsync(
                        GameContextType.Scene,
                        _sceneContext,
                        progressNotifier,
                        cancellationToken,
                        _activeSceneScopeKey,
                        ScopeLifecycleState.Deactivating)
                    .ConfigureAwait(false);
                _sceneContext = null;
            }

            if (_sessionContext != null)
            {
                await DisposeActivatedScopeAsync(
                        GameContextType.Session,
                        _sessionContext,
                        progressNotifier,
                        cancellationToken,
                        transitionState: ScopeLifecycleState.Deactivating)
                    .ConfigureAwait(false);
                _sessionContext = null;
            }

            if (_ownsGlobalContext)
            {
                await DisposeOwnedGlobalContextAsync(_globalContext, cancellationToken).ConfigureAwait(false);
                _globalContext = null;
            }

            DisposeAndClearEventBuses(includeGlobal: _ownsGlobalContext);
            _activeSceneScopeKey = null;
            _activeModuleScopeKey = null;

            var initializedServices = new HashSet<Type>();
            var availableServices = new Dictionary<Type, object>();
            _logger.LogInformation("BuildAsync started — initializing scopes");

            IGameContext? globalContext = null;
            GameContext? sessionContext = null;
            GameContext? sceneContext = null;
            GameContext? moduleContext = null;
            Type? activeSceneScopeKey = null;
            Type? activeModuleScopeKey = null;

            try
            {
                ThrowIfStaleGeneration(generation, cancellationToken);
                if (_ownsGlobalContext)
                {
                    _logger.LogDebug("Building scope {Scope}", GameContextType.Global);
                    SetScopeStateIfTracked(GameContextType.Global, ScopeLifecycleState.Loading);
                    _globalEventBus = new ScopeEventBus();
                    globalContext = CreateContext(
                        null,
                        _scopeProfiles.GlobalRegistrations,
                        Array.Empty<ServiceDescriptor>(),
                        _onGlobalInitialized,
                        initialize: true,
                        availableServices,
                        _globalEventBus);
                    var globalTotalServices = await ExecuteInitializersAsync(
                            GameContextType.Global,
                            (GameContext)globalContext,
                            initializedServices,
                            progressNotifier,
                            generation,
                            cancellationToken)
                        .ConfigureAwait(false);
                    progressNotifier.OnScopeCompleted(GameContextType.Global, globalTotalServices);
                    SetScopeStateIfTracked(GameContextType.Global, ScopeLifecycleState.Active);
                    _globalContext = globalContext;
                    globalContext = null;
                }
                else
                {
                    globalContext = _globalContext
                        ?? throw new InvalidOperationException("External global context is not configured.");
                    SetScopeStateIfTracked(GameContextType.Global, ScopeLifecycleState.Active);
                }

                ThrowIfStaleGeneration(generation, cancellationToken);
                await ExecuteStageCallbackOnMainThreadAsync(
                        progressNotifier.OnGlobalContextReadyForSessionInitializationAsync,
                        cancellationToken)
                    .ConfigureAwait(false);
                ThrowIfStaleGeneration(generation, cancellationToken);
                _sessionEventBus = new ScopeEventBus(_globalEventBus);
                sessionContext = await CreateAndInitializeScopeContextAsync(
                        GameContextType.Session,
                        (globalContext ?? _globalContext)!,
                        _scopeProfiles.SessionRegistrations,
                        Array.Empty<ServiceDescriptor>(),
                        _onSessionInitialized,
                        initializedServices,
                        availableServices,
                        progressNotifier,
                        generation,
                        cancellationToken,
                        eventBus: _sessionEventBus)
                    .ConfigureAwait(false);

                ThrowIfStaleGeneration(generation, cancellationToken);
                _sessionContext = sessionContext;
                _sceneContext = sceneContext;
                _moduleContext = moduleContext;
                _activeSceneScopeKey = activeSceneScopeKey;
                _activeModuleScopeKey = activeModuleScopeKey;
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
                                    activeModuleScopeKey)
                                .ConfigureAwait(false);
                            moduleContext = null;
                        },
                        async () =>
                        {
                            await DisposeScopeContextAsync(
                                    GameContextType.Scene,
                                    sceneContext,
                                    cancellationToken,
                                    activeSceneScopeKey)
                                .ConfigureAwait(false);
                            sceneContext = null;
                        },
                        async () =>
                        {
                            await DisposeScopeContextAsync(
                                    GameContextType.Session,
                                    sessionContext,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            sessionContext = null;
                        },
                        async () =>
                        {
                            if (!_ownsGlobalContext)
                                return;

                            SetScopeStateIfTracked(GameContextType.Global, ScopeLifecycleState.Failed);
                            if (globalContext is GameContext ownedGlobalContext)
                            {
                                await DisposeScopeContextAsync(
                                        GameContextType.Global,
                                        ownedGlobalContext,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            else
                            {
                                await DisposeContextAsync(globalContext, cancellationToken).ConfigureAwait(false);
                            }

                            globalContext = null;
                        })
                    .ConfigureAwait(false);

                if (cleanupFailures.Count > 0)
                    throw CreateCleanupAggregateException("BuildAsync", ex, cleanupFailures);

                throw;
            }
        }

        private void ResetRuntimeLifecycleBookkeeping()
        {
            _lazyInitialization.Clear();
            _scopeRegistry.ResetScopeStates();
        }

        private void ValidateExternalGlobalConfiguration()
        {
            if (_ownsGlobalContext)
                return;

            if (_scopeProfiles.HasGlobalRegistrations)
            {
                throw new InvalidOperationException(
                    "GBBR1001: Global registrations are not allowed when using an external global context bridge.");
            }
        }

        private async Task RestartSessionAsyncCore(long generation, IInitializationProgressNotifier progressNotifier, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Session restarting");
            ResetRuntimeLifecycleBookkeeping();

            await DisposeAdditiveModulesAsync(cancellationToken).ConfigureAwait(false);
            await DisposePreloadedContextsAsync(cancellationToken).ConfigureAwait(false);

            if (_moduleContext != null)
            {
                await DisposeActivatedScopeAsync(
                        GameContextType.Module,
                        _moduleContext,
                        progressNotifier,
                        cancellationToken,
                        _activeModuleScopeKey,
                        ScopeLifecycleState.Reloading)
                    .ConfigureAwait(false);
                _moduleContext = null;
            }

            if (_sceneContext != null)
            {
                await DisposeActivatedScopeAsync(
                        GameContextType.Scene,
                        _sceneContext,
                        progressNotifier,
                        cancellationToken,
                        _activeSceneScopeKey,
                        ScopeLifecycleState.Reloading)
                    .ConfigureAwait(false);
                _sceneContext = null;
            }

            if (_sessionContext != null)
            {
                await DisposeActivatedScopeAsync(
                        GameContextType.Session,
                        _sessionContext,
                        progressNotifier,
                        cancellationToken,
                        transitionState: ScopeLifecycleState.Reloading)
                    .ConfigureAwait(false);
                _sessionContext = null;
            }

            var (initializedServices, availableServices) = CreateSeededInitializationState(_globalContext);

            GameContext? sessionContext = null;
            GameContext? sceneContext = null;
            GameContext? moduleContext = null;

            try
            {
                DisposeAndClearEventBuses(includeGlobal: false);

                await DrainMainThreadFrameAsync(cancellationToken).ConfigureAwait(false);
                ThrowIfStaleGeneration(generation, cancellationToken);
                await ExecuteStageCallbackOnMainThreadAsync(
                        progressNotifier.OnSessionRestartTeardownCompletedAsync,
                        cancellationToken)
                    .ConfigureAwait(false);
                ThrowIfStaleGeneration(generation, cancellationToken);
                _sessionEventBus = new ScopeEventBus(_globalEventBus);
                sessionContext = await CreateAndInitializeScopeContextAsync(
                        GameContextType.Session,
                        _globalContext!,
                        _scopeProfiles.SessionRegistrations,
                        Array.Empty<ServiceDescriptor>(),
                        _onSessionInitialized,
                        initializedServices,
                        availableServices,
                        progressNotifier,
                        generation,
                        cancellationToken,
                        eventBus: _sessionEventBus)
                    .ConfigureAwait(false);

                if (_activeSceneScopeKey != null && _scopeProfiles.TryGetSceneProfile(_activeSceneScopeKey, out var sceneProfile))
                {
                    _sceneEventBus = new ScopeEventBus(_sessionEventBus);
                    sceneContext = await CreateAndInitializeScopeContextAsync(
                            GameContextType.Scene,
                            sessionContext,
                            sceneProfile.Registrations,
                            sceneProfile.Services,
                            _onSceneInitialized,
                            initializedServices,
                            availableServices,
                            progressNotifier,
                            generation,
                            cancellationToken,
                            _activeSceneScopeKey,
                            eventBus: _sceneEventBus)
                        .ConfigureAwait(false);
                }

                if (_activeModuleScopeKey != null && sceneContext != null && _scopeProfiles.TryGetModuleProfile(_activeModuleScopeKey, out var moduleProfile))
                {
                    _moduleEventBus = new ScopeEventBus(_sceneEventBus);
                    moduleContext = await CreateAndInitializeScopeContextAsync(
                            GameContextType.Module,
                            sceneContext,
                            moduleProfile.Registrations,
                            moduleProfile.Services,
                            _onModuleInitialized,
                            initializedServices,
                            availableServices,
                            progressNotifier,
                            generation,
                            cancellationToken,
                            _activeModuleScopeKey,
                            eventBus: _moduleEventBus)
                        .ConfigureAwait(false);
                }

                ThrowIfStaleGeneration(generation, cancellationToken);
                _sessionContext = sessionContext;
                _sceneContext = sceneContext;
                _moduleContext = moduleContext;
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
                                    _activeModuleScopeKey)
                                .ConfigureAwait(false);
                            moduleContext = null;
                        },
                        async () =>
                        {
                            await DisposeScopeContextAsync(
                                    GameContextType.Scene,
                                    sceneContext,
                                    cancellationToken,
                                    _activeSceneScopeKey)
                                .ConfigureAwait(false);
                            sceneContext = null;
                        },
                        async () =>
                        {
                            await DisposeScopeContextAsync(
                                    GameContextType.Session,
                                    sessionContext,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            sessionContext = null;
                        })
                    .ConfigureAwait(false);

                if (cleanupFailures.Count > 0)
                    throw CreateCleanupAggregateException("RestartSession", ex, cleanupFailures);

                throw;
            }
        }
    }
}
