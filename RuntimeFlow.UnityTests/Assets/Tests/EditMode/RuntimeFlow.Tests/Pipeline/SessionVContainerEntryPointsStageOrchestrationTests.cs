using NUnit.Framework;
using System;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.Tests
{
    public sealed partial class SessionVContainerEntryPointsStageOrchestrationTests
    {
        [Test]
        public async Task SessionVContainerEntryPoints_ExecutesStagedInitializablesInCanonicalOrder_ThenNonStagedFallback()
        {
            var events = await InitializeAndGetRecordedEventsAsync(
                CreateResolver(
                    registerServices: builder =>
                    {
                        builder.Register<PreBootstrapStageInitializable>(Lifetime.Singleton)
                            .As<IPreBootstrapTag>()
                            .As<IInitializable>()
                            .As<IPreBootstrapStartupInitializableService>();
                        builder.Register<PlatformStageInitializable>(Lifetime.Singleton)
                            .As<IPlatformTag>()
                            .As<IInitializable>()
                            .As<IPlatformStartupInitializableService>();
                        builder.Register<ContentStageInitializable>(Lifetime.Singleton)
                            .As<IContentTag>()
                            .As<IInitializable>()
                            .As<IContentStartupInitializableService>();
                        builder.Register<SessionStageInitializable>(Lifetime.Singleton)
                            .As<ISessionTag>()
                            .As<IInitializable>()
                            .As<ISessionStartupInitializableService>();
                        builder.Register<UiStageInitializable>(Lifetime.Singleton)
                            .As<IUiTag>()
                            .As<IInitializable>()
                            .As<IUiStartupInitializableService>();
                        builder.Register<NonStagedInitializable>(Lifetime.Singleton)
                            .As<INonStagedTag>()
                            .As<IInitializable>();
                    },
                    getInitializableRegistrations: root => new[]
                    {
                        GetRegistration<IUiTag>(root),
                        GetRegistration<INonStagedTag>(root),
                        GetRegistration<ISessionTag>(root),
                        GetRegistration<IContentTag>(root),
                        GetRegistration<IPreBootstrapTag>(root),
                        GetRegistration<IPlatformTag>(root)
                    }),
                CreateUnmanagedEntryPointSettings());

            Assert.That(
                events,
                Is.EqualTo(new[] { "prebootstrap", "platform", "content", "session", "ui", "nonstaged" }));
        }

        [Test]
        public async Task SessionVContainerEntryPoints_DeduplicatesByImplementationType_InSinglePass()
        {
            var events = await InitializeAndGetRecordedEventsAsync(
                CreateResolver(
                    registerServices: builder =>
                    {
                        builder.Register<DuplicateSessionStageInitializable>(Lifetime.Transient)
                            .As<IDuplicateTagA>()
                            .As<IInitializable>()
                            .As<ISessionStartupInitializableService>();
                        builder.Register<DuplicateSessionStageInitializable>(Lifetime.Transient)
                            .As<IDuplicateTagB>()
                            .As<IInitializable>()
                            .As<ISessionStartupInitializableService>();
                    },
                    getInitializableRegistrations: root => new[]
                    {
                        GetRegistration<IDuplicateTagA>(root),
                        GetRegistration<IDuplicateTagB>(root)
                    }),
                CreateUnmanagedEntryPointSettings());

            Assert.That(events, Is.EqualTo(new[] { "duplicate" }));
        }

        [Test]
        public async Task SessionVContainerEntryPoints_InitializesManagedDualInitializable_InInitializablePass()
        {
            var events = await InitializeAndGetRecordedEventsAsync(
                CreateResolver(
                    registerServices: builder =>
                    {
                        builder.Register<ManagedDualInitializable>(Lifetime.Singleton)
                            .As<IManagedDualTag>()
                            .As<IInitializable>()
                            .As<ISessionInitializableService>();
                        builder.Register<PlainInitializable>(Lifetime.Singleton)
                            .As<IPlainTag>()
                            .As<IInitializable>();
                    },
                    getInitializableRegistrations: root => new[]
                    {
                        GetRegistration<IManagedDualTag>(root),
                        GetRegistration<IPlainTag>(root)
                    }),
                CreateUnmanagedEntryPointSettings());

            Assert.That(events, Is.EqualTo(new[] { "managed-dual", "plain" }));
        }
    }

}
