using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Scene loader that records load requests without performing real scene operations.
    /// Use in integration tests where scene loading is not needed.
    /// </summary>
    public sealed class NoOpSceneLoader : IGameSceneLoader
    {
        private readonly List<string> _loadedScenes = new();

        /// <summary>Scenes loaded via LoadSceneSingleAsync (in order).</summary>
        public IReadOnlyList<string> LoadedScenes => _loadedScenes;

        public Task LoadSceneSingleAsync(string sceneName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _loadedScenes.Clear();
            _loadedScenes.Add(sceneName);
            return Task.CompletedTask;
        }

        public Task LoadSceneAdditiveAsync(string sceneName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _loadedScenes.Add(sceneName);
            return Task.CompletedTask;
        }

        /// <summary>Resets the tracked scene list.</summary>
        public void Reset() => _loadedScenes.Clear();
    }
}
