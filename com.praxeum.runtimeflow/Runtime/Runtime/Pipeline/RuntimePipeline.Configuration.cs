using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RuntimeFlow.Contexts
{
    public sealed partial class RuntimePipeline
    {
        public static RuntimePipeline Create(
            Action<GameContextBuilder> configure,
            Action<RuntimePipelineOptions>? configureOptions = null,
            ILoggerFactory? loggerFactory = null)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));

            var options = new RuntimePipelineOptions();
            configureOptions?.Invoke(options);

            var healthSupervisor = RuntimeHealthSupervisor.Create(options);
            var logger = loggerFactory?.CreateLogger<RuntimePipeline>() ?? (ILogger)NullLogger<RuntimePipeline>.Instance;
            var builderLogger = loggerFactory?.CreateLogger<GameContextBuilder>() ?? (ILogger)NullLogger<GameContextBuilder>.Instance;
            var builder = new GameContextBuilder(options.ExecutionScheduler, healthSupervisor, builderLogger);
            configure(builder);
            builder.FlushDeferredScopedRegistrations();
            return CreatePipeline(builder, healthSupervisor, options, logger);
        }

        public static RuntimePipeline CreateFromGlobalContext(
            IGameContext globalContext,
            Action<GameContextBuilder> configure,
            Action<RuntimePipelineOptions>? configureOptions = null,
            ILoggerFactory? loggerFactory = null)
        {
            if (globalContext == null) throw new ArgumentNullException(nameof(globalContext));
            if (configure == null) throw new ArgumentNullException(nameof(configure));

            var options = new RuntimePipelineOptions();
            configureOptions?.Invoke(options);

            var healthSupervisor = RuntimeHealthSupervisor.Create(options);
            var logger = loggerFactory?.CreateLogger<RuntimePipeline>() ?? (ILogger)NullLogger<RuntimePipeline>.Instance;
            var builderLogger = loggerFactory?.CreateLogger<GameContextBuilder>() ?? (ILogger)NullLogger<GameContextBuilder>.Instance;
            var builder = new GameContextBuilder(options.ExecutionScheduler, healthSupervisor, builderLogger);
            builder.UseExternalGlobalContext(globalContext);
            configure(builder);
            builder.FlushDeferredScopedRegistrations();
            return CreatePipeline(builder, healthSupervisor, options, logger);
        }

        public static RuntimePipeline CreateFromResolver(
            VContainer.IObjectResolver globalResolver,
            Action<GameContextBuilder> configure,
            Action<RuntimePipelineOptions>? configureOptions = null,
            ILoggerFactory? loggerFactory = null)
        {
            if (globalResolver == null) throw new ArgumentNullException(nameof(globalResolver));
            return CreateFromGlobalContext(
                new ResolverBackedGameContext(globalResolver),
                configure,
                configureOptions,
                loggerFactory);
        }

        private static RuntimePipeline CreatePipeline(
            GameContextBuilder builder,
            RuntimeHealthSupervisor healthSupervisor,
            RuntimePipelineOptions options,
            ILogger logger)
        {
            var errorClassifier = options.ErrorClassifier ?? DefaultRuntimeErrorClassifier.Instance;
            var retryObserver = options.RetryObserver ?? NullRuntimeRetryObserver.Instance;
            var loadingProgressObserver = options.LoadingProgressObserver ?? NullRuntimeLoadingProgressObserver.Instance;

            return new RuntimePipeline(
                builder,
                healthSupervisor,
                errorClassifier,
                options.RetryPolicy,
                retryObserver,
                loadingProgressObserver,
                options.DefaultProgressNotifier,
                options.ReplayFlowOnSessionRestart,
                options.SessionRestartPreparationHooks,
                logger);
        }

        public RuntimePipeline ConfigureFlow(IRuntimeFlowScenario flow)
        {
            _flow = flow ?? throw new ArgumentNullException(nameof(flow));
            return this;
        }

        public IGameContext SessionContext => _builder.GetSessionContext();

        public RuntimePipeline ConfigureTransitionHandler(IScopeTransitionHandler handler)
        {
            _transitionHandler = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        public RuntimePipeline ConfigureGuards(params IRuntimeFlowGuard[] guards)
        {
            _guards = ComposeGuardsWithRestartPreparationHooks(
                guards ?? throw new ArgumentNullException(nameof(guards)),
                _sessionRestartPreparationHooks);
            return this;
        }

        public RuntimePipeline ConfigureGuards(IEnumerable<IRuntimeFlowGuard> guards)
        {
            if (guards == null) throw new ArgumentNullException(nameof(guards));
            _guards = ComposeGuardsWithRestartPreparationHooks(
                guards is IReadOnlyList<IRuntimeFlowGuard> list ? list : new List<IRuntimeFlowGuard>(guards),
                _sessionRestartPreparationHooks);
            return this;
        }

        public RuntimeStatus GetRuntimeStatus()
        {
            lock (_statusSync)
            {
                return _status;
            }
        }

        public RuntimeReadinessStatus GetReadinessStatus()
        {
            var status = GetRuntimeStatus();
            return new RuntimeReadinessStatus(
                isReady: status.IsReady,
                updatedAtUtc: status.UpdatedAtUtc,
                currentOperationCode: status.CurrentOperationCode,
                blockingReasonCode: status.BlockingReasonCode,
                blockingReason: status.Message);
        }

        public RuntimeRestartReadiness GetRestartReadiness()
        {
            return _restartLifecycleManager.GetRestartReadiness();
        }

        public IRuntimeExecutionContext GetExecutionContext()
        {
            return _executionContextManager.GetExecutionContext();
        }

        RuntimeRestartLifecycleSnapshot IRuntimeRestartLifecycleManager.Snapshot => _restartLifecycleManager.Snapshot;

        Task IRuntimeRestartLifecycleManager.RestartAsync(
            RuntimeRestartRequest request,
            CancellationToken cancellationToken)
        {
            return _restartLifecycleManager.RestartAsync(request, cancellationToken);
        }

        public Task RestartSessionAsync(
            RuntimeRestartRequest request,
            CancellationToken cancellationToken = default)
        {
            return _restartLifecycleManager.RestartAsync(
                request ?? new RuntimeRestartRequest(),
                cancellationToken);
        }

        /// <summary>
        /// Returns the current lifecycle state of the specified scope.
        /// </summary>
        public ScopeLifecycleState GetScopeState<TScope>()
        {
            return GetScopeState(typeof(TScope));
        }

        /// <summary>
        /// Returns the current lifecycle state of the specified scope.
        /// </summary>
        public ScopeLifecycleState GetScopeState(Type scopeType)
        {
            if (scopeType == null) throw new ArgumentNullException(nameof(scopeType));
            return _builder.GetScopeLifecycleState(scopeType);
        }

        /// <summary>
        /// Returns true if the specified scope is fully initialized and activated.
        /// </summary>
        public bool IsScopeActive<TScope>()
        {
            return GetScopeState<TScope>() == ScopeLifecycleState.Active;
        }

        /// <summary>
        /// Returns true if the specified scope is fully initialized and activated.
        /// </summary>
        public bool IsScopeActive(Type scopeType)
        {
            return GetScopeState(scopeType) == ScopeLifecycleState.Active;
        }

        /// <summary>
        /// Returns true if the specified scope can currently be reloaded
        /// (must be active and declared as a restartable scope type).
        /// </summary>
        public bool CanReloadScope<TScope>()
        {
            return CanReloadScope(typeof(TScope));
        }

        /// <summary>
        /// Returns true if the specified scope can currently be reloaded
        /// (must be active and declared as a restartable scope type).
        /// </summary>
        public bool CanReloadScope(Type scopeType)
        {
            if (scopeType == null) throw new ArgumentNullException(nameof(scopeType));
            if (_disposed) return false;

            var state = _builder.GetScopeLifecycleState(scopeType);
            if (state != ScopeLifecycleState.Active) return false;

            if (!_builder.TryResolveScopeType(scopeType, out var contextType)) return false;
            return RuntimeScopeRestartabilityPolicy.IsRestartable(contextType);
        }
    }
}
