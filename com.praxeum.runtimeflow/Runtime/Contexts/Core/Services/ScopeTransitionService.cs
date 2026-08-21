using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    internal sealed class ScopeTransitionService
    {
        private readonly Func<GameContextType, IGameContext, IReadOnlyCollection<Action<IGameContext>>, IReadOnlyCollection<ServiceDescriptor>, Action<IGameContext>?, ISet<Type>, IDictionary<Type, object>, IInitializationProgressNotifier, long, CancellationToken, Type?, bool, RuntimeFlow.Events.ScopeEventBus?, Task<GameContext>> _createAndInitializeAsync;
        private readonly Func<GameContextType, GameContext?, CancellationToken, Type?, Action?, Task> _disposeScopeAsync;
        private readonly Func<CancellationToken, Func<Task>[], Task<List<Exception>>> _captureCleanupAsync;
        private readonly Func<string, Exception, IReadOnlyCollection<Exception>, AggregateException> _createCleanupAggregate;
        private readonly Func<CancellationToken> _createFailureCleanupToken;
        private readonly Func<Exception, bool, Exception?> _filterCancellationFailures;
        private readonly Action<GameContextType, ScopeLifecycleState, Type?> _setScopeState;
        private readonly Action<long, CancellationToken, Action> _publishInGeneration;
        private readonly Action<long, CancellationToken> _throwIfStaleGeneration;
        private readonly ScopeActivationService _activationService;
        private readonly ActiveScopeState _activeState;

        public ScopeTransitionService(
            ActiveScopeState activeState,
            ScopeActivationService activationService,
            Func<GameContextType, IGameContext, IReadOnlyCollection<Action<IGameContext>>, IReadOnlyCollection<ServiceDescriptor>, Action<IGameContext>?, ISet<Type>, IDictionary<Type, object>, IInitializationProgressNotifier, long, CancellationToken, Type?, bool, RuntimeFlow.Events.ScopeEventBus?, Task<GameContext>> createAndInitializeAsync,
            Func<GameContextType, GameContext?, CancellationToken, Type?, Action?, Task> disposeScopeAsync,
            Func<CancellationToken, Func<Task>[], Task<List<Exception>>> captureCleanupAsync,
            Func<string, Exception, IReadOnlyCollection<Exception>, AggregateException> createCleanupAggregate,
            Func<CancellationToken> createFailureCleanupToken,
            Func<Exception, bool, Exception?> filterCancellationFailures,
            Action<GameContextType, ScopeLifecycleState, Type?> setScopeState,
            Action<long, CancellationToken, Action> publishInGeneration,
            Action<long, CancellationToken> throwIfStaleGeneration)
        {
            _activeState = activeState ?? throw new ArgumentNullException(nameof(activeState));
            _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
            _createAndInitializeAsync = createAndInitializeAsync ?? throw new ArgumentNullException(nameof(createAndInitializeAsync));
            _disposeScopeAsync = disposeScopeAsync ?? throw new ArgumentNullException(nameof(disposeScopeAsync));
            _captureCleanupAsync = captureCleanupAsync ?? throw new ArgumentNullException(nameof(captureCleanupAsync));
            _createCleanupAggregate = createCleanupAggregate ?? throw new ArgumentNullException(nameof(createCleanupAggregate));
            _createFailureCleanupToken = createFailureCleanupToken ?? throw new ArgumentNullException(nameof(createFailureCleanupToken));
            _filterCancellationFailures = filterCancellationFailures ?? throw new ArgumentNullException(nameof(filterCancellationFailures));
            _setScopeState = setScopeState ?? throw new ArgumentNullException(nameof(setScopeState));
            _publishInGeneration = publishInGeneration ?? throw new ArgumentNullException(nameof(publishInGeneration));
            _throwIfStaleGeneration = throwIfStaleGeneration ?? throw new ArgumentNullException(nameof(throwIfStaleGeneration));
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
            RuntimeFlow.Events.ScopeEventBus? eventBus = null)
        {
            if (string.IsNullOrWhiteSpace(operationName)) throw new ArgumentException("Operation name is required.", nameof(operationName));

            GameContext? context = null;
            try
            {
                context = await _createAndInitializeAsync(scope, parentContext, profile.Registrations, profile.Services, initializedCallback, initializedServices, availableServices, progressNotifier, generation, cancellationToken, scopeKey, skipActivation, eventBus).ConfigureAwait(false);

                if (verifyGenerationAfterCreate)
                    _throwIfStaleGeneration(generation, cancellationToken);

                return context;
            }
            catch (Exception ex)
            {
                var cleanupFailures = await CaptureEnteredScopeCleanupFailuresAsync(scope, scopeKey, context).ConfigureAwait(false);
                if (cleanupFailures.Count > 0)
                    throw _createCleanupAggregate(operationName, ex, cleanupFailures);
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
            try
            {
                _throwIfStaleGeneration(generation, cancellationToken);

                if (_activeState.PreloadedContexts.TryGetValue(scopeKey, out var existingPreloadedContext))
                {
                    await _disposeScopeAsync(scope, existingPreloadedContext, cancellationToken, scopeKey, null).ConfigureAwait(false);
                }

                _publishInGeneration(generation, cancellationToken, () => _activeState.PreloadedContexts[scopeKey] = preloadedContext);
            }
            catch (Exception ex)
            {
                var cleanupFailures = await CaptureEnteredScopeCleanupFailuresAsync(scope, scopeKey, preloadedContext).ConfigureAwait(false);
                if (cleanupFailures.Count > 0)
                    throw _createCleanupAggregate(operationName, ex, cleanupFailures);
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
            try
            {
                _publishInGeneration(generation, cancellationToken, () => _activeState.AdditiveModuleContexts[moduleScopeKey] = moduleContext);
            }
            catch (Exception ex)
            {
                var cleanupFailures = await CaptureEnteredScopeCleanupFailuresAsync(GameContextType.Module, moduleScopeKey, moduleContext).ConfigureAwait(false);
                if (cleanupFailures.Count > 0)
                    throw _createCleanupAggregate(operationName, ex, cleanupFailures);
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
            if (!_activeState.PreloadedContexts.TryGetValue(scopeKey, out var preloadedContext))
                return false;

            await _activationService.ExecuteEnterAsync(scope, preloadedContext, progressNotifier, totalServices: 0, cancellationToken).ConfigureAwait(false);
            _publishInGeneration(generation, cancellationToken, () =>
            {
                _activeState.PreloadedContexts.Remove(scopeKey);
                adoptContext(preloadedContext);
                _setScopeState(scope, ScopeLifecycleState.Active, scopeKey);
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
            if (context == null)
                return;

            List<Exception>? failures = null;

            if (transitionState.HasValue)
                _setScopeState(scope, transitionState.Value, scopeKey);

            try
            {
                await _activationService.ExecuteExitAsync(scope, context, progressNotifier, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures ??= new List<Exception>();
                failures.Add(ex);
            }

            var teardownCancellationToken = failures != null
                ? _createFailureCleanupToken()
                : cancellationToken;

            try
            {
                await _disposeScopeAsync(scope, context, teardownCancellationToken, scopeKey, () => _setScopeState(scope, ScopeLifecycleState.Disposed, scopeKey)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures ??= new List<Exception>();
                failures.Add(ex);
            }

            clearContext();

            if (failures != null)
            {
                var remainingFailures = _filterCancellationFailures(new AggregateException(failures), cancellationToken.IsCancellationRequested);
                if (remainingFailures != null)
                    throw remainingFailures;

                var cancellation = failures.FirstOrDefault(ex => ex is OperationCanceledException) as OperationCanceledException;
                throw cancellation ?? new OperationCanceledException(cancellationToken);
            }
        }

        private Task<List<Exception>> CaptureEnteredScopeCleanupFailuresAsync(GameContextType scope, Type? scopeKey, GameContext? context)
        {
            var cleanupCancellationToken = _createFailureCleanupToken();
            return _captureCleanupAsync(cleanupCancellationToken, new Func<Task>[]
            {
                async () => await _disposeScopeAsync(scope, context, cleanupCancellationToken, scopeKey, () => _setScopeState(scope, ScopeLifecycleState.Disposed, scopeKey)).ConfigureAwait(false)
            });
        }
    }
}
