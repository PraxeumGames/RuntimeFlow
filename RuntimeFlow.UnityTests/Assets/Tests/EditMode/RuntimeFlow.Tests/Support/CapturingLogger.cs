using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace RuntimeFlow.Tests.Support
{
    /// <summary>
    /// A Microsoft.Extensions.Logging sink that keeps every formatted message with its level, so tests can
    /// assert on RuntimeFlow's exact diagnostics without letting Debug.LogError reach the Unity console.
    /// </summary>
    public sealed class CapturingLogger : ILogger
    {
        /// <summary>One captured log call.</summary>
        public sealed class Entry
        {
            public Entry(LogLevel level, string message, Exception? exception)
            {
                Level = level;
                Message = message;
                Exception = exception;
            }

            public LogLevel Level { get; }
            public string Message { get; }
            public Exception? Exception { get; }

            public override string ToString() => $"{Level}: {Message}";
        }

        private readonly List<Entry> _entries = new List<Entry>();

        /// <summary>Every captured call, in order.</summary>
        public IReadOnlyList<Entry> Entries => _entries;

        /// <summary>Messages captured at the given level, in order.</summary>
        public IReadOnlyList<string> Messages(LogLevel level)
            => _entries.Where(e => e.Level == level).Select(e => e.Message).ToList();

        /// <summary>The first message at the given level containing <paramref name="fragment"/>, or null.</summary>
        public string? Find(LogLevel level, string fragment)
            => _entries.FirstOrDefault(e => e.Level == level && e.Message.Contains(fragment))?.Message;

        /// <summary>True when a message at the given level contains <paramref name="fragment"/>.</summary>
        public bool Has(LogLevel level, string fragment) => Find(level, fragment) != null;

        /// <summary>Everything captured so far, one entry per line; handy in assertion messages.</summary>
        public string Dump() => string.Join(Environment.NewLine, _entries.Select(e => e.ToString()));

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _entries.Add(new Entry(logLevel, formatter(state, exception), exception));
    }
}
