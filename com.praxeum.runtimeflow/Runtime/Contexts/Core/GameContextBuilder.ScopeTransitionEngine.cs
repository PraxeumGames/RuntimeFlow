using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Events;

namespace RuntimeFlow.Contexts
{
    public partial class GameContextBuilder
    {
        private sealed class ScopeTransitionEngine
        {
            private readonly GameContextBuilder _owner;

            public ScopeTransitionEngine(GameContextBuilder owner)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            }

            public async Task<GameContext> EnterScopeAsync(
                string operationName,
                GameContextType scope,
                Type? scopeKey,
                IGameContext parentContext,
                ScopeProfile profile,
                Action<IGameContext>? initializedCallback,
                ISet<Type> initializedServices,
                IDictionary<Type, object> availableServices,
                IInitializationProgressNotifier progressNotifier,
                long generation,
                CancellationToken cancellationToken,
                bool skipActivation = false,
                bool verifyGenerationAfterCreate = true,
                ScopeEventBus? eventBus = null)
            {
                if (string.IsNullOrWhiteSpace(operationName)) throw new ArgumentException("Operation name is required.", nameof(operationName));
                if (parentContext == null) throw new ArgumentNullException(nameof(parentContext));
                if (profile == null) throw new ArgumentNullException(nameof(profile));
                if (initializedServices == null) throw new ArgumentNullException(nameof(initializedServices));
                if (availableServices == null) throw new ArgumentNullException(nameof(availableServices));
                if (progressNotifier == null) throw new ArgumentNullException(nameof(progressNotifier));

                GameContext? context = null;
                try
                {
                    context = await _owner.CreateAndInitializeScopeContextAsync(
                            scope,
                            parentContext,
                            profile.Registrations,
                            profile.Services,
                            initializedCallback,
                            initializedServices,
                            availableServices,
                            progressNotifier,
                            generation,
                            cancellationToken,
                            scopeKey,
                            skipActivation,
                            eventBus)
                        .ConfigureAwait(false);

                    if (verifyGenerationAfterCreate)
                        _owner.ThrowIfStaleGeneration(generation, cancellationToken);

                    return context;
                }
                catch (Exception ex)
                {
                    var cleanupFailures = await CaptureEnteredScopeCleanupFailuresAsync(scope, scopeKey, context)
                        .ConfigureAwait(false);
                    context = null;

                    if (cleanupFailures.Count > 0)
                        throw GameContextBuilder.CreateCleanupAggregateException(operationName, ex, cleanupFailures);

                    throw;
                }
            }

            public async Task ReplacePreloadedScopeAsync(
                string operationName,
                GameContextType scope,
                Type scopeKey,
                GameContext preloadedContext,
                long generation,
                CancellationToken cancellationToken)
            {
                if (string.IsNullOrWhiteSpace(operationName)) throw new ArgumentException("Operation name is required.", nameof(operationName));
                if (scopeKey == null) throw new ArgumentNullException(nameof(scopeKey));
                if (preloadedContext == null) throw new ArgumentNullException(nameof(preloadedContext));

                try
                {
                    _owner.ThrowIfStaleGeneration(generation, cancellationToken);

                    if (_owner._preloadedContexts.TryGetValue(scopeKey, out var existingPreloadedContext))
                    {
                        await _owner.DisposeScopeContextAsync(scope, existingPreloadedContext, cancellationToken, scopeKey)
                            .ConfigureAwait(false);
                    }

                    _owner.PublishInCurrentGeneration(
                        generation,
                        cancellationToken,
                        () => _owner._preloadedContexts[scopeKey] = preloadedContext);
                }
                catch (Exception ex)
                {
                    var cleanupFailures = await CaptureEnteredScopeCleanupFailuresAsync(scope, scopeKey, preloadedContext)
                        .ConfigureAwait(false);

                    if (cleanupFailures.Count > 0)
                        throw GameContextBuilder.CreateCleanupAggregateException(operationName, ex, cleanupFailures);

                    throw;
                }
            }

