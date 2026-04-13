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
            if (_activeLoadCts == null)
                return;

            _activeLoadCts.Cancel();
            try
            {
                await AwaitWithCancellation(_activeLoadTask, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception) when (_activeLoadCts.IsCancellationRequested)
            {
            }
            finally
            {
                _activeLoadCts.Dispose();
                _activeLoadCts = null;
                _activeLoadTask = Task.CompletedTask;
            }
        }

        internal async Task DisposeAllScopesAsync(CancellationToken cancellationToken = default)
        {
            await CancelActiveLoadAsync(cancellationToken).ConfigureAwait(false);

            await DisposePreloadedContextsAsync(cancellationToken).ConfigureAwait(false);
            await DisposeAdditiveModulesAsync(cancellationToken).ConfigureAwait(false);

            if (_moduleContext != null)
            {
                await DisposeActivatedScopeAsync(
                        GameContextType.Module,
                        _moduleContext,
                        NullInitializationProgressNotifier.Instance,
                        cancellationToken,
                        _activeModuleScopeKey,
                        ScopeLifecycleState.Deactivating)
                    .ConfigureAwait(false);
                _moduleContext = null;
                _logger.LogDebug("Scope {Scope} disposed", GameContextType.Module);
            }

            if (_sceneContext != null)
            {
                await DisposeActivatedScopeAsync(
                        GameContextType.Scene,
                        _sceneContext,
                        NullInitializationProgressNotifier.Instance,
                        cancellationToken,
                        _activeSceneScopeKey,
                        ScopeLifecycleState.Deactivating)
                    .ConfigureAwait(false);
                _sceneContext = null;
                _logger.LogDebug("Scope {Scope} disposed", GameContextType.Scene);
            }

            if (_sessionContext != null)
            {
                await DisposeActivatedScopeAsync(
                        GameContextType.Session,
                        _sessionContext,
                        NullInitializationProgressNotifier.Instance,
                        cancellationToken,
                        transitionState: ScopeLifecycleState.Deactivating)
                    .ConfigureAwait(false);
                _sessionContext = null;
                _logger.LogDebug("Scope {Scope} disposed", GameContextType.Session);
            }

            if (_ownsGlobalContext)
            {
                await DisposeOwnedGlobalContextAsync(_globalContext, cancellationToken).ConfigureAwait(false);
                _globalContext = null;
                _logger.LogDebug("Scope {Scope} disposed", GameContextType.Global);
            }

            DisposeAndClearEventBuses(includeGlobal: _ownsGlobalContext);
            _activeSceneScopeKey = null;
            _activeModuleScopeKey = null;
        }

        private async Task DisposePreloadedContextsAsync(CancellationToken cancellationToken)
        {
            foreach (var kvp in _preloadedContexts.ToArray())
            {
                var scopeType = _scopeRegistry.GetDeclaredScopeOrDefault(kvp.Key, GameContextType.Scene);

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

            _preloadedContexts.Clear();
        }

        private async Task DisposeAdditiveModulesAsync(CancellationToken cancellationToken)
        {
            foreach (var kvp in _additiveModuleContexts)
            {
                SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Deactivating, kvp.Key);
                await ExecuteScopeActivationExitAsync(GameContextType.Module, kvp.Value, NullInitializationProgressNotifier.Instance, cancellationToken).ConfigureAwait(false);
                await DisposeScopeContextAsync(
                        GameContextType.Module,
                        kvp.Value,
                        cancellationToken,
                        kvp.Key,
                        () => SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Disposed, kvp.Key))
                    .ConfigureAwait(false);
            }

            _additiveModuleContexts.Clear();
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
            if (generation != Volatile.Read(ref _runGeneration))
                throw new OperationCanceledException(cancellationToken);
        }
    }
}
