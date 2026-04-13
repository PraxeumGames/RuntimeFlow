using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    public sealed class RuntimeRestartCoordinator : IRuntimeRestartCoordinator
    {
        private readonly object _sync = new object();
        private readonly Func<IRuntimeReadinessGate> _readinessGateFactory;
        private readonly Func<IRuntimeRestartLifecycleManager> _restartLifecycleManagerProvider;
        private Task<RuntimeRestartExecutionResult> _inFlightTask;
        private long _inFlightGeneration;

        public RuntimeRestartCoordinator(
            Func<IRuntimeReadinessGate> readinessGateFactory,
            Func<IRuntimeRestartLifecycleManager> restartLifecycleManagerProvider)
        {
            _readinessGateFactory = readinessGateFactory ?? throw new ArgumentNullException(nameof(readinessGateFactory));
            _restartLifecycleManagerProvider = restartLifecycleManagerProvider ?? throw new ArgumentNullException(nameof(restartLifecycleManagerProvider));
        }

        public RuntimeRestartDispatch Dispatch(RuntimeRestartCoordinatorRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            lock (_sync)
            {
                if (_inFlightTask != null && !_inFlightTask.IsCompleted)
                {
                    return RuntimeRestartDispatch.Deduplicated(
                        RuntimeRestartDuplicateReason.InFlight,
                        _inFlightTask);
                }

                var restartLifecycleManager = _restartLifecycleManagerProvider();
                if (restartLifecycleManager != null && restartLifecycleManager.Snapshot.IsInProgress)
                {
                    return RuntimeRestartDispatch.Deduplicated(
                        RuntimeRestartDuplicateReason.LifecycleInProgress,
                        Task.FromResult(
                            RuntimeRestartExecutionResult.Deduplicated(
                                request.RestartRequest,
                                RuntimeRestartDuplicateReason.LifecycleInProgress)));
                }

                var generation = ++_inFlightGeneration;
                _inFlightTask = ExecuteInternalAsync(request, generation);
                return RuntimeRestartDispatch.Started(_inFlightTask);
            }
        }

        private async Task<RuntimeRestartExecutionResult> ExecuteInternalAsync(
            RuntimeRestartCoordinatorRequest request,
            long generation)
        {
            IRuntimeReadinessGate readinessGate = null;
            var readinessWaitCompleted = false;
            var cancellationToken = request.CancellationToken;

            try
            {
                readinessGate = _readinessGateFactory();
                if (readinessGate != null)
                {
                    await readinessGate
                        .WaitUntilReadyAsync(
                            request.ReadinessTimeout,
                            request.ReadinessPollInterval,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                readinessWaitCompleted = true;

                var restartLifecycleManager = _restartLifecycleManagerProvider();
                if (restartLifecycleManager == null)
                {
                    return RuntimeRestartExecutionResult.LifecycleManagerMissing(request.RestartRequest);
                }

                if (request.OnBeforeRestartAsync != null)
                {
                    await request.OnBeforeRestartAsync(cancellationToken).ConfigureAwait(false);
                }

                await restartLifecycleManager
                    .RestartAsync(request.RestartRequest, cancellationToken)
                    .ConfigureAwait(false);
                return RuntimeRestartExecutionResult.Completed(request.RestartRequest);
            }
            catch (OperationCanceledException) when (!readinessWaitCompleted && !cancellationToken.IsCancellationRequested)
            {
                return RuntimeRestartExecutionResult.TimedOut(
                    request.RestartRequest,
                    readinessGate?.GetRestartReadiness());
            }
            catch (Exception ex)
            {
                return RuntimeRestartExecutionResult.Failed(request.RestartRequest, ex);
            }
            finally
            {
                lock (_sync)
                {
                    if (_inFlightGeneration == generation)
                    {
                        _inFlightTask = null;
                    }
                }
            }
        }
    }
}