            public async Task PublishAdditiveModuleScopeAsync(
                string operationName,
                Type moduleScopeKey,
                GameContext moduleContext,
                long generation,
                CancellationToken cancellationToken)
            {
                if (string.IsNullOrWhiteSpace(operationName)) throw new ArgumentException("Operation name is required.", nameof(operationName));
                if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
                if (moduleContext == null) throw new ArgumentNullException(nameof(moduleContext));

                try
                {
                    _owner.PublishInCurrentGeneration(
                        generation,
                        cancellationToken,
                        () => _owner._additiveModuleContexts[moduleScopeKey] = moduleContext);
                }
                catch (Exception ex)
                {
                    var cleanupFailures = await CaptureEnteredScopeCleanupFailuresAsync(
                            GameContextType.Module,
                            moduleScopeKey,
                            moduleContext)
                        .ConfigureAwait(false);

                    if (cleanupFailures.Count > 0)
                        throw GameContextBuilder.CreateCleanupAggregateException(operationName, ex, cleanupFailures);

                    throw;
                }
            }

            private Task<List<Exception>> CaptureEnteredScopeCleanupFailuresAsync(
                GameContextType scope,
                Type? scopeKey,
                GameContext? context)
            {
                var cleanupCancellationToken = CreateFailureCleanupCancellationToken();
                return _owner.CaptureCleanupFailuresAsync(
                    cleanupCancellationToken,
                    async () =>
                    {
                        await _owner.DisposeScopeContextAsync(
                                scope,
                                context,
                                cleanupCancellationToken,
                                scopeKey,
                                () => _owner.SetScopeStateIfTracked(scope, ScopeLifecycleState.Disposed, scopeKey))
                            .ConfigureAwait(false);
                    });
            }

            public async Task<bool> TryActivatePreloadedScopeAsync(
                GameContextType scope,
                Type scopeKey,
                IInitializationProgressNotifier progressNotifier,
                long generation,
                CancellationToken cancellationToken,
                Action<GameContext> adoptContext)
            {
                if (scopeKey == null) throw new ArgumentNullException(nameof(scopeKey));
                if (progressNotifier == null) throw new ArgumentNullException(nameof(progressNotifier));
                if (adoptContext == null) throw new ArgumentNullException(nameof(adoptContext));

                if (!_owner._preloadedContexts.TryGetValue(scopeKey, out var preloadedContext))
                    return false;

                await _owner.ExecuteScopeActivationEnterAsync(
                        scope,
                        preloadedContext,
                        progressNotifier,
                        totalServices: 0,
                        cancellationToken)
                    .ConfigureAwait(false);
                _owner.PublishInCurrentGeneration(
                    generation,
                    cancellationToken,
                    () =>
                    {
                        _owner._preloadedContexts.Remove(scopeKey);
                        adoptContext(preloadedContext);
                        _owner.SetScopeStateIfTracked(scope, ScopeLifecycleState.Active, scopeKey);
                    });
                return true;
            }

            public async Task ExitActivatedScopeAsync(
                GameContextType scope,
                GameContext? context,
                Type? scopeKey,
                ScopeLifecycleState? transitionState,
                IInitializationProgressNotifier progressNotifier,
                CancellationToken cancellationToken,
                Action clearContext)
            {
                if (progressNotifier == null) throw new ArgumentNullException(nameof(progressNotifier));
                if (clearContext == null) throw new ArgumentNullException(nameof(clearContext));
                if (context == null)
                    return;

                if (transitionState.HasValue)
                    _owner.SetScopeStateIfTracked(scope, transitionState.Value, scopeKey);
                await _owner.ExecuteScopeActivationExitAsync(scope, context, progressNotifier, cancellationToken)
                    .ConfigureAwait(false);
                await _owner.DisposeScopeContextAsync(
                        scope,
                        context,
                        cancellationToken,
                        scopeKey,
                        () => _owner.SetScopeStateIfTracked(scope, ScopeLifecycleState.Disposed, scopeKey))
                    .ConfigureAwait(false);
                clearContext();
            }
        }
    }
}
