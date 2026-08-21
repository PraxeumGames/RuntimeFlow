using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RuntimeFlow.Contexts
{
    internal sealed class ScopeDisposalService
    {
        private readonly ActiveScopeState _activeState;
        private readonly GameContextScopeRegistry _scopeRegistry;
        private readonly IInitializationExecutionScheduler _executionScheduler;
        private readonly ILogger _logger;
        private readonly ScopeOperationCoordinator _coordinator;
        private readonly ScopeActivationService _activationService;

        public ScopeDisposalService(
            ActiveScopeState activeState,
            GameContextScopeRegistry scopeRegistry,
            IInitializationExecutionScheduler executionScheduler,
            ILogger logger,
            ScopeOperationCoordinator coordinator,
            ScopeActivationService activationService)
        {
            _activeState = activeState ?? throw new ArgumentNullException(nameof(activeState));
            _scopeRegistry = scopeRegistry ?? throw new ArgumentNullException(nameof(scopeRegistry));
            _executionScheduler = executionScheduler ?? throw new ArgumentNullException(nameof(executionScheduler));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
        }

        public async Task DisposeAllScopesAsync(CancellationToken cancellationToken = default)
        {
            await _coordinator.CancelActiveLoadAsync(CancellationToken.None).ConfigureAwait(false);
            await _coordinator.SideLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await DisposeAllScopesCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _coordinator.SideLock.Release();
            }
        }

        private async Task DisposeAllScopesCoreAsync(CancellationToken cancellationToken)
        {
            var failures = new List<Exception>();

            await CaptureDisposeAllFailureAsync(failures, () => DisposePreloadedContextsAsync(cancellationToken)).ConfigureAwait(false);
            await CaptureDisposeAllFailureAsync(failures, () => DisposeAdditiveModulesAsync(cancellationToken)).ConfigureAwait(false);

            if (_activeState.ModuleContext != null)
            {
                var moduleContext = _activeState.ModuleContext;
                var moduleScopeKey = _activeState.ActiveModuleScopeKey;
                try
                {
                    await CaptureDisposeAllFailureAsync(failures, () => DisposeActivatedScopeDirectAsync(GameContextType.Module, moduleContext, NullInitializationProgressNotifier.Instance, cancellationToken, moduleScopeKey, ScopeLifecycleState.Deactivating)).ConfigureAwait(false);
                    _logger.LogDebug("Scope {Scope} disposed", GameContextType.Module);
                }
                finally { _activeState.ModuleContext = null; }
            }

            if (_activeState.SceneContext != null)
            {
                var sceneContext = _activeState.SceneContext;
                var sceneScopeKey = _activeState.ActiveSceneScopeKey;
                try
                {
                    await CaptureDisposeAllFailureAsync(failures, () => DisposeActivatedScopeDirectAsync(GameContextType.Scene, sceneContext, NullInitializationProgressNotifier.Instance, cancellationToken, sceneScopeKey, ScopeLifecycleState.Deactivating)).ConfigureAwait(false);
                    _logger.LogDebug("Scope {Scope} disposed", GameContextType.Scene);
                }
                finally { _activeState.SceneContext = null; }
            }

            if (_activeState.SessionContext != null)
            {
                var sessionContext = _activeState.SessionContext;
                try
                {
                    await CaptureDisposeAllFailureAsync(failures, () => DisposeActivatedScopeDirectAsync(GameContextType.Session, sessionContext, NullInitializationProgressNotifier.Instance, cancellationToken, transitionState: ScopeLifecycleState.Deactivating)).ConfigureAwait(false);
                    _logger.LogDebug("Scope {Scope} disposed", GameContextType.Session);
                }
                finally { _activeState.SessionContext = null; }
            }

            if (_activeState.OwnsGlobalContext)
            {
                var globalContext = _activeState.GlobalContext;
                try
                {
                    await CaptureDisposeAllFailureAsync(failures, () => DisposeOwnedGlobalContextAsync(globalContext, cancellationToken)).ConfigureAwait(false);
                    _logger.LogDebug("Scope {Scope} disposed", GameContextType.Global);
                }
                finally { _activeState.GlobalContext = null; }
            }

            DisposeAndClearEventBuses(_activeState.OwnsGlobalContext);
            _activeState.ActiveSceneScopeKey = null;
            _activeState.ActiveModuleScopeKey = null;

            if (failures.Count > 0)
                throw new AggregateException("DisposeAllScopes completed with one or more teardown failures.", failures);
        }

        public async Task DisposePreloadedContextsAsync(CancellationToken cancellationToken)
        {
            var failures = new List<Exception>();
            foreach (var kvp in _activeState.PreloadedContexts.ToArray())
            {
                var scopeType = _scopeRegistry.GetDeclaredScopeOrDefault(kvp.Key, GameContextType.Scene);
                try
                {
                    if (scopeType is GameContextType.Scene or GameContextType.Module)
                    {
                        _scopeRegistry.SetScopeStateIfTracked(scopeType, ScopeLifecycleState.Deactivating, kvp.Key);
                        await DisposeScopeContextAsync(scopeType, kvp.Value, cancellationToken, kvp.Key, () => _scopeRegistry.SetScopeStateIfTracked(scopeType, ScopeLifecycleState.Disposed, kvp.Key)).ConfigureAwait(false);
                    }
                    else
                    {
                        await DisposeContextAsync(kvp.Value, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) { AddDisposeAllFailure(failures, ex); }
                finally { _activeState.PreloadedContexts.Remove(kvp.Key); }
            }

            if (failures.Count > 0)
                throw new AggregateException(failures);
        }

        public async Task DisposeAdditiveModulesAsync(CancellationToken cancellationToken)
        {
            var failures = new List<Exception>();
            foreach (var kvp in _activeState.AdditiveModuleContexts.ToArray())
            {
                try
                {
                    await ExitActivatedScopeAsync(GameContextType.Module, kvp.Value, kvp.Key, ScopeLifecycleState.Deactivating, NullInitializationProgressNotifier.Instance, cancellationToken, () => { }).ConfigureAwait(false);
                }
                catch (Exception ex) { AddDisposeAllFailure(failures, ex); }
                finally { _activeState.AdditiveModuleContexts.Remove(kvp.Key); }
            }

            if (failures.Count > 0)
                throw new AggregateException(failures);
        }

        public Task DisposeActivatedScopeAsync(GameContextType scope, GameContext? context, Type? scopeKey, ScopeLifecycleState? transitionState, CancellationToken cancellationToken, Action? clearContext = null)
            => ExitActivatedScopeAsync(scope, context, scopeKey, transitionState, NullInitializationProgressNotifier.Instance, cancellationToken, clearContext ?? (() => { }));

        public async Task DisposeScopeContextAsync(GameContextType scope, GameContext? context, CancellationToken cancellationToken, Type? scopeKey = null, Action? onDisposed = null)
        {
            if (context == null) return;
            List<Exception>? exceptions = null;
            try { await context.DisposeAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex)
            {
                if (IsObjectDisposedFailure(ex))
                    _logger.LogWarning(ex, "Ignoring disposed object failure while disposing {Scope} context.", scope);
                else { exceptions ??= new List<Exception>(); exceptions.Add(ex); }
            }
            onDisposed?.Invoke();
            if (exceptions != null) throw new AggregateException(exceptions);
        }

        private async Task DisposeContextAsync(GameContext? context, CancellationToken cancellationToken)
        {
            if (context == null) return;
            await context.DisposeAsync(cancellationToken).ConfigureAwait(false);
        }

        private Task DisposeContextAsync(IGameContext? context, CancellationToken cancellationToken)
        {
            if (context == null) return Task.CompletedTask;
            return _executionScheduler.ExecuteAsync(InitializationThreadAffinity.MainThread, _ => { context.Dispose(); return Task.CompletedTask; }, cancellationToken);
        }

        private async Task DisposeOwnedGlobalContextAsync(IGameContext? context, CancellationToken cancellationToken)
        {
            if (context == null) return;
            if (context is GameContext gameContext)
            {
                await DisposeScopeContextAsync(GameContextType.Global, gameContext, cancellationToken, onDisposed: () => _scopeRegistry.SetScopeStateIfTracked(GameContextType.Global, ScopeLifecycleState.Disposed)).ConfigureAwait(false);
                return;
            }
            await DisposeContextAsync(context, cancellationToken).ConfigureAwait(false);
            _scopeRegistry.SetScopeStateIfTracked(GameContextType.Global, ScopeLifecycleState.Disposed);
        }

        private void DisposeAndClearEventBuses(bool includeGlobal)
            => ScopeCleanupFailures.DisposeAndClearEventBuses(_activeState, includeGlobal);

        private async Task ExitActivatedScopeAsync(GameContextType scope, GameContext? context, Type? scopeKey, ScopeLifecycleState? transitionState, IInitializationProgressNotifier progressNotifier, CancellationToken cancellationToken, Action clearContext)
        {
            if (context == null) return;
            List<Exception>? failures = null;
            if (transitionState.HasValue) _scopeRegistry.SetScopeStateIfTracked(scope, transitionState.Value, scopeKey);
            try { await _activationService.ExecuteExitAsync(scope, context, progressNotifier, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { failures ??= new List<Exception>(); failures.Add(ex); }
            var teardownToken = failures != null ? CancellationToken.None : cancellationToken;
            try { await DisposeScopeContextAsync(scope, context, teardownToken, scopeKey, () => _scopeRegistry.SetScopeStateIfTracked(scope, ScopeLifecycleState.Disposed, scopeKey)).ConfigureAwait(false); }
            catch (Exception ex) { failures ??= new List<Exception>(); failures.Add(ex); }
            clearContext();
            if (failures != null)
            {
                var remaining = FilterCancellationFailures(new AggregateException(failures), cancellationToken.IsCancellationRequested);
                if (remaining != null) throw remaining;
                var cancellation = failures.FirstOrDefault(ex => ex is OperationCanceledException) as OperationCanceledException;
                throw cancellation ?? new OperationCanceledException(cancellationToken);
            }
        }

        private async Task DisposeActivatedScopeDirectAsync(GameContextType scope, GameContext? context, IInitializationProgressNotifier progressNotifier, CancellationToken cancellationToken, Type? scopeKey = null, ScopeLifecycleState? transitionState = null)
        {
            await ExitActivatedScopeAsync(scope, context, scopeKey, transitionState, progressNotifier, cancellationToken, () => { }).ConfigureAwait(false);
        }

        private static async Task CaptureDisposeAllFailureAsync(List<Exception> failures, Func<Task> disposeOperation)
        {
            try { await disposeOperation().ConfigureAwait(false); }
            catch (Exception ex) { AddDisposeAllFailure(failures, ex); }
        }

        private static void AddDisposeAllFailure(List<Exception> failures, Exception exception)
        {
            if (exception is AggregateException agg) { failures.AddRange(agg.Flatten().InnerExceptions); return; }
            failures.Add(exception);
        }

        private static Exception? FilterCancellationFailures(Exception exception, bool cancellationRequested)
            => ScopeCleanupFailures.FilterCancellationFailures(exception, cancellationRequested);

        private static bool IsObjectDisposedFailure(Exception exception)
        {
            if (exception is ObjectDisposedException) return true;
            if (exception is AggregateException agg) { var f = agg.Flatten().InnerExceptions; return f.Count > 0 && f.All(IsObjectDisposedFailure); }
            if (exception.InnerException != null && IsObjectDisposedFailure(exception.InnerException)) return true;
            return exception.Message?.IndexOf("Cannot access a disposed object.", StringComparison.Ordinal) >= 0;
        }
    }
}
