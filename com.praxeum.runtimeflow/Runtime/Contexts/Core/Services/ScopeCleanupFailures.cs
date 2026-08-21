using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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

        public static bool IsObjectDisposedFailure(Exception exception)
        {
            if (exception is ObjectDisposedException) return true;
            if (exception is AggregateException agg) { var f = agg.Flatten().InnerExceptions; return f.Count > 0 && f.All(IsObjectDisposedFailure); }
            if (exception.InnerException != null && IsObjectDisposedFailure(exception.InnerException)) return true;
            return false;
        }

        public static AggregateException CreateCleanupAggregate(string operationName, Exception operationException, IReadOnlyCollection<Exception> cleanupFailures)
        {
            if (operationException == null) throw new ArgumentNullException(nameof(operationException));
            if (cleanupFailures == null || cleanupFailures.Count == 0)
                throw new ArgumentException("Cleanup failures are required.", nameof(cleanupFailures));
            var exceptions = new List<Exception>(cleanupFailures.Count + 1) { operationException };
            foreach (var cleanupFailure in cleanupFailures)
            {
                if (cleanupFailure is AggregateException aggregateCleanupFailure)
                {
                    exceptions.AddRange(aggregateCleanupFailure.Flatten().InnerExceptions);
                    continue;
                }
                exceptions.Add(cleanupFailure);
            }
            return new AggregateException($"{operationName} failed and cleanup encountered additional errors.", exceptions);
        }

        public static async Task<List<Exception>> CaptureCleanupFailuresAsync(CancellationToken cancellationToken, params Func<Task>[] cleanupOperations)
        {
            var failures = new List<Exception>();
            foreach (var cleanupOperation in cleanupOperations)
            {
                if (cleanupOperation == null) continue;
                try { await cleanupOperation().ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (AggregateException aggregateException)
                {
                    var filteredAggregate = FilterCancellationFailures(aggregateException, cancellationToken.IsCancellationRequested);
                    if (filteredAggregate != null) failures.Add(filteredAggregate);
                }
                catch (Exception cleanupException)
                {
                    if (cancellationToken.IsCancellationRequested && IsCancellationFailure(cleanupException)) continue;
                    failures.Add(cleanupException);
                }
            }
            return failures;
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
