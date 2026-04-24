using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VContainer;

namespace RuntimeFlow.Contexts
{
    public sealed class RuntimeFlowStartupBootstrapOptions
    {
        public IGameSceneLoader? SceneLoader { get; set; }
        public RestartAwareSceneBootstrapScenarioOptions? ScenarioOptions { get; set; }
        public Action<GameContextBuilder, IPreBootstrapStageService?>? ConfigureBuilder { get; set; }
        public IEnumerable<IRuntimeFlowGuard>? Guards { get; set; }
        public Action<RuntimePipelineOptions>? ConfigurePipelineOptions { get; set; }
        public Microsoft.Extensions.Logging.ILoggerFactory? LoggerFactory { get; set; }
        public IPreBootstrapStageService? PreBootstrapStageService { get; set; }
        public Func<IPreBootstrapStageService>? PreBootstrapStageServiceFactory { get; set; }
        public Action? OnPreBootstrapCompleted { get; set; }
    }

    /// <summary>
    /// High-level bootstrap API for configuring and running RuntimePipeline with provider lifecycle handling.
    /// </summary>
    public static class RuntimePipelineBootstrapHost
    {
        public static async Task<BootstrapResult> RunRestartAwareSceneBootstrapAsync(
            RuntimeFlowStartupBootstrapOptions options,
            CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.SceneLoader == null) throw new ArgumentException("Scene loader is required.", nameof(options));
            if (options.ScenarioOptions == null) throw new ArgumentException("Scenario options are required.", nameof(options));

            var callbackContext = SynchronizationContext.Current;
            var scenarioOptions = CloneRestartAwareSceneBootstrapOptions(options.ScenarioOptions);
            var preBootstrapStageService = await EnsurePreBootstrapReadyAsync(
                    options.PreBootstrapStageService ?? scenarioOptions.PreBootstrapStageService,
                    options.PreBootstrapStageServiceFactory,
                    options.OnPreBootstrapCompleted,
                    callbackContext,
                    cancellationToken)
                .ConfigureAwait(false);

            if (preBootstrapStageService != null)
                scenarioOptions.PreBootstrapStageService = preBootstrapStageService;

            return await RunAsync(
                    configure: builder => options.ConfigureBuilder?.Invoke(builder, preBootstrapStageService),
                    sceneLoader: options.SceneLoader,
                    scenario: RuntimeFlowPresets.RestartAwareSceneBootstrap(scenarioOptions),
                    guards: options.Guards,
                    configureOptions: options.ConfigurePipelineOptions,
                    loggerFactory: options.LoggerFactory,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        public static async Task<BootstrapResult> RunAsync(
            Action<GameContextBuilder, IPreBootstrapStageService?> configure,
            IGameSceneLoader sceneLoader,
            IRuntimeFlowScenario scenario,
            IPreBootstrapStageService? preBootstrapStageService = null,
            Func<IPreBootstrapStageService>? preBootstrapStageServiceFactory = null,
            Action? onPreBootstrapCompleted = null,
            IEnumerable<IRuntimeFlowGuard>? guards = null,
            Action<RuntimePipelineOptions>? configureOptions = null,
            Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null,
            CancellationToken cancellationToken = default)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));

            var callbackContext = SynchronizationContext.Current;
            var effectivePreBootstrapStageService = await EnsurePreBootstrapReadyAsync(
                    preBootstrapStageService,
                    preBootstrapStageServiceFactory,
                    onPreBootstrapCompleted,
                    callbackContext,
                    cancellationToken)
                .ConfigureAwait(false);

