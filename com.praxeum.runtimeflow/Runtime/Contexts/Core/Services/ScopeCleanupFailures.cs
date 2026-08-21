using System;
using System.Linq;

namespace RuntimeFlow.Contexts
{
    internal static class ScopeCleanupFailures
    {
        public static Exception? FilterCancellationFailures(Exception exception, bool cancellationRequested)
        {
            if (!cancellationRequested) return exception;
            if (exception is not AggregateException agg) return IsCancellationFailure(exception) ? null : exception;
            var nonCancellation = agg.Flatten().InnerExceptions.Where(inner => !IsCancellationFailure(inner)).ToArray();
            return nonCancellation.Length == 0 ? null : new AggregateException(nonCancellation);
        }

        public static bool IsCancellationFailure(Exception exception)
        {
            if (exception is OperationCanceledException) return true;
            if (exception is AggregateException agg) { var f = agg.Flatten().InnerExceptions; return f.Count > 0 && f.All(IsCancellationFailure); }
            return false;
        }

        public static void DisposeAndClearEventBuses(ActiveScopeState state, bool includeGlobal)
        {
            state.ModuleEventBus?.Dispose(); state.ModuleEventBus = null;
            state.SceneEventBus?.Dispose(); state.SceneEventBus = null;
            state.SessionEventBus?.Dispose(); state.SessionEventBus = null;
            if (!includeGlobal) return;
            state.GlobalEventBus?.Dispose(); state.GlobalEventBus = null;
        }
    }
}
