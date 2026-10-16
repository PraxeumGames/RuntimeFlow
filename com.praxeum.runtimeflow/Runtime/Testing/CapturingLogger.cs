using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace RuntimeFlow.Testing
{
    /// <summary>
    /// A Microsoft.Extensions.Logging sink that keeps every formatted message with its level, so a test can
    /// assert on RuntimeFlow's exact diagnostics instead of letting them reach the Unity console.
    /// <code>
    /// var log = new CapturingLogger();
    /// var run = ScopeRun.Create(container, "session", new RuntimeFlowOptions { Logger = log });
    /// Assert.That(log.Has(LogLevel.Warning, "is optional and failed"), Is.True, log.Dump());
    /// </code>
    /// </summary>
    public sealed class CapturingLogger : ILogger
    {
        /// <summary>One captured log call.</summary>
        public sealed class Entry
        {
            /// <summary>Creates an entry for a single captured call.</summary>
            /// <param name="level">Level the call was made at.</param>
            /// <param name="message">The formatted message.</param>
            /// <param name="exception">The exception passed alongside the message, if any.</param>
            public Entry(LogLevel level, string message, Exception? exception)
            {
                Level = level;
                Message = message;
                Exception = exception;
            }

            /// <summary>Level the call was made at.</summary>
            public LogLevel Level { get; }

            /// <summary>The formatted message, without any level prefix.</summary>
            public string Message { get; }

            /// <summary>The exception passed alongside the message, or null.</summary>
            public Exception? Exception { get; }

            /// <summary>Renders the entry as "{Level}: {message}".</summary>
            public override string ToString() => Level + ": " + Message;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly List<string> _lines = new List<string>();

        /// <summary>Every captured call, in order.</summary>
        public IReadOnlyList<Entry> Entries => _entries;

        /// <summary>Every captured call rendered as "{Level}: {message}", in order.</summary>
        public IReadOnlyList<string> Lines => _lines;

        /// <summary>Messages captured at the given level, in order.</summary>
        /// <param name="level">The level to filter by.</param>
        public IReadOnlyList<string> Messages(LogLevel level)
        {
            var result = new List<string>();
            foreach (var entry in _entries)
            {
                if (entry.Level == level) result.Add(entry.Message);
            }
            return result;
        }

        /// <summary>The first message at the given level containing <paramref name="fragment"/>, or null.</summary>
        /// <param name="level">The level to filter by.</param>
        /// <param name="fragment">Substring the message must contain.</param>
        public string? Find(LogLevel level, string fragment)
        {
            foreach (var entry in _entries)
            {
                if (entry.Level == level && entry.Message.Contains(fragment)) return entry.Message;
            }
            return null;
        }

        /// <summary>True when a message at the given level contains <paramref name="fragment"/>.</summary>
        /// <param name="level">The level to filter by.</param>
        /// <param name="fragment">Substring the message must contain.</param>
        public bool Has(LogLevel level, string fragment) => Find(level, fragment) != null;

        /// <summary>Everything captured so far, one entry per line; handy in assertion messages.</summary>
        public string Dump() => string.Join(Environment.NewLine, _lines);

        /// <summary>Drops everything captured so far.</summary>
        public void Clear()
        {
            _entries.Clear();
            _lines.Clear();
        }

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter != null ? formatter(state, exception) : state?.ToString() ?? string.Empty;
            var entry = new Entry(logLevel, message, exception);
            _entries.Add(entry);
            _lines.Add(entry.ToString());
        }
    }
}
