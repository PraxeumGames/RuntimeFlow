using System;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using Debug = UnityEngine.Debug;

namespace RuntimeFlow
{
    /// <summary>
    /// The default RuntimeFlow logger: routes Microsoft.Extensions.Logging calls to the Unity console.
    /// Trace/Debug/Information become <see cref="Debug.Log(object)"/>, Warning becomes
    /// <see cref="Debug.LogWarning(object)"/>, Error and Critical become <see cref="Debug.LogError(object)"/>
    /// followed by <see cref="Debug.LogException(Exception)"/> when an exception was supplied, so the
    /// console keeps the clickable stack frames of the original failure.
    /// </summary>
    public sealed class UnityConsoleLogger : ILogger
    {
        /// <summary>Prefix added to messages that do not already carry it.</summary>
        public const string Prefix = "[RuntimeFlow] ";

        /// <summary>Shared instance used by <see cref="RuntimeFlowOptions.Logger"/> by default.</summary>
        public static readonly UnityConsoleLogger Default = new UnityConsoleLogger();

        /// <summary>Creates a logger that drops everything below <paramref name="minLevel"/>.</summary>
        /// <param name="minLevel">Lowest level that reaches the console; defaults to <see cref="LogLevel.Debug"/>.</param>
        public UnityConsoleLogger(LogLevel minLevel = LogLevel.Debug) => MinLevel = minLevel;

        /// <summary>Lowest level that reaches the console; fixed when the logger is created.</summary>
        public LogLevel MinLevel { get; }

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= MinLevel;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter != null ? formatter(state, exception) : state?.ToString() ?? string.Empty;
            if (!message.StartsWith(Prefix, StringComparison.Ordinal))
                message = Prefix + message;

            switch (logLevel)
            {
                case LogLevel.Critical:
                case LogLevel.Error:
                    // The message first so the console line reads as RuntimeFlow's own diagnosis, then the
                    // exception as an exception: only LogException keeps the clickable original stack trace.
                    Debug.LogError(message);
                    if (exception != null) Debug.LogException(exception);
                    break;
                case LogLevel.Warning:
                    Debug.LogWarning(Append(message, exception));
                    break;
                default:
                    Debug.Log(Append(message, exception));
                    break;
            }
        }

        private static string Append(string message, Exception? exception)
            => exception == null ? message : message + Environment.NewLine + exception;

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new NullScope();
            public void Dispose() { }
        }
    }
}
