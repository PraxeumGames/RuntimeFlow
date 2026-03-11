using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    public enum InitializationThreadAffinity
    {
        AnyThread = 0,
        MainThread = 1
    }

    public interface IInitializationThreadAffinityProvider
    {
        InitializationThreadAffinity ThreadAffinity { get; }
    }

    public interface IInitializationExecutionScheduler
    {
        Task ExecuteAsync(
            InitializationThreadAffinity affinity,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken);
    }

    internal sealed class InlineInitializationExecutionScheduler : IInitializationExecutionScheduler
    {
        public static readonly IInitializationExecutionScheduler Instance = new InlineInitializationExecutionScheduler();

        private InlineInitializationExecutionScheduler() { }

        public async Task ExecuteAsync(
            InitializationThreadAffinity affinity,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            cancellationToken.ThrowIfCancellationRequested();

            if (affinity == InitializationThreadAffinity.MainThread)
            {
                var ctx = GameContext.MainThreadContext;
                if (ctx != null && SynchronizationContext.Current != ctx)
                {
                    var tcs = new TaskCompletionSource<bool>();
                    ctx.Post(_ =>
                    {
                        RunOnPostedContext(operation, cancellationToken, tcs);
                    }, null);
                    await tcs.Task.ConfigureAwait(false);
                    return;
                }
            }

            await operation(cancellationToken).ConfigureAwait(false);
        }

        private static async void RunOnPostedContext(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken,
            TaskCompletionSource<bool> tcs)
        {
            try
            {
                await operation(cancellationToken);
                tcs.TrySetResult(true);
            }
            catch (OperationCanceledException ex)
            {
                tcs.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }
    }
}
