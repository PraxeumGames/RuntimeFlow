using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RuntimeFlow.Contexts
{
    public partial class GameContextBuilder
    {
        private async Task CancelActiveLoadAsync(CancellationToken cancellationToken = default)
        {
            CancellationTokenSource? activeLoadCts;
            Task activeLoadTask;
            lock (_activeLoadSync)
            {
                activeLoadCts = _activeLoadCts;
                if (activeLoadCts == null)
                    return;

                activeLoadTask = _activeLoadTask;
            }

            activeLoadCts.Cancel();
            try
            {
                await AwaitWithCancellation(activeLoadTask, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception) when (activeLoadCts.IsCancellationRequested)
            {
            }
            finally
            {
                if (ClearActiveLoadIfOwner(activeLoadCts))
                    activeLoadCts.Dispose();
            }
        }

        internal async Task DisposeAllScopesAsync(CancellationToken cancellationToken = default)
        {
            await CancelActiveLoadAsync(CancellationToken.None).ConfigureAwait(false);
            await _sideScopeOperationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await DisposeAllScopesCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sideScopeOperationLock.Release();
            }
        }

        private async Task DisposeAllScopesCoreAsync(CancellationToken cancellationToken)
        {
            var failures = new List<Exception>();

            await CaptureDisposeAllFailureAsync(
                    failures,
                    () => DisposePreloadedContextsAsync(cancellationToken))
                .ConfigureAwait(false);
            await CaptureDisposeAllFailureAsync(
                    failures,
                    () => DisposeAdditiveModulesAsync(cancellationToken))
                .ConfigureAwait(false);

            if (_moduleContext != null)
            {
                var moduleContext = _moduleContext;
                var moduleScopeKey = _activeModuleScopeKey;
                try
                {
                    await CaptureDisposeAllFailureAsync(
                            failures,
                            () => DisposeActivatedScopeAsync(
                                GameContextType.Module,
                                moduleContext,
                                NullInitializationProgressNotifier.Instance,
                                cancellationToken,
                                moduleScopeKey,
                                ScopeLifecycleState.Deactivating))
                        .ConfigureAwait(false);
                    _logger.LogDebug("Scope {Scope} disposed", GameContextType.Module);
                }
                finally
                {
                    _moduleContext = null;
                }
            }

            if (_sceneContext != null)
            {
                var sceneContext = _sceneContext;
                var sceneScopeKey = _activeSceneScopeKey;
                try
                {
                    await CaptureDisposeAllFailureAsync(
                            failures,
                            () => DisposeActivatedScopeAsync(
                                GameContextType.Scene,
                                sceneContext,
                                NullInitializationProgressNotifier.Instance,
                                cancellationToken,
                                sceneScopeKey,
                                ScopeLifecycleState.Deactivating))
                        .ConfigureAwait(false);
                    _logger.LogDebug("Scope {Scope} disposed", GameContextType.Scene);
                }
                finally
                {
                    _sceneContext = null;
                }
            }

            if (_sessionContext != null)
            {
                var sessionContext = _sessionContext;
                try
                {
                    await CaptureDisposeAllFailureAsync(
                            failures,
                            () => DisposeActivatedScopeAsync(
                                GameContextType.Session,
                                sessionContext,
                                NullInitializationProgressNotifier.Instance,
                                cancellationToken,
                                transitionState: ScopeLifecycleState.Deactivating))
                        .ConfigureAwait(false);
                    _logger.LogDebug("Scope {Scope} disposed", GameContextType.Session);
                }
                finally
                {
                    _sessionContext = null;
                }
            }

            if (_ownsGlobalContext)
            {
                var globalContext = _globalContext;
                try
                {
                    await CaptureDisposeAllFailureAsync(
                            failures,
                            () => DisposeOwnedGlobalContextAsync(globalContext, cancellationToken))
                        .ConfigureAwait(false);
                    _logger.LogDebug("Scope {Scope} disposed", GameContextType.Global);
                }
                finally
                {
                    _globalContext = null;
                }
            }

            DisposeAndClearEventBuses(includeGlobal: _ownsGlobalContext);
            _activeSceneScopeKey = null;
            _activeModuleScopeKey = null;

            if (failures.Count > 0)
                throw new AggregateException("DisposeAllScopes completed with one or more teardown failures.", failures);
        }

        private async Task DisposePreloadedContextsAsync(CancellationToken cancellationToken)
        {
            var failures = new List<Exception>();
            foreach (var kvp in _preloadedContexts.ToArray())
            {
                var scopeType = _scopeRegistry.GetDeclaredScopeOrDefault(kvp.Key, GameContextType.Scene);
                try
                {
                    if (scopeType is GameContextType.Scene or GameContextType.Module)
                    {
                        SetScopeStateIfTracked(scopeType, ScopeLifecycleState.Deactivating, kvp.Key);
                        await DisposeScopeContextAsync(
                                scopeType,
                                kvp.Value,
                                cancellationToken,
                                kvp.Key,
                                () => SetScopeStateIfTracked(scopeType, ScopeLifecycleState.Disposed, kvp.Key))
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await DisposeContextAsync(kvp.Value, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    AddDisposeAllFailure(failures, ex);
                }
                finally
                {
                    _preloadedContexts.Remove(kvp.Key);
                }
            }

            if (failures.Count > 0)
                throw new AggregateException(failures);
        }

        private async Task DisposeAdditiveModulesAsync(CancellationToken cancellationToken)
        {
            var failures = new List<Exception>();
            foreach (var kvp in _additiveModuleContexts.ToArray())
            {
                try
                {
                    await _scopeTransitions.ExitActivatedScopeAsync(
                            GameContextType.Module,
                            kvp.Value,
                            kvp.Key,
                            ScopeLifecycleState.Deactivating,
                            NullInitializationProgressNotifier.Instance,
                            cancellationToken,
                            () => { })
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AddDisposeAllFailure(failures, ex);
                }
                finally
                {
                    _additiveModuleContexts.Remove(kvp.Key);
                }
            }

            if (failures.Count > 0)
                throw new AggregateException(failures);
        }

        private static async Task CaptureDisposeAllFailureAsync(
            List<Exception> failures,
            Func<Task> disposeOperation)
        {
            try
            {
                await disposeOperation().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AddDisposeAllFailure(failures, ex);
            }
        }

        private static void AddDisposeAllFailure(List<Exception> failures, Exception exception)
        {
            if (failures == null) throw new ArgumentNullException(nameof(failures));
            if (exception == null) throw new ArgumentNullException(nameof(exception));

            if (exception is AggregateException aggregateException)
            {
                failures.AddRange(aggregateException.Flatten().InnerExceptions);
                return;
            }

            failures.Add(exception);
        }

        private static async Task AwaitWithCancellation(Task task, CancellationToken cancellationToken)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));

            if (task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return;
            }

            var cancellationTask = Task.Delay(Timeout.Infinite, cancellationToken);
            var completed = await Task.WhenAny(task, cancellationTask).ConfigureAwait(false);
            if (completed != task)
                cancellationToken.ThrowIfCancellationRequested();

            await task.ConfigureAwait(false);
        }

        private void ThrowIfStaleGeneration(long generation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_scopeGenerationSync)
            {
                if (generation != _runGeneration)
                    throw new OperationCanceledException(cancellationToken);
            }
        }
    }
}
