using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.SceneManagement;

namespace RuntimeFlow.Contexts
{
    internal static class RuntimeFlowSceneUtilities
    {
        internal static Task<bool> IsSceneLoadedAsync(string sceneName, CancellationToken cancellationToken)
        {
            return ExecuteOnMainThreadAsync(() =>
            {
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (scene.isLoaded && scene.name == sceneName)
                        return true;
                }

                return false;
            }, cancellationToken);
        }

        internal static Task<T> ExecuteOnMainThreadAsync<T>(
            Func<T> action,
            CancellationToken cancellationToken)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            cancellationToken.ThrowIfCancellationRequested();

            var context = GameContext.MainThreadContext;
            if (context == null || GameContext.IsOnMainThread())
            {
                return Task.FromResult(action());
            }

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationRegistration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
            context.Post(_ =>
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
