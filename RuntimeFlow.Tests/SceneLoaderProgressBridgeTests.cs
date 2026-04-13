using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using Xunit;

namespace RuntimeFlow.Tests;

public sealed class SceneLoaderProgressBridgeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrap_EmitsCoarseProgress_WhenLoaderHasNoProgressContract(bool isAdditive)
    {
        var legacyLoader = new RecordingSceneLoader();
        var loader = SceneLoaderProgressBridge.Wrap(legacyLoader);
        var snapshots = new List<GameSceneLoadProgressSnapshot>();

        await LoadAsync(loader, "Gameplay", isAdditive, snapshots.Add);

        Assert.Single(legacyLoader.Calls);
        Assert.Equal(isAdditive ? "additive:Gameplay" : "single:Gameplay", legacyLoader.Calls[0]);
        Assert.Collection(
            snapshots,
            started =>
            {
                Assert.Equal("Gameplay", started.SceneName);
                Assert.Equal(isAdditive, started.IsAdditive);
                Assert.Equal(RuntimeLoadingOperationStage.SceneLoading, started.Stage);
                Assert.Equal(0d, started.ProgressPercent);
            },
            completed =>
            {
                Assert.Equal("Gameplay", completed.SceneName);
                Assert.Equal(isAdditive, completed.IsAdditive);
                Assert.Equal(RuntimeLoadingOperationStage.Completed, completed.Stage);
                Assert.Equal(100d, completed.ProgressPercent);
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrap_PassesThroughProgressCapableLoader(bool isAdditive)
    {
        var progressLoader = new ProgressCapableSceneLoader();
        var loader = SceneLoaderProgressBridge.Wrap(progressLoader);
        var snapshots = new List<GameSceneLoadProgressSnapshot>();

        await LoadAsync(loader, "Gameplay", isAdditive, snapshots.Add);

        Assert.Same(progressLoader, loader);
        Assert.Equal(0, progressLoader.LegacyCalls);
        Assert.Equal(1, progressLoader.ProgressCalls);
        Assert.Collection(
            snapshots,
            first =>
            {
                Assert.Equal(RuntimeLoadingOperationStage.Preparing, first.Stage);
                Assert.Equal(25d, first.ProgressPercent);
            },
            second =>
            {
                Assert.Equal(RuntimeLoadingOperationStage.SceneLoading, second.Stage);
                Assert.Equal(75d, second.ProgressPercent);
            });
    }

    [Fact]
    public async Task Wrap_FallbackAndPassThrough_CanStreamIntoRuntimeProgressObserver()
    {
        var runtimeObserver = new CollectingRuntimeLoadingProgressObserver();
        var fallbackLoader = new RuntimeProgressForwardingSceneLoader(
            new RecordingSceneLoader(),
            runtimeObserver,
            operationPrefix: "legacy-scene");
        var passThroughLoader = new RuntimeProgressForwardingSceneLoader(
            new ProgressCapableSceneLoader(),
            runtimeObserver,
            operationPrefix: "progress-scene");

        await fallbackLoader.LoadSceneSingleAsync("LegacyGameplay");
        await passThroughLoader.LoadSceneSingleAsync("ModernGameplay");

        var fallbackSnapshots = runtimeObserver.Snapshots
            .Where(snapshot => snapshot.OperationId.StartsWith("legacy-scene-", StringComparison.Ordinal))
            .ToArray();
        var passThroughSnapshots = runtimeObserver.Snapshots
            .Where(snapshot => snapshot.OperationId.StartsWith("progress-scene-", StringComparison.Ordinal))
            .ToArray();

        Assert.Collection(
            fallbackSnapshots,
            started =>
            {
                Assert.Equal(RuntimeLoadingOperationKind.LoadScene, started.OperationKind);
                Assert.Equal(RuntimeLoadingOperationStage.SceneLoading, started.Stage);
                Assert.Equal(RuntimeLoadingOperationState.Running, started.State);
                Assert.Equal(0d, started.Percent);
            },
            completed =>
            {
                Assert.Equal(RuntimeLoadingOperationKind.LoadScene, completed.OperationKind);
                Assert.Equal(RuntimeLoadingOperationStage.Completed, completed.Stage);
                Assert.Equal(RuntimeLoadingOperationState.Completed, completed.State);
                Assert.Equal(100d, completed.Percent);
            });
        Assert.Collection(
            passThroughSnapshots,
            preparing =>
            {
                Assert.Equal(RuntimeLoadingOperationKind.LoadScene, preparing.OperationKind);
                Assert.Equal(RuntimeLoadingOperationStage.Preparing, preparing.Stage);
                Assert.Equal(RuntimeLoadingOperationState.Running, preparing.State);
                Assert.Equal(25d, preparing.Percent);
            },
            sceneLoading =>
            {
                Assert.Equal(RuntimeLoadingOperationKind.LoadScene, sceneLoading.OperationKind);
                Assert.Equal(RuntimeLoadingOperationStage.SceneLoading, sceneLoading.Stage);
                Assert.Equal(RuntimeLoadingOperationState.Running, sceneLoading.State);
                Assert.Equal(75d, sceneLoading.Percent);
            });

        RuntimeLoadingProgressAssertions.AssertMonotonicStageAndPercent(fallbackSnapshots);
        RuntimeLoadingProgressAssertions.AssertMonotonicStageAndPercent(passThroughSnapshots);
    }

    private static Task LoadAsync(
        IGameSceneLoaderWithProgress loader,
        string sceneName,
        bool isAdditive,
        Action<GameSceneLoadProgressSnapshot> progressCallback)
    {
        return isAdditive
            ? loader.LoadSceneAdditiveAsync(sceneName, progressCallback, CancellationToken.None)
            : loader.LoadSceneSingleAsync(sceneName, progressCallback, CancellationToken.None);
    }

    private sealed class RuntimeProgressForwardingSceneLoader : IGameSceneLoader
    {
        private readonly IGameSceneLoaderWithProgress _loaderWithProgress;
        private readonly IRuntimeLoadingProgressObserver _observer;
        private readonly string _operationPrefix;
        private int _sequence;

        public RuntimeProgressForwardingSceneLoader(
            IGameSceneLoader sceneLoader,
            IRuntimeLoadingProgressObserver observer,
            string operationPrefix)
        {
            _loaderWithProgress = SceneLoaderProgressBridge.Wrap(sceneLoader);
            _observer = observer;
            _operationPrefix = operationPrefix;
        }

        public Task LoadSceneSingleAsync(string sceneName, CancellationToken cancellationToken = default)
        {
            return LoadAsync(
                sceneName,
                static (loader, name, callback, token) => loader.LoadSceneSingleAsync(name, callback, token),
                cancellationToken);
        }

        public Task LoadSceneAdditiveAsync(string sceneName, CancellationToken cancellationToken = default)
        {
            return LoadAsync(
                sceneName,
                static (loader, name, callback, token) => loader.LoadSceneAdditiveAsync(name, callback, token),
                cancellationToken);
        }

        private Task LoadAsync(
            string sceneName,
            Func<IGameSceneLoaderWithProgress, string, Action<GameSceneLoadProgressSnapshot>?, CancellationToken, Task> load,
            CancellationToken cancellationToken)
        {
            var operationId = $"{_operationPrefix}-{Interlocked.Increment(ref _sequence):D2}";
            return load(
                _loaderWithProgress,
                sceneName,
                snapshot => Publish(operationId, snapshot),
                cancellationToken);
        }

        private void Publish(string operationId, GameSceneLoadProgressSnapshot snapshot)
        {
            var state = snapshot.Stage switch
            {
                RuntimeLoadingOperationStage.Completed => RuntimeLoadingOperationState.Completed,
                RuntimeLoadingOperationStage.Failed => RuntimeLoadingOperationState.Failed,
                RuntimeLoadingOperationStage.Canceled => RuntimeLoadingOperationState.Canceled,
                _ => RuntimeLoadingOperationState.Running
            };

            _observer.OnLoadingProgress(new RuntimeLoadingOperationSnapshot(
                operationId: operationId,
                operationKind: RuntimeLoadingOperationKind.LoadScene,
                stage: snapshot.Stage,
                state: state,
                scopeKey: null,
                scopeName: snapshot.SceneName,
                percent: snapshot.ProgressPercent,
                currentStep: state == RuntimeLoadingOperationState.Completed ? 1 : 0,
                totalSteps: 1,
                message: snapshot.Message,
                timestampUtc: DateTimeOffset.UtcNow));
        }
    }

    private sealed class RecordingSceneLoader : IGameSceneLoader
    {
        public List<string> Calls { get; } = new();

        public Task LoadSceneSingleAsync(string sceneName, CancellationToken cancellationToken = default)
        {
            Calls.Add($"single:{sceneName}");
            return Task.CompletedTask;
        }

        public Task LoadSceneAdditiveAsync(string sceneName, CancellationToken cancellationToken = default)
        {
            Calls.Add($"additive:{sceneName}");
            return Task.CompletedTask;
        }
    }

    private sealed class ProgressCapableSceneLoader : IGameSceneLoaderWithProgress
    {
        public int LegacyCalls { get; private set; }
        public int ProgressCalls { get; private set; }

        public Task LoadSceneSingleAsync(string sceneName, CancellationToken cancellationToken = default)
        {
            LegacyCalls++;
            return Task.CompletedTask;
        }

        public Task LoadSceneAdditiveAsync(string sceneName, CancellationToken cancellationToken = default)
        {
            LegacyCalls++;
            return Task.CompletedTask;
        }

        public Task LoadSceneSingleAsync(
            string sceneName,
            Action<GameSceneLoadProgressSnapshot>? progressCallback,
            CancellationToken cancellationToken = default)
        {
            ProgressCalls++;
            progressCallback?.Invoke(new GameSceneLoadProgressSnapshot(sceneName, false, RuntimeLoadingOperationStage.Preparing, 25d));
            progressCallback?.Invoke(new GameSceneLoadProgressSnapshot(sceneName, false, RuntimeLoadingOperationStage.SceneLoading, 75d));
            return Task.CompletedTask;
        }

        public Task LoadSceneAdditiveAsync(
            string sceneName,
            Action<GameSceneLoadProgressSnapshot>? progressCallback,
            CancellationToken cancellationToken = default)
        {
            ProgressCalls++;
            progressCallback?.Invoke(new GameSceneLoadProgressSnapshot(sceneName, true, RuntimeLoadingOperationStage.Preparing, 25d));
            progressCallback?.Invoke(new GameSceneLoadProgressSnapshot(sceneName, true, RuntimeLoadingOperationStage.SceneLoading, 75d));
            return Task.CompletedTask;
        }
    }
}
