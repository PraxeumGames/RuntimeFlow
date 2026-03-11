using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    /// <summary>Loads Unity scenes asynchronously in single or additive mode.</summary>
    public interface IGameSceneLoader
    {
        Task LoadSceneSingleAsync(string sceneName, CancellationToken cancellationToken = default);
        Task LoadSceneAdditiveAsync(string sceneName, CancellationToken cancellationToken = default);
    }

    /// <summary>Immutable snapshot of a scene-loading operation's current progress.</summary>
    public readonly struct GameSceneLoadProgressSnapshot
    {
        public GameSceneLoadProgressSnapshot(
            string sceneName,
            bool isAdditive,
            RuntimeLoadingOperationStage stage,
            double progressPercent,
            string? message = null)
        {
            if (double.IsNaN(progressPercent) || double.IsInfinity(progressPercent) || progressPercent < 0d || progressPercent > 100d)
            {
                throw new ArgumentOutOfRangeException(nameof(progressPercent), progressPercent, "Progress percent must be between 0 and 100.");
            }

            SceneName = sceneName;
            IsAdditive = isAdditive;
            Stage = stage;
            ProgressPercent = progressPercent;
            Message = message;
        }

        public string SceneName { get; }
        public bool IsAdditive { get; }
        public RuntimeLoadingOperationStage Stage { get; }
        public double ProgressPercent { get; }
        public string? Message { get; }
    }

    /// <summary>Extended scene loader that reports loading progress via a callback.</summary>
    public interface IGameSceneLoaderWithProgress : IGameSceneLoader
    {
        Task LoadSceneSingleAsync(
            string sceneName,
            Action<GameSceneLoadProgressSnapshot>? progressCallback,
            CancellationToken cancellationToken = default);

        Task LoadSceneAdditiveAsync(
            string sceneName,
            Action<GameSceneLoadProgressSnapshot>? progressCallback,
            CancellationToken cancellationToken = default);
    }
}
