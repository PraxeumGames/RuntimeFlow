using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using SFS.Core.GameLoading;
using VContainer;

namespace RuntimeFlow.Tests
{
    public sealed class RuntimePipelineBootstrapHostTests
    {
        [Test]
        public async Task RunAsync_PublishesCurrentPipelineBeforeSessionInitializersRun()
        {
            var probe = new PipelinePublicationProbe();

            var result = await RuntimePipelineBootstrapHost.RunAsync(
                builder =>
                {
                    builder.Global().ConfigureContainer(containerBuilder =>
                    {
                        RuntimeFlowInstallerModules.RegisterGlobalInfrastructure(containerBuilder);
                    });
                    builder.Session().ConfigureContainer(containerBuilder =>
                    {
                        containerBuilder.RegisterInstance(probe);
                        containerBuilder.Register<PipelinePublicationInitializer>(Lifetime.Singleton)
                            .AsImplementedInterfaces();
                    });
                },
                new NoOpSceneLoader(),
                RuntimeFlowPresets.InitializeOnly());

            try
            {
                Assert.That(probe.InitializerSawCurrentPipeline, Is.True);
                Assert.That(probe.InitializerPipeline, Is.SameAs(result.Pipeline));
            }
            finally
            {
                await result.DisposeAsync();
            }
        }

        [Test]
        public async Task RunAsync_WhenStartupInitializerRequestsRestart_CompletesAfterRestartReplay()
        {
            var probe = new StartupRestartProbe();
            var restartDependencies = new NoopRestartDependencies();
            var loadingState = new RuntimePipelineStringStageStateProvider();

            var result = await RuntimePipelineBootstrapHost.RunAsync(
                builder =>
                {
                    builder.Global().ConfigureContainer(containerBuilder =>
                    {
                        RuntimeFlowInstallerModules.RegisterGlobalInfrastructure(containerBuilder);
                        containerBuilder.RegisterInstance(restartDependencies)
                            .AsImplementedInterfaces();
                        containerBuilder.RegisterInstance(loadingState)
                            .As<IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>>();
                        containerBuilder.Register<RuntimeFlowGameRestartHandler>(Lifetime.Singleton)
                            .AsImplementedInterfaces();
                    });
                    builder.Session().ConfigureContainer(containerBuilder =>
                    {
                        containerBuilder.RegisterInstance(probe);
                        containerBuilder.Register<StartupRestartingInitializer>(Lifetime.Singleton)
                            .AsImplementedInterfaces();
                    });
                },
                new NoOpSceneLoader(),
                RuntimeFlowPresets.RestartAwareSceneBootstrap(
                    new RestartAwareSceneBootstrapScenarioOptions
                    {
                        SceneName = "RuntimePipelineBootstrapHostTests",
                        LoadingState = loadingState,
                        ReplayReloadScopeType = typeof(SessionScope)
                    }),
                configureOptions: options => options.ReplayFlowOnSessionRestart = true);

            try
            {
                Assert.That(probe.RestartRequests, Is.EqualTo(1));
                Assert.That(probe.CanceledInitializers, Is.EqualTo(1));
                Assert.That(probe.CompletedInitializers, Is.EqualTo(1));
                Assert.That(result.Pipeline.GetRuntimeStatus().State, Is.EqualTo(RuntimeExecutionState.Ready));
            }
            finally
            {
                await result.DisposeAsync();
            }
        }

        [Test]
        public async Task RestartLifecycle_AllowBeforeReady_AllowsInitializingRuntime()
        {
            var restartCount = 0;
            var lifecycle = CreateLifecycleManager(
                RuntimeExecutionState.Initializing,
                () => restartCount++);

            await lifecycle.RestartAsync(new RuntimeRestartRequest(allowBeforeReady: true));

            Assert.That(restartCount, Is.EqualTo(1));
        }

        [Test]
        public void RestartLifecycle_AllowBeforeReady_DoesNotAllowFailedRuntime()
        {
            var lifecycle = CreateLifecycleManager(
                RuntimeExecutionState.Failed,
                () => { });

            var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await lifecycle.RestartAsync(new RuntimeRestartRequest(allowBeforeReady: true)));

            Assert.That(exception!.Message, Does.Contain("Runtime is not ready"));
        }

