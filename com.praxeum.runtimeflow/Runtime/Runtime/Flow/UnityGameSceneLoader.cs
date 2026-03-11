using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.SceneManagement;

namespace RuntimeFlow.Contexts
{
    /// <summary>Standard Unity scene loader using SceneManager.LoadSceneAsync.</summary>
    public sealed class UnityGameSceneLoader : IGameSceneLoader
    {
        public Task LoadSceneSingleAsync(string sceneName, CancellationToken cancellationToken = default)
            => LoadSceneAsync(sceneName, LoadSceneMode.Single, cancellationToken);

        public Task LoadSceneAdditiveAsync(string sceneName, CancellationToken cancellationToken = default)
            => LoadSceneAsync(sceneName, LoadSceneMode.Additive, cancellationToken);

        private static async Task LoadSceneAsync(string sceneName, LoadSceneMode mode, CancellationToken cancellationToken)
        {
            var op = SceneManager.LoadSceneAsync(sceneName, mode);
            if (op == null)
                throw new InvalidOperationException($"Failed to load scene '{sceneName}' (mode: {mode}). Ensure the scene is added to Build Settings.");

            while (!op.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }
    }
}
