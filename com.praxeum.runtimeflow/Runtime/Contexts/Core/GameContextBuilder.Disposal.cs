using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RuntimeFlow.Contexts
{
    public partial class GameContextBuilder
    {
        private async Task DisposeContextAsync(GameContext? context, CancellationToken cancellationToken)
        {
            if (context == null)
                return;

            await context.DisposeAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task DisposeContextAsync(IGameContext? context, CancellationToken cancellationToken)
        {
            if (context == null)
                return;

            await _executionScheduler.ExecuteAsync(
                    InitializationThreadAffinity.MainThread,
                    _ =>
                    {
                        context.Dispose();
                        return Task.CompletedTask;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private static CancellationToken CreateFailureCleanupCancellationToken()
        {
            return CancellationToken.None;
        }

        private async Task DisposeScopeContextAsync(
            GameContextType scope,
            GameContext? context,
            CancellationToken cancellationToken,
            Type? scopeKey = null,
            Action? onDisposed = null)
        {
            if (context == null)
                return;

            List<Exception>? exceptions = null;

            try
            {
                await DisposeContextAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (IsObjectDisposedFailure(ex))
                {
                    _logger.LogWarning(
                        ex,
                        "Ignoring disposed object failure while disposing {Scope} context.",
                        scope);
                }
                else
                {
                    exceptions ??= new List<Exception>();
                    exceptions.Add(ex);
                }
            }

            onDisposed?.Invoke();

            if (exceptions != null)
                throw new AggregateException(exceptions);
        }

        private async Task DisposeActivatedScopeAsync(
            GameContextType scope,
            GameContext? context,
            IInitializationProgressNotifier progressNotifier,
            CancellationToken cancellationToken,
            Type? scopeKey = null,
            ScopeLifecycleState? transitionState = null)
        {
            await _scopeTransitions.ExitActivatedScopeAsync(
                    scope,
                    context,
                    scopeKey,
                    transitionState,
                    progressNotifier,
                    cancellationToken,
                    () => { })
                .ConfigureAwait(false);
        }

        private async Task DisposeOwnedGlobalContextAsync(IGameContext? context, CancellationToken cancellationToken)
        {
            if (context == null)
                return;

            if (context is GameContext gameContext)
            {
                await DisposeScopeContextAsync(
                        GameContextType.Global,
                        gameContext,
                        cancellationToken,
                        onDisposed: () => SetScopeStateIfTracked(GameContextType.Global, ScopeLifecycleState.Disposed))
                    .ConfigureAwait(false);
                return;
            }

            await DisposeContextAsync(context, cancellationToken).ConfigureAwait(false);
            SetScopeStateIfTracked(GameContextType.Global, ScopeLifecycleState.Disposed);
        }

        private void DisposeAndClearEventBuses(bool includeGlobal)
        {
            _moduleEventBus?.Dispose();
            _moduleEventBus = null;

            _sceneEventBus?.Dispose();
            _sceneEventBus = null;

            _sessionEventBus?.Dispose();
            _sessionEventBus = null;

            if (!includeGlobal)
                return;

            _globalEventBus?.Dispose();
            _globalEventBus = null;
        }

        private async Task<List<Exception>> CaptureCleanupFailuresAsync(
            CancellationToken cancellationToken,
            params Func<Task>[] cleanupOperations)
        {
            var failures = new List<Exception>();
            foreach (var cleanupOperation in cleanupOperations)
            {
                if (cleanupOperation == null)
                    continue;

                try
                {
                    await cleanupOperation().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (AggregateException aggregateException)
                {
                    var filteredAggregate = FilterCancellationFailures(aggregateException, cancellationToken.IsCancellationRequested);
                    if (filteredAggregate != null)
                    {
                        failures.Add(filteredAggregate);
                    }
                }
                catch (Exception cleanupException)
                {
                    if (cancellationToken.IsCancellationRequested && IsCancellationFailure(cleanupException))
                        continue;

                    failures.Add(cleanupException);
                }
            }

            return failures;
        }

        private static Exception? FilterCancellationFailures(Exception exception, bool cancellationRequested)
        {
            if (!cancellationRequested)
                return exception;

            if (exception is not AggregateException aggregateException)
                return IsCancellationFailure(exception) ? null : exception;

            var nonCancellationFailures = aggregateException
                .Flatten()
                .InnerExceptions
                .Where(inner => !IsCancellationFailure(inner))
                .ToArray();

            return nonCancellationFailures.Length == 0
                ? null
                : new AggregateException(nonCancellationFailures);
        }

        private static bool IsCancellationFailure(Exception exception)
        {
            if (exception is OperationCanceledException)
                return true;

            if (exception is AggregateException aggregateException)
            {
                var flattened = aggregateException.Flatten().InnerExceptions;
                return flattened.Count > 0 && flattened.All(IsCancellationFailure);
            }

            return false;
        }

        private static bool IsObjectDisposedFailure(Exception exception)
        {
            if (exception is ObjectDisposedException)
                return true;

            if (exception is AggregateException aggregateException)
            {
                var flattened = aggregateException.Flatten().InnerExceptions;
                return flattened.Count > 0 && flattened.All(IsObjectDisposedFailure);
            }

            if (exception.InnerException != null && IsObjectDisposedFailure(exception.InnerException))
                return true;

            return exception.Message?.IndexOf("Cannot access a disposed object.", StringComparison.Ordinal) >= 0;
        }

        private static AggregateException CreateCleanupAggregateException(
            string operationName,
            Exception operationException,
            IReadOnlyCollection<Exception> cleanupFailures)
        {
            if (operationException == null) throw new ArgumentNullException(nameof(operationException));
            if (cleanupFailures == null || cleanupFailures.Count == 0)
                throw new ArgumentException("Cleanup failures are required.", nameof(cleanupFailures));

            var exceptions = new List<Exception>(cleanupFailures.Count + 1)
            {
                operationException
            };

            foreach (var cleanupFailure in cleanupFailures)
            {
                if (cleanupFailure is AggregateException aggregateCleanupFailure)
                {
                    exceptions.AddRange(aggregateCleanupFailure.Flatten().InnerExceptions);
                    continue;
                }

                exceptions.Add(cleanupFailure);
            }

            return new AggregateException(
                $"{operationName} failed and cleanup encountered additional errors.",
                exceptions);
        }

    }
}
