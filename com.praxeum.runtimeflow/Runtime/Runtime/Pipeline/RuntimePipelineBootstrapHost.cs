using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VContainer;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// High-level bootstrap API for configuring and running RuntimePipeline with provider lifecycle handling.
    /// </summary>
    public static class RuntimePipelineBootstrapHost
    {
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
            var result = new BootstrapResult(pipeline, rootContainer: null, cts);
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
    }
}
