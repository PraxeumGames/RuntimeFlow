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

                var resolved = entry.Context.Resolve(entry.Initializer);
                if (resolved is IAsyncInitializableService asyncService)
                    await asyncService.InitializeAsync(cancellationToken).ConfigureAwait(false);

                _lazyInitialization.MarkInitialized(serviceType);
                RegisterInitializedServiceForScopeDisposal(entry.Scope, entry.ScopeKey, entry.Initializer);
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

        private async Task ExecuteExclusiveScopeOperationAsync(
            IInitializationProgressNotifier? progressNotifier,
            CancellationToken cancellationToken,
            Func<ScopeOperationContext, Task> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            Task operationTask;
            await _exclusiveScopeOperationStartLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await CancelActiveLoadAsync(CancellationToken.None).ConfigureAwait(false);
                operationTask = BeginExclusiveScopeOperation(progressNotifier, cancellationToken, operation);
            }
            finally
            {
                _exclusiveScopeOperationStartLock.Release();
            }

            await operationTask.ConfigureAwait(false);
        }

        private async Task ExecuteParentInvalidatingExclusiveScopeOperationAsync(
            IInitializationProgressNotifier? progressNotifier,
            CancellationToken cancellationToken,
            Func<ScopeOperationContext, Task> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            Task operationTask;
            await _exclusiveScopeOperationStartLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await CancelActiveLoadAsync(CancellationToken.None).ConfigureAwait(false);
                await _sideScopeOperationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    operationTask = BeginExclusiveScopeOperation(progressNotifier, cancellationToken, operation);
                }
                catch
                {
                    _sideScopeOperationLock.Release();
                    throw;
                }
            }
            finally
            {
                _exclusiveScopeOperationStartLock.Release();
            }

            try
            {
                await operationTask.ConfigureAwait(false);
            }
            finally
            {
                _sideScopeOperationLock.Release();
            }
        }

        private Task BeginExclusiveScopeOperation(
            IInitializationProgressNotifier? progressNotifier,
            CancellationToken cancellationToken,
            Func<ScopeOperationContext, Task> operation)
        {
            var generation = BeginNewScopeGeneration();
            var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var operationContext = CreateScopeOperationContext(generation, progressNotifier, operationCts.Token);
            var completionSource = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_activeLoadSync)
            {
                _activeLoadCts = operationCts;
                _activeLoadTask = completionSource.Task;
            }

            _ = RunExclusiveScopeOperationAsync(operation, operationContext, operationCts, completionSource);
            return completionSource.Task;
        }

        private async Task RunExclusiveScopeOperationAsync(
            Func<ScopeOperationContext, Task> operation,
            ScopeOperationContext operationContext,
            CancellationTokenSource operationCts,
            TaskCompletionSource<object?> completionSource)
        {
            try
            {
                await operation(operationContext).ConfigureAwait(false);
                completionSource.TrySetResult(null);
            }
            catch (OperationCanceledException)
            {
                completionSource.TrySetCanceled();
            }
            catch (Exception ex)
            {
                completionSource.TrySetException(ex);
            }
            finally
            {
                if (ClearActiveLoadIfOwner(operationCts))
                    operationCts.Dispose();
            }
        }

        private long BeginNewScopeGeneration()
        {
            lock (_scopeGenerationSync)
            {
                return ++_runGeneration;
            }
        }

        private long ReadScopeGeneration()
        {
            lock (_scopeGenerationSync)
            {
                return _runGeneration;
            }
        }

        private void PublishInCurrentGeneration(
            long generation,
            CancellationToken cancellationToken,
            Action publish)
        {
            if (publish == null) throw new ArgumentNullException(nameof(publish));

            cancellationToken.ThrowIfCancellationRequested();
            lock (_scopeGenerationSync)
            {
                if (generation != _runGeneration)
                    throw new OperationCanceledException(cancellationToken);

                publish();
            }
        }

        private bool ClearActiveLoadIfOwner(CancellationTokenSource operationCts)
        {
            lock (_activeLoadSync)
            {
                if (!ReferenceEquals(_activeLoadCts, operationCts))
                    return false;

                _activeLoadCts = null;
                _activeLoadTask = Task.CompletedTask;
                return true;
            }
        }

        private async Task ExecuteGenerationBoundSideScopeOperationAsync(
            IInitializationProgressNotifier? progressNotifier,
            CancellationToken cancellationToken,
            Func<ScopeOperationContext, Task> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            await _sideScopeOperationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var generation = ReadScopeGeneration();
                var operationContext = CreateScopeOperationContext(generation, progressNotifier, cancellationToken);
                await operation(operationContext).ConfigureAwait(false);
            }
            finally
            {
                _sideScopeOperationLock.Release();
            }
        }

        private static ScopeOperationContext CreateScopeOperationContext(
            long generation,
            IInitializationProgressNotifier? progressNotifier,
            CancellationToken cancellationToken)
        {
            return new ScopeOperationContext(
                generation,
                progressNotifier ?? NullInitializationProgressNotifier.Instance,
                cancellationToken);
        }

        private readonly struct ScopeOperationContext
        {
            public ScopeOperationContext(
                long generation,
                IInitializationProgressNotifier progressNotifier,
                CancellationToken cancellationToken)
            {
                Generation = generation;
                ProgressNotifier = progressNotifier ?? throw new ArgumentNullException(nameof(progressNotifier));
                CancellationToken = cancellationToken;
            }

            public long Generation { get; }
            public IInitializationProgressNotifier ProgressNotifier { get; }
            public CancellationToken CancellationToken { get; }
        }
    }
}
