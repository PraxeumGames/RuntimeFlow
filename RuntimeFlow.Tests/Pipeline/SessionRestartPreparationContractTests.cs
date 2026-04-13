using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using SFS.Core.GameLoading;

namespace RuntimeFlow.Tests;

public sealed class SessionRestartPreparationContractTests
{
    [Fact]
    public async Task RestartSessionAsync_WhenLegacyRestartAwareServicesExistWithoutHooks_FailsLoudly()
    {
        var restartAware = new RecordingRestartAwareService();
        var pipeline = CreateSessionReloadPipeline(
            configureBuilder: builder => builder.Session().RegisterInstance<ISessionRestartAware>(restartAware));

        var exception = await Assert.ThrowsAsync<RuntimeFlowGuardFailedException>(
            () => pipeline.RunAsync(NoopSceneLoader.Instance));

        Assert.Equal(RuntimeFlowGuardStage.BeforeSessionRestart, exception.Stage);
        Assert.Equal("restart.preparation.hooks.missing", exception.ReasonCode);
        Assert.Contains("No session restart preparation hooks are registered", exception.Message);
        Assert.Equal(0, restartAware.BeforeSessionRestartCalls);
    }

    [Fact]
    public async Task RestartSessionAsync_WhenNoLegacyServicesAndNoHooks_AllowsRestart()
    {
        var pipeline = CreateSessionReloadPipeline();

        await pipeline.RunAsync(NoopSceneLoader.Instance);
    }

    [Fact]
    public async Task RestartSessionAsync_WhenHookPresentInSession_UsesHookInsteadOfLegacyFallback()
    {
        var restartAware = new RecordingRestartAwareService();
        var sessionHook = new RecordingPreparationHook();
        var pipeline = CreateSessionReloadPipeline(
            configureBuilder: builder =>
            {
                builder.Session().RegisterInstance<IRuntimeSessionRestartPreparationHook>(sessionHook);
                builder.Session().RegisterInstance<ISessionRestartAware>(restartAware);
            });

        await pipeline.RunAsync(NoopSceneLoader.Instance);

        Assert.Equal(1, sessionHook.CallCount);
        Assert.Equal(0, restartAware.BeforeSessionRestartCalls);
    }

    [Fact]
    public async Task RestartSessionAsync_WhenHookPresentInOptionsAndSession_InvokesItOnce()
    {
        var restartAware = new RecordingRestartAwareService();
        var sharedHook = new RecordingPreparationHook();
        var pipeline = CreateSessionReloadPipeline(
            configureBuilder: builder =>
            {
                builder.Session().RegisterInstance<IRuntimeSessionRestartPreparationHook>(sharedHook);
                builder.Session().RegisterInstance<ISessionRestartAware>(restartAware);
            },
            configureOptions: options => options.SessionRestartPreparationHooks = new[] { sharedHook });

        await pipeline.RunAsync(NoopSceneLoader.Instance);

        Assert.Equal(1, sharedHook.CallCount);
        Assert.Equal(0, restartAware.BeforeSessionRestartCalls);
    }

    private static RuntimePipeline CreateSessionReloadPipeline(
        Action<GameContextBuilder>? configureBuilder = null,
        Action<RuntimePipelineOptions>? configureOptions = null,
        int reloadCount = 1)
    {
        if (reloadCount < 1) throw new ArgumentOutOfRangeException(nameof(reloadCount));

        var pipeline = RuntimePipeline.Create(
            builder =>
            {
                builder.DefineSessionScope();
                configureBuilder?.Invoke(builder);
            },
            configureOptions);

        pipeline.ConfigureFlow(
            new DelegateRuntimeFlowScenario(
                async (context, cancellationToken) =>
                {
                    await context.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    for (var i = 0; i < reloadCount; i++)
                    {
                        await context.ReloadScopeAsync<SessionScope>(cancellationToken).ConfigureAwait(false);
                    }
                }));

        return pipeline;
    }

    private sealed class RecordingRestartAwareService : ISessionRestartAware
    {
        public int BeforeSessionRestartCalls { get; private set; }

        public void BeforeSessionRestart()
        {
            BeforeSessionRestartCalls++;
        }
    }

    private sealed class RecordingPreparationHook : IRuntimeSessionRestartPreparationHook
    {
        public int CallCount { get; private set; }

        public Task PrepareForSessionRestartAsync(
            RuntimeSessionRestartPreparationContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }
}
