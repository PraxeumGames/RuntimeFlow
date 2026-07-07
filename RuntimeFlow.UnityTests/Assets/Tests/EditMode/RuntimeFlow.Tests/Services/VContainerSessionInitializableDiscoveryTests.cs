using NUnit.Framework;
using System;
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
    }
}
