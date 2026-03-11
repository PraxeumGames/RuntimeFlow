using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    public static class SceneLoaderProgressBridge
    {
        public static IGameSceneLoaderWithProgress Wrap(IGameSceneLoader sceneLoader)
        {
            if (sceneLoader == null) throw new ArgumentNullException(nameof(sceneLoader));
            return sceneLoader as IGameSceneLoaderWithProgress ?? new CoarseSceneLoaderProgressAdapter(sceneLoader);
        }

        private sealed class CoarseSceneLoaderProgressAdapter : IGameSceneLoaderWithProgress
        {
            private readonly IGameSceneLoader _sceneLoader;

            public CoarseSceneLoaderProgressAdapter(IGameSceneLoader sceneLoader)
            {
                _sceneLoader = sceneLoader;
            }

            public Task LoadSceneSingleAsync(string sceneName, CancellationToken cancellationToken = default)
            {
                return _sceneLoader.LoadSceneSingleAsync(sceneName, cancellationToken);
            }

            public Task LoadSceneAdditiveAsync(string sceneName, CancellationToken cancellationToken = default)
            {
                return _sceneLoader.LoadSceneAdditiveAsync(sceneName, cancellationToken);
            }

            public Task LoadSceneSingleAsync(
                string sceneName,
                Action<GameSceneLoadProgressSnapshot>? progressCallback,
                CancellationToken cancellationToken = default)
            {
                return LoadWithCoarseProgressAsync(
                    sceneName,
                    isAdditive: false,
                    progressCallback,
                    _sceneLoader.LoadSceneSingleAsync,
                    cancellationToken);
            }

            public Task LoadSceneAdditiveAsync(
                string sceneName,
                Action<GameSceneLoadProgressSnapshot>? progressCallback,
                CancellationToken cancellationToken = default)
            {
                return LoadWithCoarseProgressAsync(
                    sceneName,
                    isAdditive: true,
                    progressCallback,
                    _sceneLoader.LoadSceneAdditiveAsync,
                    cancellationToken);
            }

            private static async Task LoadWithCoarseProgressAsync(
                string sceneName,
                bool isAdditive,
                Action<GameSceneLoadProgressSnapshot>? progressCallback,
                Func<string, CancellationToken, Task> loadAsync,
                CancellationToken cancellationToken)
            {
                progressCallback?.Invoke(new GameSceneLoadProgressSnapshot(
                    sceneName,
                    isAdditive,
                    RuntimeLoadingOperationStage.SceneLoading,
                    0d,
                    "Scene loading started."));

                await loadAsync(sceneName, cancellationToken).ConfigureAwait(false);

                progressCallback?.Invoke(new GameSceneLoadProgressSnapshot(
                    sceneName,
                    isAdditive,
                    RuntimeLoadingOperationStage.Completed,
                    100d,
                    "Scene loading completed."));
            }
        }
    }
}
