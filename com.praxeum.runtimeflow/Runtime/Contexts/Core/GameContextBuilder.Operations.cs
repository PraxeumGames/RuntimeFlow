using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    public partial class GameContextBuilder
    {
        public async Task<IGameContext> BuildAsync(IInitializationProgressNotifier? progressNotifier = null, CancellationToken cancellationToken = default)
        {
            FlushDeferredScopedRegistrations();
            await CancelActiveLoadAsync().ConfigureAwait(false);
            var generation = Interlocked.Increment(ref _runGeneration);
            _activeLoadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var notifier = progressNotifier ?? NullInitializationProgressNotifier.Instance;
            _activeLoadTask = BuildAsyncCore(generation, notifier, _activeLoadCts.Token);
            await _activeLoadTask.ConfigureAwait(false);
            return _globalContext ?? throw new InvalidOperationException("Global context was not created.");
        }

        public async Task RestartSessionAsync(IInitializationProgressNotifier? progressNotifier = null, CancellationToken cancellationToken = default)
        {
            FlushDeferredScopedRegistrations();
            if (_globalContext == null)
                throw new InvalidOperationException("Global context is not created. Call BuildAsync first.");

            await ExecuteScopedOperationAsync(progressNotifier, cancellationToken, RestartSessionAsyncCore).ConfigureAwait(false);
        }

        internal async Task LoadSceneAsync(
            Type sceneScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            FlushDeferredScopedRegistrations();
            ValidateSceneScopeOperationPreconditions(sceneScopeKey);

            await ExecuteScopedOperationAsync(
                    progressNotifier,
                    cancellationToken,
                    (generation, notifier, token) => LoadSceneAsyncCore(sceneScopeKey, generation, notifier, token))
                .ConfigureAwait(false);
        }

        internal async Task LoadModuleAsync(
            Type moduleScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            FlushDeferredScopedRegistrations();
            ValidateModuleScopeOperationPreconditions(moduleScopeKey);

            await ExecuteScopedOperationAsync(
                    progressNotifier,
                    cancellationToken,
                    (generation, notifier, token) => LoadModuleAsyncCore(moduleScopeKey, generation, notifier, token))
                .ConfigureAwait(false);
        }

        internal async Task ReloadModuleAsync(
            Type moduleScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            FlushDeferredScopedRegistrations();
            ValidateModuleScopeOperationPreconditions(moduleScopeKey);

            await ExecuteScopedOperationAsync(
                    progressNotifier,
                    cancellationToken,
                    (generation, notifier, token) => ReloadModuleAsyncCore(moduleScopeKey, generation, notifier, token))
                .ConfigureAwait(false);
        }
    }
}
