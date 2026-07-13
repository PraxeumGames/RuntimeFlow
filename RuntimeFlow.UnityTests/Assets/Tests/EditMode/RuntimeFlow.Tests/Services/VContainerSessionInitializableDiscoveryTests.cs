using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.Tests
{
    public sealed class VContainerSessionInitializableDiscoveryTests
    {
        [Test]
        public async Task SessionInitializableRegisteredAsImplementedInterfaces_IsInitializedWithoutAsSelf()
        {
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    containerBuilder.Register<AchievementLikeService>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                });
            });

            await pipeline.InitializeAsync();

            var service = pipeline.SessionContext.Resolve<IAchievementLikeService>();
            Assert.That(service.InitializeCount, Is.EqualTo(1));

            await pipeline.DisposeAsync();

            Assert.That(service.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public async Task MultipleMarkerOnlySessionInitializablesRegisteredAsImplementedInterfaces_AreAllInitialized()
        {
            MarkerOnlySessionServiceA.InitializeCount = 0;
            MarkerOnlySessionServiceB.InitializeCount = 0;

            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    containerBuilder.Register<MarkerOnlySessionServiceA>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                    containerBuilder.Register<MarkerOnlySessionServiceB>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                });
            });

            await pipeline.InitializeAsync();

            Assert.That(MarkerOnlySessionServiceA.InitializeCount, Is.EqualTo(1));
            Assert.That(MarkerOnlySessionServiceB.InitializeCount, Is.EqualTo(1));
        }

        [Test]
        public async Task MultipleVContainerInitializablesRegisteredAsImplementedInterfaces_DoNotShareRuntimeFlowInitializerKey()
        {
            VContainerEntryPointSessionServiceA.AsyncInitializeCount = 0;
            VContainerEntryPointSessionServiceA.VContainerInitializeCount = 0;
            VContainerEntryPointSessionServiceB.AsyncInitializeCount = 0;
            VContainerEntryPointSessionServiceB.VContainerInitializeCount = 0;

            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterSessionBootstrapPreset(containerBuilder);
                    containerBuilder.Register<VContainerEntryPointSessionServiceA>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                    containerBuilder.Register<VContainerEntryPointSessionServiceB>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                });
            });

            await pipeline.InitializeAsync();

            Assert.That(VContainerEntryPointSessionServiceA.AsyncInitializeCount, Is.EqualTo(1));
            Assert.That(VContainerEntryPointSessionServiceB.AsyncInitializeCount, Is.EqualTo(1));
            Assert.That(VContainerEntryPointSessionServiceA.VContainerInitializeCount, Is.EqualTo(1));
            Assert.That(VContainerEntryPointSessionServiceB.VContainerInitializeCount, Is.EqualTo(1));
        }

        [Test]
        public async Task VContainerInitializableRegisteredOnlyAsInheritedEntryPointInterface_IsInitialized()
        {
            IndirectVContainerInitializableService.InitializeCount = 0;

            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterSessionBootstrapPreset(containerBuilder);
                    containerBuilder.Register<IndirectVContainerInitializableService>(Lifetime.Singleton)
                        .As<IIndirectVContainerInitializableService>();
                });
            });

            await pipeline.InitializeAsync();

            Assert.That(IndirectVContainerInitializableService.InitializeCount, Is.EqualTo(1));
        }

        [Test]
        public async Task VContainerStartableRegisteredOnlyAsInheritedEntryPointInterface_IsStarted()
        {
            IndirectVContainerStartableService.StartCount = 0;

            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterSessionBootstrapPreset(containerBuilder);
                    containerBuilder.Register<IndirectVContainerStartableService>(Lifetime.Singleton)
                        .As<IIndirectVContainerStartableService>();
                });
            });

            await pipeline.InitializeAsync();

            Assert.That(IndirectVContainerStartableService.StartCount, Is.EqualTo(1));
        }

        [Test]
        public async Task EntryPointSettingsContribution_ExcludesProductionStartableWhileServiceOverrideWinsDirectResolve()
        {
            ProductionCdnLikeStartable.StartCount = 0;

            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterSessionBootstrapPreset(containerBuilder);
                    containerBuilder.Register<ProductionCdnLikeStartable>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                    containerBuilder.RegisterInstance(new RuntimeFlowVContainerEntryPointsSettingsContribution(
                            Array.Empty<Type>(),
                            new[] { typeof(ProductionCdnLikeStartable) }))
                        .AsSelf();
                    containerBuilder.Register<TestCdnLikeSelector>(Lifetime.Singleton)
                        .As<ICdnLikeSelector>();
                });
            });

            await pipeline.InitializeAsync();

            Assert.That(ProductionCdnLikeStartable.StartCount, Is.EqualTo(0));
            Assert.That(pipeline.SessionContext.Resolve<ICdnLikeSelector>(), Is.TypeOf<TestCdnLikeSelector>());
        }

        [Test]
        public async Task AdditionalEntryPointSettings_AreMergedWithPresetSettings()
        {
            PrioritizedMergeInitializable.InitializeCount = 0;
            ExcludedMergeInitializable.InitializeCount = 0;
            RegularMergeInitializable.InitializeCount = 0;

            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    var preset = RuntimeFlowInstallerModules.CreateSessionBootstrapPreset()
                        .ConfigureSessionVContainerEntryPoints(options =>
                            options.AddPrioritizedInitializable<PrioritizedMergeInitializable>());
                    RuntimeFlowInstallerModules.RegisterSessionBootstrapPreset(containerBuilder, preset);
                    containerBuilder.RegisterInstance(new RuntimeFlowVContainerEntryPointsSettingsContribution(
                            new[] { typeof(ExcludedMergeInitializable) }))
                        .AsSelf();
                    containerBuilder.Register<PrioritizedMergeInitializable>(Lifetime.Singleton)
                        .As<IInitializable>();
                    containerBuilder.Register<ExcludedMergeInitializable>(Lifetime.Singleton)
                        .As<IInitializable>();
                    containerBuilder.Register<RegularMergeInitializable>(Lifetime.Singleton)
                        .As<IInitializable>();
                });
            });

            await pipeline.InitializeAsync();

            Assert.That(PrioritizedMergeInitializable.InitializeCount, Is.EqualTo(1));
            Assert.That(ExcludedMergeInitializable.InitializeCount, Is.EqualTo(0));
            Assert.That(RegularMergeInitializable.InitializeCount, Is.EqualTo(1));
        }

        [Test]
        public async Task EntryPointSettingsContribution_ExcludesProductionEntryPointWhileServiceOverrideWinsDirectResolve()
        {
            ProductionLobbyLikeRunner.InitializeCount = 0;

            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterSessionBootstrapPreset(containerBuilder);
                    containerBuilder.Register<ProductionLobbyLikeRunner>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                    containerBuilder.RegisterInstance(new RuntimeFlowVContainerEntryPointsSettingsContribution(
                            new[] { typeof(ProductionLobbyLikeRunner) }))
                        .AsSelf();
                    containerBuilder.Register<TestLobbyLikeRunner>(Lifetime.Singleton)
                        .As<ILobbyLikeRunner>();
                });
            });

            await pipeline.InitializeAsync();

            Assert.That(ProductionLobbyLikeRunner.InitializeCount, Is.EqualTo(0));
            Assert.That(pipeline.SessionContext.Resolve<ILobbyLikeRunner>(), Is.TypeOf<TestLobbyLikeRunner>());
        }

        [Test]
        public async Task SessionInitializableDiscovery_ResolvesThroughOverriddenServiceInterface()
        {
            ProductionOverriddenSessionService.InitializeCount = 0;
            ReplacementOverriddenSessionService.InitializeCount = 0;

            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    containerBuilder.Register<ProductionOverriddenSessionService>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                    containerBuilder.Register<ReplacementOverriddenSessionService>(Lifetime.Singleton)
                        .As<IOverriddenSessionService>();
                });
            });

            await pipeline.InitializeAsync();

            Assert.That(ProductionOverriddenSessionService.InitializeCount, Is.EqualTo(0));
            Assert.That(ReplacementOverriddenSessionService.InitializeCount, Is.EqualTo(1));
        }

        [Test]
        public async Task SessionSelfRegistration_WhenTerminalCall_IsRegisteredAndInitialized()
        {
            SelfRegisteredSessionService.InitializeCount = 0;

            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().Register<SelfRegisteredSessionService>(Lifetime.Singleton);
            });

            await pipeline.InitializeAsync();

            Assert.That(SelfRegisteredSessionService.InitializeCount, Is.EqualTo(1));
            Assert.That(pipeline.SessionContext.Resolve<SelfRegisteredSessionService>(), Is.Not.Null);
        }

        [Test]
        public async Task SessionSelfRegistrationByType_WhenTerminalCall_IsRegisteredAndInitialized()
        {
            SelfRegisteredByTypeSessionService.InitializeCount = 0;

            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().Register(typeof(SelfRegisteredByTypeSessionService), Lifetime.Singleton);
            });

            await pipeline.InitializeAsync();

            Assert.That(SelfRegisteredByTypeSessionService.InitializeCount, Is.EqualTo(1));
            Assert.That(pipeline.SessionContext.Resolve<SelfRegisteredByTypeSessionService>(), Is.Not.Null);
        }

        [Test]
        public async Task SessionInitializableDiscovery_MergesRuntimeFlowAndVContainerRegistrationSources()
        {
            FluentMixedSessionService.InitializeCount = 0;
            VContainerMixedSessionService.InitializeCount = 0;

            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().Register<FluentMixedSessionService>(Lifetime.Singleton);
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    containerBuilder.Register<VContainerMixedSessionService>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                });
            });

            await pipeline.InitializeAsync();

            Assert.That(FluentMixedSessionService.InitializeCount, Is.EqualTo(1));
            Assert.That(VContainerMixedSessionService.InitializeCount, Is.EqualTo(1));
        }

        [Test]
        public async Task MarkerOnlySessionInitializable_WithSharedNonAsyncInterface_DoesNotUseNonAsyncInterfaceAsInitializerKey()
        {
            ResettableMarkerOnlySessionService.InitializeCount = 0;

            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    containerBuilder.Register<ResettableMarkerOnlySessionService>(Lifetime.Singleton)
                        .AsImplementedInterfaces();
                    containerBuilder.Register<PlainSessionResetService>(Lifetime.Singleton)
                        .As<INonAsyncSessionReset>();
                });
            });

            await pipeline.InitializeAsync();

            Assert.That(ResettableMarkerOnlySessionService.InitializeCount, Is.EqualTo(1));
        }

        [Test]
        public async Task MarkerOnlySessionInitializable_DependsOnConcreteMarkerOnlyRegistration_WaitsForDependency()
        {
            var events = new List<string>();
            var setupCanFinish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    containerBuilder.RegisterInstance(events);
                    containerBuilder.RegisterInstance(setupCanFinish);
                    containerBuilder.Register<LocalizationSetupLikeService>(Lifetime.Singleton)
                        .AsSelf()
                        .AsImplementedInterfaces();
                    containerBuilder.Register<NavigatorStartupLikeService>(Lifetime.Singleton)
                        .AsSelf()
                        .AsImplementedInterfaces();
                });
            });

            try
            {
                var initializeTask = pipeline.InitializeAsync();
                await WaitUntilAsync(() => events.Contains("setup:start"));
                await Task.Delay(50);

                Assert.That(
                    events,
                    Does.Not.Contain("navigator:start"),
                    "A concrete [DependsOn] edge must block the dependent service even when the dependency is marker-only.");

                setupCanFinish.SetResult(true);
                await initializeTask;

                Assert.That(events, Is.EqualTo(new[] { "setup:start", "setup:end", "navigator:start" }));
            }
            finally
            {
                setupCanFinish.TrySetResult(true);
                await pipeline.DisposeAsync();
            }
        }

        private interface IAchievementLikeService
        {
            int InitializeCount { get; }
            int DisposeCount { get; }
        }

        private sealed class AchievementLikeService : IAchievementLikeService, ISessionInitializableService, IDisposable
        {
            public int InitializeCount { get; private set; }
            public int DisposeCount { get; private set; }

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                InitializeCount++;
                return Task.CompletedTask;
            }

            public void Dispose()
            {
                DisposeCount++;
            }
        }

        private sealed class MarkerOnlySessionServiceA : ISessionInitializableService
        {
            public static int InitializeCount;

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                InitializeCount++;
                return Task.CompletedTask;
            }
        }

        private sealed class MarkerOnlySessionServiceB : ISessionInitializableService
        {
            public static int InitializeCount;

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                InitializeCount++;
                return Task.CompletedTask;
            }
        }

        private sealed class VContainerEntryPointSessionServiceA : ISessionInitializableService, IInitializable
        {
            public static int AsyncInitializeCount;
            public static int VContainerInitializeCount;

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                AsyncInitializeCount++;
                return Task.CompletedTask;
            }

            public void Initialize()
            {
                VContainerInitializeCount++;
            }
        }

        private sealed class VContainerEntryPointSessionServiceB : ISessionInitializableService, IInitializable
        {
            public static int AsyncInitializeCount;
            public static int VContainerInitializeCount;

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                AsyncInitializeCount++;
                return Task.CompletedTask;
            }

            public void Initialize()
            {
                VContainerInitializeCount++;
            }
        }

        private interface IIndirectVContainerInitializableService : IInitializable
        {
        }

        private sealed class IndirectVContainerInitializableService : IIndirectVContainerInitializableService
        {
            public static int InitializeCount;

            public void Initialize()
            {
                InitializeCount++;
            }
        }

        private interface IIndirectVContainerStartableService : IStartable
        {
        }

        private sealed class IndirectVContainerStartableService : IIndirectVContainerStartableService
        {
            public static int StartCount;

            public void Start()
            {
                StartCount++;
            }
        }

        private interface ICdnLikeSelector
        {
        }

        private sealed class ProductionCdnLikeStartable : ICdnLikeSelector, IStartable
        {
            public static int StartCount;

            public void Start()
            {
                StartCount++;
            }
        }

        private sealed class TestCdnLikeSelector : ICdnLikeSelector
        {
        }

        private sealed class PrioritizedMergeInitializable : IInitializable
        {
            public static int InitializeCount;

            public void Initialize()
            {
                InitializeCount++;
            }
        }

        private sealed class ExcludedMergeInitializable : IInitializable
        {
            public static int InitializeCount;

            public void Initialize()
            {
                InitializeCount++;
            }
        }

        private sealed class RegularMergeInitializable : IInitializable
        {
            public static int InitializeCount;

            public void Initialize()
            {
                InitializeCount++;
            }
        }

        private interface ILobbyLikeRunner
        {
        }

        private sealed class ProductionLobbyLikeRunner : ILobbyLikeRunner, IInitializable
        {
            public static int InitializeCount;

            public void Initialize()
            {
                InitializeCount++;
            }
        }

        private sealed class TestLobbyLikeRunner : ILobbyLikeRunner
        {
        }

        private interface IOverriddenSessionService : ISessionInitializableService
        {
        }

        private sealed class ProductionOverriddenSessionService : IOverriddenSessionService
        {
            public static int InitializeCount;

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                InitializeCount++;
                return Task.CompletedTask;
            }
        }

        private sealed class ReplacementOverriddenSessionService : IOverriddenSessionService
        {
            public static int InitializeCount;

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                InitializeCount++;
                return Task.CompletedTask;
            }
        }

        private sealed class SelfRegisteredSessionService : ISessionInitializableService
        {
            public static int InitializeCount;

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                InitializeCount++;
                return Task.CompletedTask;
            }
        }

        private sealed class SelfRegisteredByTypeSessionService : ISessionInitializableService
        {
            public static int InitializeCount;

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                InitializeCount++;
                return Task.CompletedTask;
            }
        }

        private sealed class FluentMixedSessionService : ISessionInitializableService
        {
            public static int InitializeCount;

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                InitializeCount++;
                return Task.CompletedTask;
            }
        }

        private interface IVContainerMixedSessionService : ISessionInitializableService
        {
        }

        private sealed class VContainerMixedSessionService : IVContainerMixedSessionService
        {
            public static int InitializeCount;

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                InitializeCount++;
                return Task.CompletedTask;
            }
        }

        private interface INonAsyncSessionReset
        {
            void Reset();
        }

        private sealed class ResettableMarkerOnlySessionService : ISessionInitializableService, INonAsyncSessionReset
        {
            public static int InitializeCount;

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                InitializeCount++;
                return Task.CompletedTask;
            }

            public void Reset()
            {
            }
        }

        private sealed class PlainSessionResetService : INonAsyncSessionReset
        {
            public void Reset()
            {
            }
        }

        private sealed class LocalizationSetupLikeService : ISessionInitializableService
        {
            private readonly List<string> _events;
            private readonly TaskCompletionSource<bool> _canFinish;

            public LocalizationSetupLikeService(
                List<string> events,
                TaskCompletionSource<bool> canFinish)
            {
                _events = events;
                _canFinish = canFinish;
            }

            public async Task InitializeAsync(CancellationToken cancellationToken)
            {
                _events.Add("setup:start");
                await _canFinish.Task;
                cancellationToken.ThrowIfCancellationRequested();
                _events.Add("setup:end");
            }
        }

        [DependsOn(typeof(LocalizationSetupLikeService))]
        private sealed class NavigatorStartupLikeService : ISessionInitializableService
        {
            private readonly List<string> _events;

            public NavigatorStartupLikeService(List<string> events)
            {
                _events = events;
            }

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _events.Add("navigator:start");
                return Task.CompletedTask;
            }
        }

        private static async Task WaitUntilAsync(Func<bool> predicate)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!predicate())
            {
                cts.Token.ThrowIfCancellationRequested();
                await Task.Delay(10, cts.Token);
            }
        }
    }
}