        private static RuntimeRestartLifecycleManager CreateLifecycleManager(
            RuntimeExecutionState state,
            Action restartAction)
        {
            return new RuntimeRestartLifecycleManager(
                restartOperation: (_, _) =>
                {
                    restartAction();
                    return Task.CompletedTask;
                },
                readinessGate: new RuntimeReadinessGate(
                    runtimeReadinessProvider: () => new RuntimeReadinessStatus(
                        isReady: false,
                        updatedAtUtc: DateTimeOffset.UtcNow,
                        blockingReasonCode: "runtime.not_ready",
                        blockingReason: "Runtime is not ready."),
                    executionContextProvider: () => CreateExecutionContext(state)),
                executionContextProvider: new FixedExecutionContextProvider(state),
                pipelineStateQuery: new FixedPipelineStateQuery(state));
        }

        private static RuntimeExecutionContextSnapshot CreateExecutionContext(RuntimeExecutionState state)
        {
            return new RuntimeExecutionContextSnapshot(
                RuntimeExecutionPhase.Flow,
                isReplay: false,
                state,
                DateTimeOffset.UtcNow,
                currentOperationCode: RuntimeOperationCodes.RunFlow);
        }

        private sealed class PipelinePublicationProbe
        {
            public bool InitializerSawCurrentPipeline { get; set; }
            public RuntimePipeline InitializerPipeline { get; set; }
        }

        private sealed class StartupRestartProbe
        {
            public int RestartRequests;
            public int CanceledInitializers;
            public int CompletedInitializers;
        }

        private sealed class PipelinePublicationInitializer : ISessionInitializableService
        {
            private readonly IRuntimeFlowPipelineProvider _pipelineProvider;
            private readonly PipelinePublicationProbe _probe;

            public PipelinePublicationInitializer(
                IRuntimeFlowPipelineProvider pipelineProvider,
                PipelinePublicationProbe probe)
            {
                _pipelineProvider = pipelineProvider;
                _probe = probe;
            }

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _probe.InitializerSawCurrentPipeline = _pipelineProvider.TryGetCurrent(out var pipeline);
                _probe.InitializerPipeline = pipeline;
                return Task.CompletedTask;
            }
        }

        private sealed class StartupRestartingInitializer : ISessionInitializableService
        {
            private readonly IGameRestartHandler _restartHandler;
            private readonly StartupRestartProbe _probe;

            public StartupRestartingInitializer(
                IGameRestartHandler restartHandler,
                StartupRestartProbe probe)
            {
                _restartHandler = restartHandler;
                _probe = probe;
            }

            public async Task InitializeAsync(CancellationToken cancellationToken)
            {
                if (Interlocked.CompareExchange(ref _probe.RestartRequests, 1, 0) == 0)
                {
                    _restartHandler.Restart("startup-restart-test", forceSave: false);
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        Interlocked.Increment(ref _probe.CanceledInitializers);
                        throw;
                    }
                }

                Interlocked.Increment(ref _probe.CompletedInitializers);
            }
        }

        private sealed class NoopRestartDependencies : IGameRestartStateSaver, IGameDataCleaner
        {
            public void SaveAppState()
            {
            }

            public void ClearSecondaryUserData()
            {
            }

            public void ClearAllUserData()
            {
            }
        }

        private sealed class FixedExecutionContextProvider : IRuntimeExecutionContextProvider
        {
            private readonly RuntimeExecutionState _state;

            public FixedExecutionContextProvider(RuntimeExecutionState state)
            {
                _state = state;
            }

            public IRuntimeExecutionContext GetExecutionContext() => CreateExecutionContext(_state);
        }

        private sealed class FixedPipelineStateQuery : IRuntimePipelineStateQuery
        {
            private readonly RuntimeExecutionState _state;

            public FixedPipelineStateQuery(RuntimeExecutionState state)
            {
                _state = state;
            }

            public RuntimeStatus GetRuntimeStatus()
            {
                return new RuntimeStatus(
                    _state,
                    DateTimeOffset.UtcNow,
                    currentOperationCode: RuntimeOperationCodes.RunFlow,
                    message: "test");
            }

            public RuntimeReadinessStatus GetReadinessStatus()
            {
                return new RuntimeReadinessStatus(
                    isReady: false,
                    updatedAtUtc: DateTimeOffset.UtcNow,
                    currentOperationCode: RuntimeOperationCodes.RunFlow,
                    blockingReasonCode: "runtime.not_ready",
                    blockingReason: "Runtime is not ready.");
            }
        }
    }
}
