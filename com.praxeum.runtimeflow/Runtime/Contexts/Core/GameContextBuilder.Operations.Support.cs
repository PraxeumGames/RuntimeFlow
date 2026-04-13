using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VContainer;

namespace RuntimeFlow.Contexts
{
    public partial class GameContextBuilder
    {
        internal bool TryResolveFromSession<TService>(out TService? service)
            where TService : class
        {
            if (_sessionContext == null)
            {
                service = null;
                return false;
            }

            if (!_sessionContext.IsRegistered(typeof(TService)))
            {
                service = null;
                return false;
            }

            try
            {
                service = _sessionContext.Resolve<TService>();
                return true;
            }
            catch (VContainerException)
            {
                service = null;
                return false;
            }
        }

        internal IGameContext GetSessionContext()
        {
            return _sessionContext ?? throw new InvalidOperationException("Session context is not initialized. Call BuildAsync first.");
        }

        internal async Task EnsureLazyServiceInitializedAsync(Type serviceType, CancellationToken cancellationToken = default)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));

            if (_lazyInitialization.IsInitialized(serviceType))
                return;

            if (!_lazyInitialization.TryGetBinding(serviceType, out var entry))
                return;

            await _lazyInitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_lazyInitialization.IsInitialized(serviceType))
                    return;

                var resolved = entry.Context.Resolve(serviceType);
                if (resolved is IAsyncInitializableService asyncService)
                    await asyncService.InitializeAsync(cancellationToken).ConfigureAwait(false);

                _lazyInitialization.MarkInitialized(serviceType);
                RegisterInitializedServiceForScopeDisposal(entry.Scope, entry.ScopeKey, serviceType);
            }
            finally
            {
                _lazyInitLock.Release();
            }
        }

        internal bool CanRestartSession()
        {
            return _globalContext != null;
        }

        internal void SetScopeState(Type scopeType, ScopeLifecycleState state)
        {
            _scopeRegistry.SetScopeState(scopeType, state);
        }

        internal ScopeLifecycleState GetScopeState(Type scopeType)
        {
            return _scopeRegistry.GetScopeState(scopeType);
        }

        private void SetScopeStateIfTracked(GameContextType scope, ScopeLifecycleState state, Type? explicitScopeKey = null)
        {
            _scopeRegistry.SetScopeStateIfTracked(scope, state, explicitScopeKey);
        }

        private Type? FindDeclaredScopeKey(GameContextType scope)
        {
            return _scopeRegistry.FindDeclaredScopeKey(scope);
        }

        private IEnumerable<Type> FindDeclaredScopeKeys(GameContextType scope)
        {
            return _scopeRegistry.FindDeclaredScopeKeys(scope);
        }

        private void ValidateSceneScopeOperationPreconditions(Type sceneScopeKey)
        {
            if (sceneScopeKey == null) throw new ArgumentNullException(nameof(sceneScopeKey));
            if (_sessionContext == null)
                throw new InvalidOperationException("Session context is not initialized. Call BuildAsync first.");
            if (!_scopeProfiles.HasSceneProfile(sceneScopeKey))
                throw new InvalidOperationException($"Scene scope '{sceneScopeKey.Name}' is not configured.");
        }

        private void ValidateModuleScopeOperationPreconditions(Type moduleScopeKey)
        {
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
            if (_sceneContext == null)
                throw new InvalidOperationException("Scene context is not initialized. Call LoadSceneAsync first.");
            if (!_scopeProfiles.HasModuleProfile(moduleScopeKey))
                throw new InvalidOperationException($"Module scope '{moduleScopeKey.Name}' is not configured.");
        }

        private async Task ExecuteScopedOperationAsync(
            IInitializationProgressNotifier? progressNotifier,
            CancellationToken cancellationToken,
            Func<long, IInitializationProgressNotifier, CancellationToken, Task> operation)
        {
            await CancelActiveLoadAsync().ConfigureAwait(false);
            var generation = Interlocked.Increment(ref _runGeneration);
            _activeLoadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var notifier = progressNotifier ?? NullInitializationProgressNotifier.Instance;
            _activeLoadTask = operation(generation, notifier, _activeLoadCts.Token);
            await _activeLoadTask.ConfigureAwait(false);
        }
    }
}
