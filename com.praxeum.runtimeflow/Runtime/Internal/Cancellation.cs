using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace RuntimeFlow.Internal
{
    /// <summary>
    /// Cancels without ever throwing. <see cref="CancellationTokenSource.Cancel()"/> runs every registered
    /// callback — user code such as <c>ct.Register(() => request.Abort())</c> — and rethrows their exceptions
    /// as one <see cref="AggregateException"/> after all of them ran. Escaping, that exception would skip
    /// whatever the framework had to do next (settle a run, release dependents, dispose a scope), so it is
    /// logged and the caller carries on.
    /// </summary>
    internal static class Cancellation
    {
        /// <summary>Cancels <paramref name="source"/>; an already disposed source is ignored.</summary>
        /// <param name="source">The source to cancel; null is ignored.</param>
        /// <param name="logger">Receives an Error line per failed cancellation.</param>
        /// <param name="scope">Scope name used in the message.</param>
        /// <param name="what">What was cancelled, for example "the run" or "Catalog".</param>
        public static void Cancel(CancellationTokenSource? source, ILogger logger, string scope, string what)
        {
            if (source == null) return;
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down: nothing left to cancel.
            }
            catch (Exception exception)
            {
                logger.Error($"[RuntimeFlow] {scope}: a cancellation callback threw {Describe(exception)} while cancelling " +
                             $"{what}; the other callbacks ran, continuing.", exception);
            }
        }

        private static string Describe(Exception exception)
        {
            var inner = exception;
            while (inner is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
                inner = aggregate.InnerExceptions[0];
            return $"{inner.GetType().Name} ({inner.Message})";
        }
    }
}
