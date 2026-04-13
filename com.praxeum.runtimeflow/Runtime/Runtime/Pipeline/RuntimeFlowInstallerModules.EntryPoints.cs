using System;
using VContainer;

namespace RuntimeFlow.Contexts
{
    public static partial class RuntimeFlowInstallerModules
    {
        public static void RegisterSessionSyncEntryPointsBootstrap(
            IContainerBuilder builder,
            RuntimeFlowSessionSyncEntryPointsBootstrapOptions? options = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));

            var settings = (options ?? new RuntimeFlowSessionSyncEntryPointsBootstrapOptions()).BuildSettings();
            RegisterEntryPointsInitializationService<RuntimeFlowSessionSyncEntryPointsInitializationService>(builder, settings);
        }

        public static void RegisterGlobalVContainerEntryPoints(
            IContainerBuilder builder,
            RuntimeFlowVContainerEntryPointsInstallerOptions? options = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));

            var settings = (options ?? new RuntimeFlowVContainerEntryPointsInstallerOptions()).BuildSettings();
            RegisterEntryPointsInitializationService<RuntimeFlowGlobalVContainerEntryPointsInitializationService>(builder, settings);
        }

        private static void RegisterEntryPointsInitializationService<TService>(
            IContainerBuilder builder,
            RuntimeFlowVContainerEntryPointsSettings settings)
            where TService : class
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            builder.RegisterInstance(settings);
            builder.Register<TService>(Lifetime.Singleton)
                .AsSelf()
                .AsImplementedInterfaces();
        }
    }
}
