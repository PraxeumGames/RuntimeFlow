using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.Tests
{
    public sealed class ScopeEntryPointsBeforeAsyncInitializationTests
    {
        [Test]
        public async Task GlobalScope_InitializesVContainerEntryPoints_BeforeGlobalAsyncServices()
        {
            var events = new List<string>();
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.Global().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterGlobalBootstrapPreset(containerBuilder);
                    containerBuilder.RegisterInstance(events);
                    containerBuilder.Register<GlobalEntryPoint>(Lifetime.Singleton)
                        .As<IInitializable>();
                    containerBuilder.Register<GlobalAsyncService>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                    containerBuilder.Register<GlobalStartable>(Lifetime.Singleton)
                        .As<IStartable>();
                });
                builder.DefineSessionScope();
            });

            await pipeline.InitializeAsync();

            Assert.That(events, Is.EqualTo(new[] { "global:init", "global:async", "global:start" }));
        }

        [Test]
        public async Task GlobalScope_RunsBootstrapOperations_AfterInitializablesBeforeAsyncServicesAndStartables()
        {
            var events = new List<string>();
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.Global().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterGlobalBootstrapPreset(containerBuilder);
                    containerBuilder.RegisterInstance(events);
                    containerBuilder.Register<GlobalEntryPoint>(Lifetime.Singleton)
                        .As<IInitializable>();
                    containerBuilder.Register<GlobalBootstrapOperation>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                    containerBuilder.Register<GlobalAsyncService>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                    containerBuilder.Register<GlobalStartable>(Lifetime.Singleton)
                        .As<IStartable>();
                });
                builder.DefineSessionScope();
            });

            await pipeline.InitializeAsync();

            Assert.That(events, Is.EqualTo(new[] { "global:init", "global:bootstrap", "global:async", "global:start" }));
        }

        [Test]
        public async Task GlobalScope_WhenBootstrapOperationFails_ReportsPhaseOperationAndStep()
        {
            var events = new List<string>();
            var recorder = new StartupOperationRecorder();
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.Global().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterGlobalBootstrapPreset(containerBuilder);
                    containerBuilder.RegisterInstance(events);
                    containerBuilder.Register<ThrowingGlobalBootstrapOperation>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                    containerBuilder.Register<GlobalAsyncService>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                    containerBuilder.Register<GlobalStartable>(Lifetime.Singleton)
                        .As<IStartable>();
                });
                builder.DefineSessionScope();
            });

            var ex = await AsyncTestAssert.ThrowsAsync<RuntimeStartupOperationException>(
                async () => await pipeline.InitializeAsync(recorder));

            Assert.That(ex.Phase, Is.EqualTo(RuntimeStartupOperationPhases.GlobalBootstrapOperations));
            Assert.That(ex.OperationName, Is.EqualTo(nameof(ThrowingGlobalBootstrapOperation)));
            Assert.That(ex.Step, Is.EqualTo("Explode"));
            Assert.That(ex.Detail, Is.EqualTo("with-detail"));
            Assert.That(events, Is.EqualTo(new[] { "global:bootstrap:throw" }));
            Assert.That(recorder.LastFailure, Does.Contain("phase=GlobalBootstrapOperations"));
            Assert.That(recorder.LastFailure, Does.Contain("operation=ThrowingGlobalBootstrapOperation"));
            Assert.That(recorder.LastFailure, Does.Contain("step=Explode"));
            Assert.That(recorder.LastFailure, Does.Contain("detail=with-detail"));
        }

        [Test]
        public async Task SessionScope_InitializesVContainerEntryPoints_BeforeSessionAsyncServices()
        {
            var events = new List<string>();
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.Global().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterGlobalBootstrapPreset(containerBuilder);
                });
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterSessionBootstrapPreset(containerBuilder);
                    containerBuilder.RegisterInstance(events);
                    containerBuilder.Register<SessionEntryPoint>(Lifetime.Singleton)
                        .As<IInitializable>();
                    containerBuilder.Register<SessionDualInitializableService>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                    containerBuilder.Register<SessionAsyncService>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                    containerBuilder.Register<SessionStartable>(Lifetime.Singleton)
                        .As<IStartable>();
                });
            });

            await pipeline.InitializeAsync();

            AssertOccursOnce(events, "session:init");
            AssertOccursOnce(events, "dual:init");
            AssertOccursOnce(events, "dual:async");
            AssertOccursOnce(events, "dual:start");
            AssertOccursOnce(events, "session:async");
            AssertOccursOnce(events, "session:start");
            AssertBefore(events, "session:init", "session:async");
            AssertBefore(events, "dual:init", "dual:async");
            AssertBefore(events, "session:init", "dual:async");
            AssertBefore(events, "dual:init", "session:async");
            AssertBefore(events, "session:async", "session:start");
            AssertBefore(events, "dual:async", "session:start");
            AssertBefore(events, "session:async", "dual:start");
            AssertBefore(events, "dual:async", "dual:start");
        }

        [Test]
        public async Task SessionScope_StartsVContainerStartables_OnMainThreadAfterAnyThreadAsyncServices()
        {
            var events = new List<string>();
            var scheduler = new RecordingExecutionScheduler();
            var pipeline = RuntimePipeline.Create(
                builder =>
                {
                    builder.Global().ConfigureContainer(containerBuilder =>
                    {
                        RuntimeFlowInstallerModules.RegisterGlobalBootstrapPreset(containerBuilder);
                    });
                    builder.Session().ConfigureContainer(containerBuilder =>
                    {
                        RuntimeFlowInstallerModules.RegisterSessionBootstrapPreset(containerBuilder);
                        containerBuilder.RegisterInstance(events);
                        containerBuilder.RegisterInstance(scheduler);
                        containerBuilder.Register<AnyThreadSessionAsyncService>(Lifetime.Singleton)
                            .AsImplementedInterfaces();
                        containerBuilder.Register<RecordingStartable>(Lifetime.Singleton)
                            .As<IStartable>();
                    });
                },
                options => options.ExecutionScheduler = scheduler);

            await pipeline.InitializeAsync();

            Assert.That(events, Does.Contain("async:AnyThread"));
            Assert.That(events, Does.Contain("start:MainThread"));
            AssertBefore(events, "async:AnyThread", "start:MainThread");
        }

        [Test]
        public async Task SessionScope_WhenGlobalUsesSameLifecycleServiceType_StillRunsSessionInitializer()
        {
            var events = new List<string>();
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.Global().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterGlobalBootstrapPreset(containerBuilder);
                    containerBuilder.RegisterInstance(events);
                    containerBuilder.Register<GlobalSharedLifecycleService>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                });
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterSessionBootstrapPreset(containerBuilder);
                    containerBuilder.Register<SessionSharedLifecycleService>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                });
            });

            await pipeline.InitializeAsync();

            Assert.That(events, Is.EqualTo(new[] { "shared:global", "shared:session" }));
        }

        [Test]
        public async Task GlobalBootstrapOperation_RunsThroughExecutionScheduler_OnMainThreadByDefault()
        {
            var events = new List<string>();
            var scheduler = new RecordingExecutionScheduler();
            var pipeline = RuntimePipeline.Create(
                builder =>
                {
                    builder.Global().ConfigureContainer(containerBuilder =>
                    {
                        RuntimeFlowInstallerModules.RegisterGlobalBootstrapPreset(containerBuilder);
                        containerBuilder.RegisterInstance(events);
                        containerBuilder.RegisterInstance(scheduler);
                        containerBuilder.Register<SchedulerRecordingGlobalBootstrapOperation>(Lifetime.Singleton)
                            .AsImplementedInterfaces();
                    });
                    builder.DefineSessionScope();
                },
                options => options.ExecutionScheduler = scheduler);

            await pipeline.InitializeAsync();

            Assert.That(events, Does.Contain("bootstrap:MainThread"));
        }

        [Test]
        public async Task GlobalBootstrapOperation_WhenCanceled_PreservesCanceledSemanticsAndDiagnostics()
        {
            var operation = new BlockingGlobalBootstrapOperation();
            var observer = new CollectingRuntimeLoadingProgressObserver();
            var recorder = new StartupOperationRecorder();
            var scheduler = new RecordingExecutionScheduler();
            var pipeline = RuntimePipeline.Create(
                builder =>
                {
                    builder.Global().ConfigureContainer(containerBuilder =>
                    {
                        RuntimeFlowInstallerModules.RegisterGlobalBootstrapPreset(containerBuilder);
                        containerBuilder.RegisterInstance(operation)
                            .AsImplementedInterfaces();
                    });
                    builder.DefineSessionScope();
                },
                options =>
                {
                    options.LoadingProgressObserver = observer;
                    options.ExecutionScheduler = scheduler;
                });

            using var cancellationSource = new CancellationTokenSource();
            var initialize = pipeline.InitializeAsync(recorder, cancellationSource.Token);
            await operation.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

            cancellationSource.Cancel();
            var ex = await AsyncTestAssert.CatchAsync<OperationCanceledException>(() => initialize);

            Assert.That(ex, Is.AssignableTo<OperationCanceledException>());
            Assert.That(recorder.LastFailure, Does.Contain("phase=GlobalBootstrapOperations"));
            Assert.That(recorder.LastFailure, Does.Contain("operation=BlockingGlobalBootstrapOperation"));
            Assert.That(recorder.LastFailure, Does.Contain("step=Waiting"));
            Assert.That(recorder.LastFailure, Does.Contain("detail=blocked"));
            Assert.That(recorder.LastFailure, Does.Contain("exception=RuntimeStartupOperationCanceledException"));
            Assert.That(observer.Snapshots.Any(snapshot =>
                    snapshot.Stage == RuntimeLoadingOperationStage.Canceled
                    && snapshot.State == RuntimeLoadingOperationState.Canceled),
                Is.True);
        }

        [Test]
        public async Task SessionScope_DoesNotRediscoverParentExplicitAsyncService()
        {
            var events = new List<string>();
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.Global().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterGlobalBootstrapPreset(containerBuilder);
                    containerBuilder.RegisterInstance(events);
                    containerBuilder.Register<GlobalOnlyExplicitLifecycleService>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                });
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterSessionBootstrapPreset(containerBuilder);
                    containerBuilder.Register<SessionAsyncService>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                });
            });

            await pipeline.InitializeAsync();
            await pipeline.RestartSessionAsync();

            Assert.That(events.Count(item => item == "explicit-global-only:global"), Is.EqualTo(1));
            Assert.That(events.Count(item => item == "session:async"), Is.EqualTo(2));
        }

        [Test]
        public async Task SessionScope_LocalSameKeyDependencyWinsOverParentInitializedState()
        {
            var events = new List<string>();
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.Global().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterGlobalBootstrapPreset(containerBuilder);
                    containerBuilder.RegisterInstance(events);
                    containerBuilder.Register<GlobalSharedExplicitLifecycleService>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                });
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterSessionBootstrapPreset(containerBuilder);
                    containerBuilder.Register<ZSessionSharedExplicitLifecycleService>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                    containerBuilder.Register<ASessionDependentOnSharedExplicitLifecycleService>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                });
            });

            await pipeline.InitializeAsync();

            AssertBefore(events, "shared-explicit:session", "shared-explicit:dependent");
        }

        [Test]
        public async Task SessionScope_MarkerOnlyAsyncServiceWithOrdinaryInterface_DoesNotUseOrdinaryInterfaceAsInitializerKey()
        {
            var events = new List<string>();
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.Global().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterGlobalBootstrapPreset(containerBuilder);
                });
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterSessionBootstrapPreset(containerBuilder);
                    containerBuilder.RegisterInstance(events);
                    containerBuilder.Register<MarkerOnlyAsyncResetParticipant>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                    containerBuilder.Register<OrdinaryResetOnlyService>(Lifetime.Singleton)
                        .As<IOrdinaryResetParticipant>();
                });
            });

            await pipeline.InitializeAsync();

            Assert.That(events, Is.EqualTo(new[] { "marker-only:init" }));
        }

        [Test]
        public async Task SessionRestart_KeepsGlobalScopeActiveAndDoesNotRerunGlobalLifecycle()
        {
            var events = new List<string>();
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.Global().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterGlobalBootstrapPreset(containerBuilder);
                    containerBuilder.RegisterInstance(events);
                    containerBuilder.Register<GlobalAsyncService>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                });
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterSessionBootstrapPreset(containerBuilder);
                    containerBuilder.Register<SessionAsyncService>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                });
            });

            await pipeline.InitializeAsync();
            await pipeline.RestartSessionAsync();

            Assert.That(pipeline.GetScopeState<GlobalScope>(), Is.EqualTo(ScopeLifecycleState.Active));
            Assert.That(pipeline.GetScopeState<SessionScope>(), Is.EqualTo(ScopeLifecycleState.Active));
            Assert.That(events.Count(item => item == "global:async"), Is.EqualTo(1));
            Assert.That(events.Count(item => item == "session:async"), Is.EqualTo(2));
        }

        [Test]
        public async Task SessionScope_WhenAsyncGraphIsEmpty_RunsInitializableBeforeStartable()
        {
            var events = new List<string>();
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.Global().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterGlobalBootstrapPreset(containerBuilder);
                });
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterSessionBootstrapPreset(containerBuilder);
                    containerBuilder.RegisterInstance(events);
                    containerBuilder.Register<SessionEntryPoint>(Lifetime.Singleton)
                        .As<IInitializable>();
                    containerBuilder.Register<SessionStartable>(Lifetime.Singleton)
                        .As<IStartable>();
                });
            });

            await pipeline.InitializeAsync();

            Assert.That(events, Is.EqualTo(new[] { "session:init", "session:start" }));
        }

        [Test]
        public async Task SessionScope_WhenStartableThrows_DisposesInitializedAsyncServices()
        {
            var events = new List<string>();
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.Global().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterGlobalBootstrapPreset(containerBuilder);
                });
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterSessionBootstrapPreset(containerBuilder);
                    containerBuilder.RegisterInstance(events);
                    containerBuilder.Register<DisposableSessionAsyncService>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                    containerBuilder.Register<ThrowingStartable>(Lifetime.Singleton)
                        .As<IStartable>();
                });
            });

            var ex = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.InitializeAsync());

            Assert.That(ex.Message, Is.EqualTo("start failed"));
            Assert.That(events, Does.Contain("async:init"));
            Assert.That(events, Does.Contain("start:throw"));
            Assert.That(events, Does.Contain("async:dispose"));
            AssertBefore(events, "async:init", "start:throw");
            AssertBefore(events, "start:throw", "async:dispose");
        }

        [Test]
        public async Task SessionScope_RunsEntryPointsBeforeAsyncServicesWithoutDependsOnBridge()
        {
            var events = new List<string>();
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineGlobalScope();
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterSessionBootstrapPreset(containerBuilder);
                    containerBuilder.RegisterInstance(events);
                    containerBuilder.Register<SessionEntryPoint>(Lifetime.Singleton)
                        .As<IInitializable>();
                    containerBuilder.Register<SessionServiceWithoutEntryPointDependency>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                });
            });

            await pipeline.InitializeAsync();

            Assert.That(events, Is.EqualTo(new[] { "session:init", "phase-dependent:async" }));
        }

        private static void AssertBefore(IReadOnlyList<string> events, string expectedEarlier, string expectedLater)
        {
            var earlierIndex = IndexOf(events, expectedEarlier);
            var laterIndex = IndexOf(events, expectedLater);

            Assert.That(earlierIndex, Is.GreaterThanOrEqualTo(0), $"Missing event '{expectedEarlier}'. Events: {string.Join(", ", events)}");
            Assert.That(laterIndex, Is.GreaterThanOrEqualTo(0), $"Missing event '{expectedLater}'. Events: {string.Join(", ", events)}");
            Assert.That(
                earlierIndex < laterIndex,
                Is.True,
                $"Expected '{expectedEarlier}' before '{expectedLater}'. Events: {string.Join(", ", events)}");
        }

        private static int IndexOf(IReadOnlyList<string> events, string value)
        {
            for (var i = 0; i < events.Count; i++)
            {
                if (events[i] == value)
                {
                    return i;
                }
            }

            return -1;
        }

        private static void AssertOccursOnce(IReadOnlyCollection<string> events, string value)
        {
            Assert.That(
                events.Count(item => item == value),
                Is.EqualTo(1),
                $"Expected '{value}' exactly once. Events: {string.Join(", ", events)}");
        }

        private sealed class GlobalEntryPoint : IInitializable
        {
            private readonly List<string> _events;

            public GlobalEntryPoint(List<string> events)
            {
                _events = events;
            }

            public void Initialize()
            {
                _events.Add("global:init");
            }
        }

        private sealed class GlobalAsyncService : IGlobalInitializableService
        {
            private readonly List<string> _events;

            public GlobalAsyncService(List<string> events)
            {
                _events = events;
            }

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _events.Add("global:async");
                return Task.CompletedTask;
            }
        }

        private sealed class GlobalBootstrapOperation : IGlobalBootstrapOperation
        {
            private readonly List<string> _events;

            public GlobalBootstrapOperation(List<string> events)
            {
                _events = events;
            }

            public string Name => nameof(GlobalBootstrapOperation);
            public int Order => 0;

            public Task ExecuteAsync(IStartupOperationContext context, CancellationToken cancellationToken)
            {
                context.ReportStep("Bootstrap");
                _events.Add("global:bootstrap");
                return Task.CompletedTask;
            }
        }

        private sealed class ThrowingGlobalBootstrapOperation : IGlobalBootstrapOperation
        {
            private readonly List<string> _events;

            public ThrowingGlobalBootstrapOperation(List<string> events)
            {
                _events = events;
            }

            public string Name => nameof(ThrowingGlobalBootstrapOperation);
            public int Order => 0;

            public Task ExecuteAsync(IStartupOperationContext context, CancellationToken cancellationToken)
            {
                context.ReportStep("Explode", "with-detail");
                _events.Add("global:bootstrap:throw");
                throw new InvalidOperationException("bootstrap failed");
            }
        }

        private sealed class BlockingGlobalBootstrapOperation : IGlobalBootstrapOperation
        {
            public TaskCompletionSource<bool> Started { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public string Name => nameof(BlockingGlobalBootstrapOperation);
            public int Order => 0;

            public async Task ExecuteAsync(IStartupOperationContext context, CancellationToken cancellationToken)
            {
                context.ReportStep("Waiting", "blocked");
                Started.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        private interface ISharedLifecycleService : IAsyncInitializableService
        {
        }

        private sealed class GlobalSharedLifecycleService : ISharedLifecycleService, IGlobalInitializableService
        {
            private readonly List<string> _events;

            public GlobalSharedLifecycleService(List<string> events)
            {
                _events = events;
            }

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _events.Add("shared:global");
                return Task.CompletedTask;
            }
        }

        private sealed class SessionSharedLifecycleService : ISharedLifecycleService, ISessionInitializableService
        {
            private readonly List<string> _events;

            public SessionSharedLifecycleService(List<string> events)
            {
                _events = events;
            }

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _events.Add("shared:session");
                return Task.CompletedTask;
            }
        }

        private interface IExplicitGlobalOnlyLifecycleService : IAsyncInitializableService
        {
        }

        private sealed class GlobalOnlyExplicitLifecycleService :
            IExplicitGlobalOnlyLifecycleService,
            IGlobalInitializableService
        {
            private readonly List<string> _events;

            public GlobalOnlyExplicitLifecycleService(List<string> events)
            {
                _events = events;
            }

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _events.Add("explicit-global-only:global");
                return Task.CompletedTask;
            }
        }

        private interface ISharedExplicitLifecycleService : IAsyncInitializableService
        {
        }

        private sealed class GlobalSharedExplicitLifecycleService :
            ISharedExplicitLifecycleService,
            IGlobalInitializableService
        {
            private readonly List<string> _events;

            public GlobalSharedExplicitLifecycleService(List<string> events)
            {
                _events = events;
            }

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _events.Add("shared-explicit:global");
                return Task.CompletedTask;
            }
        }

        private sealed class ZSessionSharedExplicitLifecycleService :
            ISharedExplicitLifecycleService,
            ISessionInitializableService
        {
            private readonly List<string> _events;

            public ZSessionSharedExplicitLifecycleService(List<string> events)
            {
                _events = events;
            }

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _events.Add("shared-explicit:session");
                return Task.CompletedTask;
            }
        }

        private sealed class ASessionDependentOnSharedExplicitLifecycleService : ISessionInitializableService
        {
            private readonly List<string> _events;

            public ASessionDependentOnSharedExplicitLifecycleService(
                List<string> events,
                ISharedExplicitLifecycleService dependency)
            {
                _events = events;
            }

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _events.Add("shared-explicit:dependent");
                return Task.CompletedTask;
            }
        }

        private interface IOrdinaryResetParticipant
        {
            void Reset();
        }

        private sealed class MarkerOnlyAsyncResetParticipant : ISessionInitializableService, IOrdinaryResetParticipant
        {
            private readonly List<string> _events;

            public MarkerOnlyAsyncResetParticipant(List<string> events)
            {
                _events = events;
            }

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _events.Add("marker-only:init");
                return Task.CompletedTask;
            }

            public void Reset()
            {
            }
        }

        private sealed class OrdinaryResetOnlyService : IOrdinaryResetParticipant
        {
            public void Reset()
            {
            }
        }

        private sealed class SchedulerRecordingGlobalBootstrapOperation : IGlobalBootstrapOperation
        {
            private readonly List<string> _events;
            private readonly RecordingExecutionScheduler _scheduler;

            public SchedulerRecordingGlobalBootstrapOperation(
                List<string> events,
                RecordingExecutionScheduler scheduler)
            {
                _events = events;
                _scheduler = scheduler;
            }

            public string Name => nameof(SchedulerRecordingGlobalBootstrapOperation);
            public int Order => 0;

            public Task ExecuteAsync(IStartupOperationContext context, CancellationToken cancellationToken)
            {
                _events.Add($"bootstrap:{_scheduler.CurrentAffinity}");
                return Task.CompletedTask;
            }
        }

        private sealed class GlobalStartable : IStartable
        {
            private readonly List<string> _events;

            public GlobalStartable(List<string> events)
            {
                _events = events;
            }

            public void Start()
            {
                _events.Add("global:start");
            }
        }

        private sealed class SessionEntryPoint : IInitializable
        {
            private readonly List<string> _events;

            public SessionEntryPoint(List<string> events)
            {
                _events = events;
            }

            public void Initialize()
            {
                _events.Add("session:init");
            }
        }

        private sealed class SessionDualInitializableService : ISessionInitializableService, IInitializable, IStartable
        {
            private readonly List<string> _events;

            public SessionDualInitializableService(List<string> events)
            {
                _events = events;
            }

            public void Initialize()
            {
                _events.Add("dual:init");
            }

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _events.Add("dual:async");
                return Task.CompletedTask;
            }

            public void Start()
            {
                _events.Add("dual:start");
            }
        }

        private sealed class SessionAsyncService : ISessionInitializableService
        {
            private readonly List<string> _events;

            public SessionAsyncService(List<string> events)
            {
                _events = events;
            }

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _events.Add("session:async");
                return Task.CompletedTask;
            }
        }

        private sealed class SessionServiceWithoutEntryPointDependency : ISessionInitializableService
        {
            private readonly List<string> _events;

            public SessionServiceWithoutEntryPointDependency(List<string> events)
            {
                _events = events;
            }

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _events.Add("phase-dependent:async");
                return Task.CompletedTask;
            }
        }

        private sealed class SessionStartable : IStartable
        {
            private readonly List<string> _events;

            public SessionStartable(List<string> events)
            {
                _events = events;
            }

            public void Start()
            {
                _events.Add("session:start");
            }
        }

        private sealed class AnyThreadSessionAsyncService : ISessionInitializableService, IInitializationThreadAffinityProvider
        {
            private readonly List<string> _events;
            private readonly RecordingExecutionScheduler _scheduler;

            public AnyThreadSessionAsyncService(List<string> events, RecordingExecutionScheduler scheduler)
            {
                _events = events;
                _scheduler = scheduler;
            }

            public InitializationThreadAffinity ThreadAffinity => InitializationThreadAffinity.AnyThread;

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _events.Add($"async:{_scheduler.CurrentAffinity}");
                return Task.CompletedTask;
            }
        }

        private sealed class RecordingStartable : IStartable
        {
            private readonly List<string> _events;
            private readonly RecordingExecutionScheduler _scheduler;

            public RecordingStartable(List<string> events, RecordingExecutionScheduler scheduler)
            {
                _events = events;
                _scheduler = scheduler;
            }

            public void Start()
            {
                _events.Add($"start:{_scheduler.CurrentAffinity}");
            }
        }

        private sealed class DisposableSessionAsyncService : ISessionInitializableService, IAsyncDisposableService
        {
            private readonly List<string> _events;

            public DisposableSessionAsyncService(List<string> events)
            {
                _events = events;
            }

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _events.Add("async:init");
                return Task.CompletedTask;
            }

            public Task DisposeAsync(CancellationToken cancellationToken)
            {
                _events.Add("async:dispose");
                return Task.CompletedTask;
            }
        }

        private sealed class ThrowingStartable : IStartable
        {
            private readonly List<string> _events;

            public ThrowingStartable(List<string> events)
            {
                _events = events;
            }

            public void Start()
            {
                _events.Add("start:throw");
                throw new InvalidOperationException("start failed");
            }
        }

        private sealed class RecordingExecutionScheduler : IInitializationExecutionScheduler
        {
            private readonly AsyncLocal<InitializationThreadAffinity?> _currentAffinity = new();

            public InitializationThreadAffinity? CurrentAffinity => _currentAffinity.Value;

            public async Task ExecuteAsync(
                InitializationThreadAffinity affinity,
                Func<CancellationToken, Task> operation,
                CancellationToken cancellationToken)
            {
                var previous = _currentAffinity.Value;
                _currentAffinity.Value = affinity;
                try
                {
                    await operation(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _currentAffinity.Value = previous;
                }
            }
        }

        private sealed class StartupOperationRecorder : IInitializationProgressNotifier, IStartupOperationProgressNotifier
        {
            public string LastFailure { get; private set; } = string.Empty;

            public void OnScopeStarted(GameContextType scope, int totalServices) { }
            public void OnServiceStarted(GameContextType scope, Type serviceType, int completedServices, int totalServices) { }
            public void OnServiceCompleted(GameContextType scope, Type serviceType, int completedServices, int totalServices) { }
            public void OnScopeCompleted(GameContextType scope, int totalServices) { }
            public void OnServiceProgress(GameContextType scope, Type serviceType, float progress, string message, int completedServices, int totalServices) { }
            public Task OnGlobalContextReadyForSessionInitializationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public Task OnSessionRestartTeardownCompletedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public void OnStartupOperationStarted(GameContextType scope, string phase, string operationName, int completedOperations, int totalOperations, TimeSpan elapsed) { }
            public void OnStartupOperationStep(GameContextType scope, string phase, string operationName, string step, string detail, int completedOperations, int totalOperations, TimeSpan elapsed) { }
            public void OnStartupOperationCompleted(GameContextType scope, string phase, string operationName, int completedOperations, int totalOperations, TimeSpan elapsed) { }

            public void OnStartupOperationFailed(
                GameContextType scope,
                string phase,
                string operationName,
                string step,
                string detail,
                Exception exception,
                int completedOperations,
                int totalOperations,
                TimeSpan elapsed)
            {
                LastFailure = $"phase={phase} operation={operationName} step={step} detail={detail} exception={exception.GetType().Name}";
            }
        }
    }

}
