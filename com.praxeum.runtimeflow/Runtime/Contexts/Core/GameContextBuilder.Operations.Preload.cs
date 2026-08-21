using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    public partial class GameContextBuilder
    {
        public Task PreloadSceneAsync(
            Type sceneScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            if (sceneScopeKey == null) throw new ArgumentNullException(nameof(sceneScopeKey));
            return ExecuteGenerationBoundSideScopeOperationAsync(
                progressNotifier,
                cancellationToken,
                operation => _preloadService.PreloadSceneCoreAsync(sceneScopeKey, operation));
        }

        public Task PreloadModuleAsync(
            Type moduleScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
            return ExecuteGenerationBoundSideScopeOperationAsync(
                progressNotifier,
                cancellationToken,
                operation => _preloadService.PreloadModuleCoreAsync(moduleScopeKey, operation));
        }

        public bool HasPreloadedScope(Type scopeKey)
        {
            if (scopeKey == null) throw new ArgumentNullException(nameof(scopeKey));
            return _preloadedContexts.ContainsKey(scopeKey);
        }

        public Task LoadAdditiveModuleAsync(
            Type moduleScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
            return ExecuteGenerationBoundSideScopeOperationAsync(
                progressNotifier,
                cancellationToken,
                operation => _preloadService.LoadAdditiveModuleCoreAsync(moduleScopeKey, operation));
        }

        public Task UnloadAdditiveModuleAsync(
            Type moduleScopeKey,
            CancellationToken cancellationToken = default)
        {
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
            return ExecuteGenerationBoundSideScopeOperationAsync(
                NullInitializationProgressNotifier.Instance,
                cancellationToken,
                operation => _preloadService.UnloadAdditiveModuleCoreAsync(moduleScopeKey, operation));
        }
    }
}
