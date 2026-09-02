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

        public bool IsEmpty => _observers.Count == 0;

        public void RunStarted(bool isRestart)
        {
            for (var i = 0; i < _observers.Count; i++)
            {
                try { _observers[i].OnRunStarted(_scope, isRestart); }
                catch (Exception e) { Report(_observers[i], nameof(IRuntimeFlowObserver.OnRunStarted), e); }
            }
        }

        public void PhaseStarted(string phase)
        {
            for (var i = 0; i < _observers.Count; i++)
            {
                try { _observers[i].OnPhaseStarted(_scope, phase); }
                catch (Exception e) { Report(_observers[i], nameof(IRuntimeFlowObserver.OnPhaseStarted), e); }
            }
        }

        public void PhaseCompleted(string phase)
        {
            for (var i = 0; i < _observers.Count; i++)
            {
                try { _observers[i].OnPhaseCompleted(_scope, phase); }
                catch (Exception e) { Report(_observers[i], nameof(IRuntimeFlowObserver.OnPhaseCompleted), e); }
            }
        }

        public void ServiceStarted(ServiceStatus service)
        {
            for (var i = 0; i < _observers.Count; i++)
            {
                try { _observers[i].OnServiceStarted(service); }
                catch (Exception e) { Report(_observers[i], nameof(IRuntimeFlowObserver.OnServiceStarted), e); }
            }
        }

        public void ServiceAwaitingPlayer(ServiceStatus service)
        {
            for (var i = 0; i < _observers.Count; i++)
            {
                try { _observers[i].OnServiceAwaitingPlayer(service); }
                catch (Exception e) { Report(_observers[i], nameof(IRuntimeFlowObserver.OnServiceAwaitingPlayer), e); }
            }
        }

        public void ServiceCompleted(ServiceStatus service)
        {
            for (var i = 0; i < _observers.Count; i++)
            {
                try { _observers[i].OnServiceCompleted(service); }
                catch (Exception e) { Report(_observers[i], nameof(IRuntimeFlowObserver.OnServiceCompleted), e); }
            }
        }

        public void ServiceFailed(ServiceStatus service, Exception error)
        {
            for (var i = 0; i < _observers.Count; i++)
            {
                try { _observers[i].OnServiceFailed(service, error); }
                catch (Exception e) { Report(_observers[i], nameof(IRuntimeFlowObserver.OnServiceFailed), e); }
            }
        }

        public void RunCompleted(StartupResult result)
        {
            for (var i = 0; i < _observers.Count; i++)
            {
                try { _observers[i].OnRunCompleted(_scope, result); }
                catch (Exception e) { Report(_observers[i], nameof(IRuntimeFlowObserver.OnRunCompleted), e); }
            }
        }

        public void RunHalted(StartupResult result)
        {
            for (var i = 0; i < _observers.Count; i++)
            {
                try { _observers[i].OnRunHalted(_scope, result); }
                catch (Exception e) { Report(_observers[i], nameof(IRuntimeFlowObserver.OnRunHalted), e); }
            }
        }

        public void RunFailed(RuntimeFlowException error)
        {
            for (var i = 0; i < _observers.Count; i++)
            {
                try { _observers[i].OnRunFailed(_scope, error); }
                catch (Exception e) { Report(_observers[i], nameof(IRuntimeFlowObserver.OnRunFailed), e); }
            }
        }

        private void Report(IRuntimeFlowObserver observer, string method, Exception error)
            => _logger.Error(
                $"[RuntimeFlow] {_scope}: observer {observer.GetType().Name} threw {error.GetType().Name} in {method}; ignored.",
                error);
    }
}
