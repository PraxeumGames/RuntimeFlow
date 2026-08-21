using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    internal static class GameContextThreadDispatcher
    {
        private static readonly TimeSpan MainThreadDispatchTimeout = TimeSpan.FromMinutes(2);
        private static SynchronizationContext? _mainThreadContext;
        private static int _mainThreadId;
        private static readonly object _sync = new();
        private static bool _warnedMissingMainThreadContext;

        public static SynchronizationContext? MainThreadContext
        {
            get { lock (_sync) return _mainThreadContext; }
        }

        public static void CaptureMainThread()
        {
            lock (_sync)
            {
                _mainThreadId = Thread.CurrentThread.ManagedThreadId;
                _mainThreadContext = SynchronizationContext.Current;
            }
        }

        public static void CaptureMainThreadContext() => CaptureMainThread();

        public static bool IsOnMainThread()
        {
            SynchronizationContext? ctx;
            int mainId;
            lock (_sync)
            {
                ctx = _mainThreadContext;
                mainId = _mainThreadId;
            }
            if (Thread.CurrentThread.ManagedThreadId == mainId && mainId != 0)
                return true;
            return ctx != null && SynchronizationContext.Current == ctx;
        }

        public static Task<T> DispatchToMainThreadAsync<T>(Func<T> action, string operationDescription, CancellationToken cancellationToken = default)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (IsOnMainThread())
                return Task.FromResult(action());

            SynchronizationContext? ctx;
            lock (_sync) ctx = _mainThreadContext;
            if (ctx == null)
            {
                WarnMissingMainThreadContextOnce(operationDescription);
                return Task.FromResult(action());
            }

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            ctx.Post(_ =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                        tcs.TrySetCanceled(cancellationToken);
                    else
                        tcs.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }, null);

            return WaitWithTimeoutAsync(tcs.Task, operationDescription, cancellationToken);
        }

        /// <summary>
        /// Enforces the main-thread resolution contract. On the main thread this is a no-op.
        /// Without a captured SynchronizationContext (headless/EditMode) the operation is
        /// allowed inline with a one-time warning. Otherwise the caller must use the async
        /// API; blocking cross-thread resolution does not exist by design.
        /// </summary>
        public static void EnsureMainThreadOperationAllowed(string operationDescription)
        {
            if (IsOnMainThread()) return;
            lock (_sync)
            {
                if (_mainThreadContext == null)
                {
                    WarnMissingMainThreadContextOnce(operationDescription);
                    return;
                }
            }
            throw new InvalidOperationException(
                $"'{operationDescription}' must run on the Unity main thread. " +
                "From background threads use the async API instead (for example 'await context.ResolveAsync<TService>()'). " +
                "Synchronous cross-thread resolution was removed because it can deadlock the caller against the main thread.");
        }

        internal static void WarnMissingMainThreadContextOnce(string operationDescription)
        {
            if (_warnedMissingMainThreadContext) return;
            _warnedMissingMainThreadContext = true;
            UnityEngine.Debug.LogWarning($"[RuntimeFlow] No main-thread SynchronizationContext was captured; executing '{operationDescription}' on the calling thread. " +
                                         "Call GameContext.CaptureMainThread() during startup to enable correct main-thread marshalling.");
        }

        private static async Task<T> WaitWithTimeoutAsync<T>(Task<T> task, string operationDescription, CancellationToken cancellationToken)
        {
            var delayToken = cancellationToken.CanBeCanceled ? cancellationToken : CancellationToken.None;
            var delayTask = Task.Delay(MainThreadDispatchTimeout, delayToken);
            var completed = await Task.WhenAny(task, delayTask).ConfigureAwait(false);

            if (completed == task)
                return await task.ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);

            throw new TimeoutException($"Timed out while waiting for main-thread dispatch to {operationDescription}.");
        }
    }
}
