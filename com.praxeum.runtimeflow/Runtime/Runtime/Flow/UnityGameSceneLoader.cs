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
            var op = await ExecuteOnMainThreadAsync(() => SceneManager.LoadSceneAsync(sceneName, mode), cancellationToken)
                .ConfigureAwait(false);
            if (op == null)
                throw new InvalidOperationException($"Failed to load scene '{sceneName}' (mode: {mode}). Ensure the scene is added to Build Settings.");

            while (!await ExecuteOnMainThreadAsync(() => op.isDone, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        private static Task<T> ExecuteOnMainThreadAsync<T>(Func<T> action, CancellationToken cancellationToken)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            cancellationToken.ThrowIfCancellationRequested();
            var mainThreadContext = GameContext.MainThreadContext;
            if (mainThreadContext == null || GameContext.IsOnMainThread())
            {
                return Task.FromResult(action());
            }

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationRegistration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            mainThreadContext.Post(_ =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    completion.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }, null);

            return AwaitMainThreadCompletionAsync(completion.Task, cancellationRegistration);
        }

        private static async Task<T> AwaitMainThreadCompletionAsync<T>(
            Task<T> task,
            CancellationTokenRegistration cancellationRegistration)
        {
            try
            {
                return await task.ConfigureAwait(false);
            }
            finally
            {
                cancellationRegistration.Dispose();
            }
        }
    }
}
