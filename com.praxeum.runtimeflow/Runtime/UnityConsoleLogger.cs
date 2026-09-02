using System;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using Debug = UnityEngine.Debug;

namespace RuntimeFlow
{
    /// <summary>
    /// The default RuntimeFlow logger: routes Microsoft.Extensions.Logging calls to the Unity console.
    /// Trace/Debug/Information become <see cref="Debug.Log(object)"/>, Warning becomes
    /// <see cref="Debug.LogWarning(object)"/>, Error and Critical become <see cref="Debug.LogError(object)"/>.
    /// </summary>
    public sealed class UnityConsoleLogger : ILogger
    {
        /// <summary>Prefix added to messages that do not already carry it.</summary>
        public const string Prefix = "[RuntimeFlow] ";

        /// <summary>Shared instance used by <see cref="RuntimeFlowOptions.Logger"/> by default.</summary>
        public static readonly UnityConsoleLogger Default = new UnityConsoleLogger();

        /// <summary>Lowest level that reaches the console; defaults to <see cref="LogLevel.Debug"/>.</summary>
        public LogLevel MinLevel { get; set; } = LogLevel.Debug;

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
            if (exception != null)
                message = message + Environment.NewLine + exception;

            switch (logLevel)
            {
                case LogLevel.Critical:
                case LogLevel.Error:
                    Debug.LogError(message);
                    break;
                case LogLevel.Warning:
                    Debug.LogWarning(message);
                    break;
                default:
                    Debug.Log(message);
                    break;
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new NullScope();
            public void Dispose() { }
        }
    }
}
