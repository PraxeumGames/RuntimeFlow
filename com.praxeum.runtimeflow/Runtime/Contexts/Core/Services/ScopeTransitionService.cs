using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Events;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Executes scope enter/exit transitions: creating and activating scope contexts,
    /// publishing preloaded and additive module contexts, and tearing down activated
    /// scopes with guaranteed disposal even when deactivation hooks fail.
    /// </summary>
    internal sealed class ScopeTransitionService
    {
        private readonly ActiveScopeState _activeState;
        private readonly GameContextScopeRegistry _scopeRegistry;
        private readonly ScopeOperationCoordinator _coordinator;
        private readonly ScopeActivationService _activationService;
        private readonly ScopeInitializationService _initService;
        private readonly ScopeDisposalService _disposalService;
        private readonly ScopeLifecycleDependencies _deps;

        public ScopeTransitionService(
            ActiveScopeState activeState,
            GameContextScopeRegistry scopeRegistry,
            ScopeOperationCoordinator coordinator,
            ScopeActivationService activationService,
            ScopeInitializationService initService,
            ScopeDisposalService disposalService,
            ScopeLifecycleDependencies deps)
        {
            _activeState = activeState ?? throw new ArgumentNullException(nameof(activeState));
            _scopeRegistry = scopeRegistry ?? throw new ArgumentNullException(nameof(scopeRegistry));
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
            _initService = initService ?? throw new ArgumentNullException(nameof(initService));
            _disposalService = disposalService ?? throw new ArgumentNullException(nameof(disposalService));
            _deps = deps ?? throw new ArgumentNullException(nameof(deps));
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
                context = await _initService.CreateAndInitializeScopeContextAsync(
                        scope, parentContext, profile.Registrations, profile.Services, initializedCallback,
                        initializedServices, availableServices, progressNotifier, generation, cancellationToken,
                        scopeKey, skipActivation, eventBus, _deps)
                    .ConfigureAwait(false);

                if (verifyGenerationAfterCreate)
                    _coordinator.ThrowIfStaleGeneration(generation, cancellationToken);

                return context;
            }
            catch (Exception ex)
            {
                var cleanupFailures = await CaptureEnteredScopeCleanupFailuresAsync(scope, scopeKey, context).ConfigureAwait(false);
                context = null;

                if (cleanupFailures.Count > 0)
                    throw ScopeCleanupFailures.CreateCleanupAggregate(operationName, ex, cleanupFailures);

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
                _coordinator.ThrowIfStaleGeneration(generation, cancellationToken);

                if (_activeState.PreloadedContexts.TryGetValue(scopeKey, out var existingPreloadedContext))
                {
                    await _disposalService.DisposeScopeContextAsync(scope, existingPreloadedContext, cancellationToken, scopeKey).ConfigureAwait(false);
                }

                _coordinator.PublishInCurrentGeneration(
                    generation,
                    cancellationToken,
                    () => _activeState.PreloadedContexts[scopeKey] = preloadedContext);
            }
            catch (Exception ex)
            {
                var cleanupFailures = await CaptureEnteredScopeCleanupFailuresAsync(scope, scopeKey, preloadedContext).ConfigureAwait(false);

                if (cleanupFailures.Count > 0)
                    throw ScopeCleanupFailures.CreateCleanupAggregate(operationName, ex, cleanupFailures);

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
                _coordinator.PublishInCurrentGeneration(
                    generation,
                    cancellationToken,
                    () => _activeState.AdditiveModuleContexts[moduleScopeKey] = moduleContext);
            }
            catch (Exception ex)
            {
                var cleanupFailures = await CaptureEnteredScopeCleanupFailuresAsync(GameContextType.Module, moduleScopeKey, moduleContext).ConfigureAwait(false);

                if (cleanupFailures.Count > 0)
                    throw ScopeCleanupFailures.CreateCleanupAggregate(operationName, ex, cleanupFailures);

                throw;
            }
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

            if (!_activeState.PreloadedContexts.TryGetValue(scopeKey, out var preloadedContext))
                return false;

            await _activationService.ExecuteEnterAsync(scope, preloadedContext, progressNotifier, totalServices: 0, cancellationToken).ConfigureAwait(false);
            _coordinator.PublishInCurrentGeneration(
                generation,
                cancellationToken,
                () =>
                {
                    _activeState.PreloadedContexts.Remove(scopeKey);
                    adoptContext(preloadedContext);
                    _scopeRegistry.SetScopeStateIfTracked(scope, ScopeLifecycleState.Active, scopeKey);
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

            // Teardown must always complete: a deactivation failure must not leave the
            // scope undisposed or the active-scope reference stale.
            List<Exception>? failures = null;

            if (transitionState.HasValue)
                _scopeRegistry.SetScopeStateIfTracked(scope, transitionState.Value, scopeKey);

            try
            {
                await _activationService.ExecuteExitAsync(scope, context, progressNotifier, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures ??= new List<Exception>();
                failures.Add(ex);
            }

            // Once the deactivation hook has failed, the teardown must still run to
            // completion: a cancelled/superseded transition must not leak the scope.
            // Disposal is immune to the original failure, so it uses a non-cancelled
            // cleanup token.
            var teardownCancellationToken = failures != null
                ? CancellationToken.None
                : cancellationToken;

            try
            {
                await _disposalService.DisposeScopeContextAsync(
                        scope,
                        context,
                        teardownCancellationToken,
                        scopeKey,
                        () => _scopeRegistry.SetScopeStateIfTracked(scope, ScopeLifecycleState.Disposed, scopeKey))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures ??= new List<Exception>();
                failures.Add(ex);
            }

            clearContext();

            if (failures != null)
            {
                // Cancellation-driven teardown failures must surface as
                // OperationCanceledException so superseded transitions keep their
                // cancellation semantics instead of an AggregateException.
                var remainingFailures = ScopeCleanupFailures.FilterCancellationFailures(
                    new AggregateException(failures),
                    cancellationToken.IsCancellationRequested);
                if (remainingFailures != null)
                    throw remainingFailures;

                var cancellation = failures.FirstOrDefault(ex => ex is OperationCanceledException)
                    as OperationCanceledException;
                throw cancellation ?? new OperationCanceledException(cancellationToken);
            }
        }

        private Task<List<Exception>> CaptureEnteredScopeCleanupFailuresAsync(
            GameContextType scope,
            Type? scopeKey,
            GameContext? context)
        {
            return ScopeCleanupFailures.CaptureCleanupFailuresAsync(
                CancellationToken.None,
                async () =>
                {
                    await _disposalService.DisposeScopeContextAsync(
                            scope,
                            context,
                            CancellationToken.None,
                            scopeKey,
                            () => _scopeRegistry.SetScopeStateIfTracked(scope, ScopeLifecycleState.Disposed, scopeKey))
                        .ConfigureAwait(false);
                });
        }
    }
}
