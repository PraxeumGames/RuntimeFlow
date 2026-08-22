using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Flow;

namespace RuntimeFlow.Testing
{
    /// <summary>
    /// A scene loader that completes every request without touching Unity's SceneManager —
    /// lets pipeline scenarios run headless in EditMode tests.
    /// </summary>
    public sealed class NoopSceneLoader : IGameSceneLoader
    {
        public static readonly NoopSceneLoader Instance = new();

        public Task LoadSceneSingleAsync(string sceneName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task LoadSceneAdditiveAsync(string sceneName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
