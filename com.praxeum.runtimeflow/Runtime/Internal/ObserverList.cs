using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace RuntimeFlow.Internal
{
    /// <summary>Direct, allocation-free logging helpers that never treat a message as a format template.</summary>
    internal static class RfLogger
    {
        private static readonly Func<string, Exception?, string> Formatter = (state, _) => state;

        /// <summary>Writes a message at <see cref="LogLevel.Debug"/>.</summary>
        public static void Debug(this ILogger logger, string message)
            => logger.Log(LogLevel.Debug, default, message, null, Formatter);

        /// <summary>Writes a message at <see cref="LogLevel.Information"/>.</summary>
        public static void Info(this ILogger logger, string message)
            => logger.Log(LogLevel.Information, default, message, null, Formatter);

        /// <summary>Writes a message at <see cref="LogLevel.Warning"/>.</summary>
        public static void Warn(this ILogger logger, string message)
            => logger.Log(LogLevel.Warning, default, message, null, Formatter);

        /// <summary>Writes a message at <see cref="LogLevel.Error"/>.</summary>
        public static void Error(this ILogger logger, string message, Exception? exception = null)
            => logger.Log(LogLevel.Error, default, message, exception, Formatter);
    }

    /// <summary>
    /// Fans run and service events out to the configured observers; an observer that throws is
    /// logged and skipped so it can never break a run.
    /// </summary>
    internal sealed class ObserverList
    {
        private readonly IReadOnlyList<IRuntimeFlowObserver> _observers;
        private readonly ILogger _logger;
        private readonly string _scope;

        public ObserverList(IReadOnlyList<IRuntimeFlowObserver> observers, ILogger logger, string scope)
        {
            _observers = observers;
            _logger = logger;
            _scope = scope;
        }

        public void RunStarted(bool isRestart)
            => Fan(o => o.OnRunStarted(_scope, isRestart), nameof(IRuntimeFlowObserver.OnRunStarted));

        public void PhaseStarted(string phase)
            => Fan(o => o.OnPhaseStarted(_scope, phase), nameof(IRuntimeFlowObserver.OnPhaseStarted));

        public void PhaseCompleted(string phase)
            => Fan(o => o.OnPhaseCompleted(_scope, phase), nameof(IRuntimeFlowObserver.OnPhaseCompleted));

        public void ServiceStarted(ServiceStatus service)
            => Fan(o => o.OnServiceStarted(service), nameof(IRuntimeFlowObserver.OnServiceStarted));

        public void ServiceAwaitingPlayer(ServiceStatus service)
            => Fan(o => o.OnServiceAwaitingPlayer(service), nameof(IRuntimeFlowObserver.OnServiceAwaitingPlayer));

        public void ServiceCompleted(ServiceStatus service)
            => Fan(o => o.OnServiceCompleted(service), nameof(IRuntimeFlowObserver.OnServiceCompleted));

        public void ServiceFailed(ServiceStatus service, Exception error)
            => Fan(o => o.OnServiceFailed(service, error), nameof(IRuntimeFlowObserver.OnServiceFailed));

        public void RunCompleted(StartupResult result)
            => Fan(o => o.OnRunCompleted(_scope, result), nameof(IRuntimeFlowObserver.OnRunCompleted));

        public void RunHalted(StartupResult result)
            => Fan(o => o.OnRunHalted(_scope, result), nameof(IRuntimeFlowObserver.OnRunHalted));

        public void RunFailed(RuntimeFlowException error)
            => Fan(o => o.OnRunFailed(_scope, error), nameof(IRuntimeFlowObserver.OnRunFailed));

        /// <summary>
        /// Delivers one event to every observer in registration order; an observer that throws is reported
        /// and skipped, so a broken listener can never take the run down with it.
        /// </summary>
        private void Fan(Action<IRuntimeFlowObserver> call, string method)
        {
            for (var i = 0; i < _observers.Count; i++)
            {
                try { call(_observers[i]); }
                catch (Exception e) { Report(_observers[i], method, e); }
            }
        }

        private void Report(IRuntimeFlowObserver observer, string method, Exception error)
            => _logger.Error(
                $"[RuntimeFlow] {_scope}: observer {observer.GetType().Name} threw {error.GetType().Name} in {method}; ignored.",
                error);
    }
}
