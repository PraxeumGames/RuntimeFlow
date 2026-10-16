using System;
using Microsoft.Extensions.Logging;

namespace RuntimeFlow.Internal
{
    /// <summary>Disposes VContainer scopes without ever letting one failing service abort the teardown.</summary>
    internal static class ScopeDisposal
    {
        /// <summary>Upper bound on retries, so a pathological scope cannot spin teardown forever.</summary>
        private const int MaxFailures = 64;

        /// <summary>
        /// Disposes <paramref name="scope"/>, logging failures instead of throwing. VContainer disposes a
        /// scope's <see cref="IDisposable"/> registrations from a stack and stops at the first exception,
        /// leaving the rest undisposed; disposing again resumes with the next one, so it is repeated until
        /// the scope disposes cleanly or the retry limit is reached. Exception identity cannot establish
        /// progress: different registrations may throw the same exception instance. Identical failures
        /// (same type, message and stack) are logged once, with the number of repeats at the end.
        /// </summary>
        public static void Dispose(IDisposable scope, ILogger logger, string name)
        {
            Exception? previous = null;
            var repeats = 0;
            try
            {
                for (var failures = 0; failures < MaxFailures; failures++)
                {
                    try
                    {
                        scope.Dispose();
                        return;
                    }
                    catch (Exception exception)
                    {
                        if (previous != null && Identical(exception, previous))
                        {
                            repeats++;
                        }
                        else
                        {
                            Summarize(logger, name, previous, repeats);
                            repeats = 0;
                            logger.Error($"[RuntimeFlow] {name}: disposing the scope threw {exception.GetType().Name}; " +
                                         "continuing with the remaining disposables.", exception);
                        }
                        previous = exception;
                    }
                }
                logger.Error($"[RuntimeFlow] {name}: scope disposal reached the retry limit of {MaxFailures} failures; " +
                             "remaining disposables may not have been released.");
            }
            finally
            {
                Summarize(logger, name, previous, repeats);
            }
        }

        private static void Summarize(ILogger logger, string name, Exception? previous, int repeats)
        {
            if (previous == null || repeats == 0) return;
            logger.Error($"[RuntimeFlow] {name}: disposing the scope threw that {previous.GetType().Name} " +
                         $"({previous.Message}) {repeats.ToString(System.Globalization.CultureInfo.InvariantCulture)} more times.");
        }

        private static bool Identical(Exception a, Exception b)
            => a.GetType() == b.GetType()
               && string.Equals(a.Message, b.Message, StringComparison.Ordinal)
               && string.Equals(a.StackTrace, b.StackTrace, StringComparison.Ordinal);
    }
}
