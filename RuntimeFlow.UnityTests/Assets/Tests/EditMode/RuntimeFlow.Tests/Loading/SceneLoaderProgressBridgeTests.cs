using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{

public sealed class SceneLoaderProgressBridgeTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task Wrap_EmitsCoarseProgress_WhenLoaderHasNoProgressContract(bool isAdditive)
    {
        var legacyLoader = new RecordingSceneLoader();
        var loader = SceneLoaderProgressBridge.Wrap(legacyLoader);
        var snapshots = new List<GameSceneLoadProgressSnapshot>();

        await LoadAsync(loader, "Gameplay", isAdditive, snapshots.Add);

        Assert.That(legacyLoader.Calls, Has.Count.EqualTo(1));
        Assert.That(legacyLoader.Calls[0], Is.EqualTo(isAdditive ? "additive:Gameplay" : "single:Gameplay"));
        Assert.That(snapshots, Has.Count.EqualTo(2));

        var started = snapshots[0];
        Assert.That(started.SceneName, Is.EqualTo("Gameplay"));
        Assert.That(started.IsAdditive, Is.EqualTo(isAdditive));
        Assert.That(started.Stage, Is.EqualTo(RuntimeLoadingOperationStage.SceneLoading));
        Assert.That(started.ProgressPercent, Is.EqualTo(0d));

        var completed = snapshots[1];
        Assert.That(completed.SceneName, Is.EqualTo("Gameplay"));
        Assert.That(completed.IsAdditive, Is.EqualTo(isAdditive));
        Assert.That(completed.Stage, Is.EqualTo(RuntimeLoadingOperationStage.Completed));
        Assert.That(completed.ProgressPercent, Is.EqualTo(100d));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Wrap_PassesThroughProgressCapableLoader(bool isAdditive)
    {
        var progressLoader = new ProgressCapableSceneLoader();
        var loader = SceneLoaderProgressBridge.Wrap(progressLoader);
        var snapshots = new List<GameSceneLoadProgressSnapshot>();

        await LoadAsync(loader, "Gameplay", isAdditive, snapshots.Add);

        Assert.That(loader, Is.SameAs(progressLoader));
        Assert.That(progressLoader.LegacyCalls, Is.EqualTo(0));
        Assert.That(progressLoader.ProgressCalls, Is.EqualTo(1));
        Assert.That(snapshots, Has.Count.EqualTo(2));

        var first = snapshots[0];
        Assert.That(first.Stage, Is.EqualTo(RuntimeLoadingOperationStage.Preparing));
        Assert.That(first.ProgressPercent, Is.EqualTo(25d));

        var second = snapshots[1];
        Assert.That(second.Stage, Is.EqualTo(RuntimeLoadingOperationStage.SceneLoading));
        Assert.That(second.ProgressPercent, Is.EqualTo(75d));
    }

    [Test]
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

        Assert.That(fallbackSnapshots, Has.Length.EqualTo(2));

        var fallbackStarted = fallbackSnapshots[0];
        Assert.That(fallbackStarted.OperationKind, Is.EqualTo(RuntimeLoadingOperationKind.LoadScene));
        Assert.That(fallbackStarted.Stage, Is.EqualTo(RuntimeLoadingOperationStage.SceneLoading));
        Assert.That(fallbackStarted.State, Is.EqualTo(RuntimeLoadingOperationState.Running));
        Assert.That(fallbackStarted.Percent, Is.EqualTo(0d));

        var fallbackCompleted = fallbackSnapshots[1];
        Assert.That(fallbackCompleted.OperationKind, Is.EqualTo(RuntimeLoadingOperationKind.LoadScene));
        Assert.That(fallbackCompleted.Stage, Is.EqualTo(RuntimeLoadingOperationStage.Completed));
        Assert.That(fallbackCompleted.State, Is.EqualTo(RuntimeLoadingOperationState.Completed));
        Assert.That(fallbackCompleted.Percent, Is.EqualTo(100d));

        Assert.That(passThroughSnapshots, Has.Length.EqualTo(2));

        var preparing = passThroughSnapshots[0];
        Assert.That(preparing.OperationKind, Is.EqualTo(RuntimeLoadingOperationKind.LoadScene));
        Assert.That(preparing.Stage, Is.EqualTo(RuntimeLoadingOperationStage.Preparing));
        Assert.That(preparing.State, Is.EqualTo(RuntimeLoadingOperationState.Running));
        Assert.That(preparing.Percent, Is.EqualTo(25d));

        var sceneLoading = passThroughSnapshots[1];
        Assert.That(sceneLoading.OperationKind, Is.EqualTo(RuntimeLoadingOperationKind.LoadScene));
        Assert.That(sceneLoading.Stage, Is.EqualTo(RuntimeLoadingOperationStage.SceneLoading));
        Assert.That(sceneLoading.State, Is.EqualTo(RuntimeLoadingOperationState.Running));
        Assert.That(sceneLoading.Percent, Is.EqualTo(75d));

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
}
