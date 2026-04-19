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
                if (ctx == null)
                {
                    await operation(cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (GameContext.IsOnMainThread())
                {
                    await operation(cancellationToken).ConfigureAwait(false);
                    return;
                }

                var tcs = new TaskCompletionSource<bool>();
                ctx.Post(_ =>
                {
                    RunOnPostedContext(operation, cancellationToken, tcs);
                }, null);
                await tcs.Task.ConfigureAwait(false);
                return;
            }

            await operation(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Invoked via <see cref="SynchronizationContext.Post"/> and therefore declared <c>async void</c>.
        /// Exceptions from <paramref name="operation"/> are routed through <paramref name="tcs"/> rather
        /// than escaping to the synchronization context's unhandled-exception handler, making the caller's
        /// awaited task fault or cancel correctly.
        /// </summary>
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