            return await RunAsync(
                    configure: builder => configure(builder, effectivePreBootstrapStageService),
                    sceneLoader: sceneLoader,
                    scenario: scenario,
                    guards: guards,
                    configureOptions: configureOptions,
                    loggerFactory: loggerFactory,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        private static Task InvokeOnCapturedContextAsync(
            Action? callback,
            SynchronizationContext? context,
            CancellationToken cancellationToken)
        {
            if (callback == null)
                return Task.CompletedTask;

            cancellationToken.ThrowIfCancellationRequested();
            if (context == null || SynchronizationContext.Current == context)
            {
                callback();
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            context.Post(_ =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    callback();
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }, null);

            return tcs.Task;
        }

        private static async Task<IPreBootstrapStageService?> EnsurePreBootstrapReadyAsync(
            IPreBootstrapStageService? preBootstrapStageService,
            Func<IPreBootstrapStageService>? preBootstrapStageServiceFactory,
            Action? onPreBootstrapCompleted,
            SynchronizationContext? callbackContext,
            CancellationToken cancellationToken)
        {
            var effectivePreBootstrapStageService = preBootstrapStageService ?? preBootstrapStageServiceFactory?.Invoke();
            if (effectivePreBootstrapStageService == null)
            {
                return null;
            }

            await effectivePreBootstrapStageService.EnsureCompletedAsync(cancellationToken).ConfigureAwait(false);
            PreBootstrapRuntimeValidator.EnsureSucceeded(
                effectivePreBootstrapStageService,
                "Prebootstrap must be completed before continuing runtime bootstrap.");
            await InvokeOnCapturedContextAsync(onPreBootstrapCompleted, callbackContext, cancellationToken)
                .ConfigureAwait(false);
            return effectivePreBootstrapStageService;
        }

        public static Task<BootstrapResult> RunAsync(
            Action<GameContextBuilder> configure,
            IGameSceneLoader sceneLoader,
            IRuntimeFlowScenario scenario,
            IEnumerable<IRuntimeFlowGuard>? guards = null,
            Action<RuntimePipelineOptions>? configureOptions = null,
            Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null,
            CancellationToken cancellationToken = default)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));

            var pipeline = RuntimePipeline.Create(configure, configureOptions, loggerFactory);
            return RunAsync(
                pipeline,
                sceneLoader,
                scenario,
                guards,
                cancellationToken);
        }

        public static async Task<BootstrapResult> RunAsync(
            RuntimePipeline pipeline,
            IGameSceneLoader sceneLoader,
            IRuntimeFlowScenario scenario,
            IEnumerable<IRuntimeFlowGuard>? guards = null,
            CancellationToken cancellationToken = default)
        {
            if (pipeline == null) throw new ArgumentNullException(nameof(pipeline));
            if (sceneLoader == null) throw new ArgumentNullException(nameof(sceneLoader));
            if (scenario == null) throw new ArgumentNullException(nameof(scenario));

            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var result = new BootstrapResult(pipeline, rootContainer: null!, cts); // rootContainer intentionally null at bootstrap; BootstrapResult.cs (other agent) declares non-nullable
            IRuntimeFlowPipelineProvider? pipelineProvider = null;

            try
            {
                if (guards != null)
                    pipeline.ConfigureGuards(guards);

                pipeline.ConfigureFlow(scenario);
                await pipeline.RunAsync(sceneLoader, cancellationToken: cts.Token).ConfigureAwait(false);
                result.SessionContext = pipeline.SessionContext;

                pipelineProvider = TryResolvePipelineProvider(result.SessionContext?.Resolver);
                if (pipelineProvider != null)
                {
                    pipelineProvider.SetCurrent(pipeline, sceneLoader);
                    result.BindPipelineProvider(pipelineProvider);
                }

                result.MarkCompleted(true);
                return result;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                pipelineProvider ??= TryResolvePipelineProvider(result.SessionContext?.Resolver);
                pipelineProvider?.ClearCurrent(pipeline);
                result.MarkCompleted(false);
                throw;
            }
            catch
            {
                pipelineProvider ??= TryResolvePipelineProvider(result.SessionContext?.Resolver);
                pipelineProvider?.ClearCurrent(pipeline);
                result.MarkCompleted(false);
                throw;
            }
        }

        private static IRuntimeFlowPipelineProvider? TryResolvePipelineProvider(IObjectResolver? resolver)
        {
            if (resolver == null)
            {
                return null;
            }

            return resolver.TryResolve<IRuntimeFlowPipelineProvider>(out var provider)
                ? provider
                : null;
        }

        private static RestartAwareSceneBootstrapScenarioOptions CloneRestartAwareSceneBootstrapOptions(
            RestartAwareSceneBootstrapScenarioOptions source)
        {
            return new RestartAwareSceneBootstrapScenarioOptions
            {
                SceneName = source.SceneName,
                PreBootstrapStageService = source.PreBootstrapStageService,
                LoadingState = source.LoadingState,
                ReplayReloadScopeType = source.ReplayReloadScopeType,
                RunStageName = source.RunStageName,
                PreBootstrapStageName = source.PreBootstrapStageName,
                RunStartReasonCode = source.RunStartReasonCode,
                ReplayRunStartReasonCode = source.ReplayRunStartReasonCode,
                RunCompleteReasonCode = source.RunCompleteReasonCode,
                RunFailReasonCode = source.RunFailReasonCode,
                PreBootstrapReasonCodeResolver = source.PreBootstrapReasonCodeResolver,
                PreBootstrapFailedReasonCodeFallback = source.PreBootstrapFailedReasonCodeFallback,
                PreBootstrapFailedDiagnosticFallback = source.PreBootstrapFailedDiagnosticFallback
            };
        }
    }
}
