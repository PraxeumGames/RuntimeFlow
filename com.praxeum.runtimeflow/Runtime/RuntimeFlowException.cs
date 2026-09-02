using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RuntimeFlow
{
    /// <summary>
    /// Thrown when a required service fails: carries every collected failure plus a snapshot of what
    /// had completed and what was still running or blocked when the run stopped.
    /// </summary>
    public sealed class RuntimeFlowException : Exception
    {
        internal RuntimeFlowException(
            string message,
            string scope,
            string? service,
            string? phase,
            TimeSpan elapsed,
            IReadOnlyList<string> completed,
            IReadOnlyList<string> unfinished,
            IReadOnlyList<(string Service, Exception Error)> failures,
            Exception? innerException)
            : base(message, innerException)
        {
            Scope = scope;
            Service = service;
            Phase = phase;
            Elapsed = elapsed;
            Completed = completed;
            Unfinished = unfinished;
            Failures = failures;
        }

        /// <summary>Creates the exception for a graph-level failure with a ready-made message.</summary>
        public RuntimeFlowException(string scope, string message, Exception? innerException = null)
            : this(message, scope, null, null, TimeSpan.Zero,
                Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<(string, Exception)>(), innerException)
        {
        }

        /// <summary>Name of the scope that failed to initialize.</summary>
        public string Scope { get; }

        /// <summary>Name of the first failing service, or null when the failure is not service-specific.</summary>
        public string? Service { get; }

        /// <summary>Phase the first failing service belonged to, or null.</summary>
        public string? Phase { get; }

        /// <summary>How long the run had been going when it failed.</summary>
        public TimeSpan Elapsed { get; }

        /// <summary>Names of services that had finished, in completion order.</summary>
        public IReadOnlyList<string> Completed { get; }

        /// <summary>Services that never finished, each annotated with why it was blocked.</summary>
        public IReadOnlyList<string> Unfinished { get; }

        /// <summary>Every failure collected during the run, in the order they were observed.</summary>
        public IReadOnlyList<(string Service, Exception Error)> Failures { get; }

        internal static RuntimeFlowException Create(
            string scope,
            string? phase,
            TimeSpan elapsed,
            IReadOnlyList<string> completed,
            IReadOnlyList<string> unfinished,
            IReadOnlyList<(string Service, Exception Error, TimeSpan Elapsed, IReadOnlyList<string> DegradedUpstreams)> failures)
        {
            var text = new StringBuilder();
            text.Append("Initialization of scope '").Append(scope).Append("' failed: ");

            string? service = failures.Count > 0 ? failures[0].Service : null;
            if (failures.Count == 1)
            {
                var (name, error, at, upstreams) = failures[0];
                text.Append(name).Append(" threw ").Append(error.GetType().Name)
                    .Append(" after ").Append(Seconds(at));
                if (phase != null) text.Append(" in phase '").Append(phase).Append('\'');
                if (upstreams != null && upstreams.Count > 0)
                    text.Append(" (after upstream ").Append(Upstreams(upstreams)).Append(" degraded)");
                text.Append(". ");
            }
            else
            {
                text.Append(failures.Count.ToString(CultureInfo.InvariantCulture)).Append(" services failed — ");
                for (var i = 0; i < failures.Count; i++)
                {
                    if (i > 0) text.Append(", ");
                    text.Append(failures[i].Service).Append(" (").Append(failures[i].Error.GetType().Name)
                        .Append(" after ").Append(Seconds(failures[i].Elapsed));
                    var upstreams = failures[i].DegradedUpstreams;
                    if (upstreams != null && upstreams.Count > 0)
                        text.Append(", after upstream ").Append(Upstreams(upstreams)).Append(" degraded");
                    text.Append(')');
                }
                text.Append(". ");
            }

            text.Append("Completed (").Append(completed.Count.ToString(CultureInfo.InvariantCulture)).Append("): ")
                .Append(completed.Count == 0 ? "none" : string.Join(", ", completed)).Append("; ");
            text.Append("unfinished (").Append(unfinished.Count.ToString(CultureInfo.InvariantCulture)).Append("): ")
                .Append(unfinished.Count == 0 ? "none" : string.Join(", ", unfinished)).Append(". ");
            text.Append(failures.Count > 1
                ? "InnerException is an AggregateException with the original exceptions."
                : "See InnerException.");

            var plain = new (string Service, Exception Error)[failures.Count];
            var originals = new Exception[failures.Count];
            for (var i = 0; i < failures.Count; i++)
            {
                plain[i] = (failures[i].Service, failures[i].Error);
                originals[i] = failures[i].Error;
            }

            Exception? inner = failures.Count switch
            {
                0 => null,
                1 => originals[0],
                _ => new AggregateException(originals)
            };

            return new RuntimeFlowException(text.ToString(), scope, service, phase,
                elapsed, completed, unfinished, plain, inner);
        }

        private static string Upstreams(IReadOnlyList<string> names) => string.Join(", ", names);

        private static string Seconds(TimeSpan value)
            => value.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
    }
}
