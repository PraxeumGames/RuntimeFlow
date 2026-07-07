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
            await ExecuteParentInvalidatingExclusiveScopeOperationAsync(
                    progressNotifier,
                    cancellationToken,
                    operation => BuildAsyncCore(operation.Generation, operation.ProgressNotifier, operation.CancellationToken))
                .ConfigureAwait(false);
            return _globalContext ?? throw new InvalidOperationException("Global context was not created.");
        }

        public async Task RestartSessionAsync(IInitializationProgressNotifier? progressNotifier = null, CancellationToken cancellationToken = default)
        {
            FlushDeferredScopedRegistrations();
            if (_globalContext == null)
                throw new InvalidOperationException("Global context is not created. Call BuildAsync first.");

            await ExecuteParentInvalidatingExclusiveScopeOperationAsync(
                    progressNotifier,
                    cancellationToken,
                    operation => RestartSessionAsyncCore(operation.Generation, operation.ProgressNotifier, operation.CancellationToken))
                .ConfigureAwait(false);
        }

        internal async Task LoadSceneAsync(
            Type sceneScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            FlushDeferredScopedRegistrations();
            ValidateSceneScopeOperationPreconditions(sceneScopeKey);

            await ExecuteParentInvalidatingExclusiveScopeOperationAsync(
                    progressNotifier,
                    cancellationToken,
                    operation => LoadSceneAsyncCore(sceneScopeKey, operation.Generation, operation.ProgressNotifier, operation.CancellationToken))
                .ConfigureAwait(false);
        }

        internal async Task LoadModuleAsync(
            Type moduleScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            FlushDeferredScopedRegistrations();
            ValidateModuleScopeOperationPreconditions(moduleScopeKey);

            await ExecuteExclusiveScopeOperationAsync(
                    progressNotifier,
                    cancellationToken,
                    operation => LoadModuleAsyncCore(moduleScopeKey, operation.Generation, operation.ProgressNotifier, operation.CancellationToken))
                .ConfigureAwait(false);
        }

        internal async Task ReloadModuleAsync(
            Type moduleScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            FlushDeferredScopedRegistrations();
            ValidateModuleScopeOperationPreconditions(moduleScopeKey);

            await ExecuteExclusiveScopeOperationAsync(
                    progressNotifier,
                    cancellationToken,
                    operation => ReloadModuleAsyncCore(moduleScopeKey, operation.Generation, operation.ProgressNotifier, operation.CancellationToken))
                .ConfigureAwait(false);
        }
    }
}
