using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

internal sealed class NoopSceneLoader : IGameSceneLoader
{
    public static readonly IGameSceneLoader Instance = new NoopSceneLoader();

    public Task LoadSceneSingleAsync(string sceneName, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task LoadSceneAdditiveAsync(string sceneName, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
