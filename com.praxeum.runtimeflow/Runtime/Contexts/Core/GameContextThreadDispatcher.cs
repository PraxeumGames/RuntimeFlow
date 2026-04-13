using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace RuntimeFlow.Contexts
{
    internal static class GameContextThreadDispatcher
    {
        private static readonly TimeSpan MainThreadDispatchTimeout = TimeSpan.FromMinutes(2);
        private static SynchronizationContext? _mainThreadContext;
        private static int _mainThreadId;

        public static SynchronizationContext? MainThreadContext => _mainThreadContext;

        public static void CaptureMainThread()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            // SynchronizationContext may not be set up yet during SubsystemRegistration.
            // Capture it here if available; BeforeSceneLoad callback ensures it's set.
            _mainThreadContext = SynchronizationContext.Current;
        }

        public static void CaptureMainThreadContext()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _mainThreadContext = SynchronizationContext.Current;
        }

        public static bool IsOnMainThread()
        {
            if (_mainThreadContext != null && SynchronizationContext.Current == _mainThreadContext)
                return true;

            return Thread.CurrentThread.ManagedThreadId == _mainThreadId;
        }

        public static T DispatchToMainThread<T>(Func<T> action, string operationDescription)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            if (_mainThreadContext == null || IsOnMainThread())
                return action();

            T? result = default;
            ExceptionDispatchInfo? capturedException = null;
            using var completed = new ManualResetEventSlim(false);
            _mainThreadContext.Post(_ =>
            {
                try
                {
                    result = action();
                }
                catch (Exception ex)
                {
                    capturedException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    completed.Set();
                }
            }, null);

            if (!completed.Wait(MainThreadDispatchTimeout))
            {
                throw new TimeoutException(
                    $"Timed out while waiting for main-thread dispatch to {operationDescription}.");
            }

            capturedException?.Throw();
            return result!;
        }
    }
}
