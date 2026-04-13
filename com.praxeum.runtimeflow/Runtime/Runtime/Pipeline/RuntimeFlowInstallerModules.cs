using System;
using VContainer;

namespace RuntimeFlow.Contexts
{
    public static partial class RuntimeFlowInstallerModules
    {
        public static void RegisterGlobalBootstrapPreset(
            IContainerBuilder builder,
            RuntimeFlowGlobalBootstrapPresetOptions? options = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            options ??= new RuntimeFlowGlobalBootstrapPresetOptions();

            RegisterGlobalInfrastructure(builder, options.GlobalInfrastructure);
            if (options.RegisterGlobalVContainerEntryPoints)
            {
                RegisterGlobalVContainerEntryPoints(builder, options.GlobalVContainerEntryPoints);
            }
        }

        public static void RegisterSessionBootstrapPreset(
            IContainerBuilder builder,
            RuntimeFlowSessionBootstrapPresetOptions? options = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            options ??= new RuntimeFlowSessionBootstrapPresetOptions();
            RegisterSessionBootstrap(builder, options.BuildInstallerOptions());
        }

        public static RuntimeFlowSessionBootstrapPresetOptions CreateSessionBootstrapPreset()
        {
            return new RuntimeFlowSessionBootstrapPresetOptions();
        }

        public static void RegisterGlobalInfrastructure(
            IContainerBuilder builder,
            RuntimeFlowGlobalInstallerOptions? options = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            options ??= new RuntimeFlowGlobalInstallerOptions();

            ApplyLazySingletonConfiguration(builder, options.EnableLazySingletons, options.ConfigureLazySingletons);

            if (options.RegisterPipelineProvider)
            {
                builder.Register<RuntimeFlowPipelineProvider>(Lifetime.Singleton)
                    .AsImplementedInterfaces();
            }
        }

        public static void RegisterSessionInfrastructure(
            IContainerBuilder builder,
            RuntimeFlowSessionInstallerOptions? options = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            options ??= new RuntimeFlowSessionInstallerOptions();

            ApplyLazySingletonConfiguration(builder, options.EnableLazySingletons, options.ConfigureLazySingletons);

            if (options.RegisterBuildCallbackResolverWarmup)
            {
                if (options.BuildCallbackResolverAction != null)
                {
                    builder.RegisterBuildCallback(options.BuildCallbackResolverAction);
                }
                else if (options.BuildCallbackResolverType != null)
                {
                    builder.RegisterBuildCallback(resolver => resolver.Resolve(options.BuildCallbackResolverType));
                }
            }
        }

        public static void RegisterSessionBootstrap(
            IContainerBuilder builder,
            RuntimeFlowSessionBootstrapInstallerOptions? options = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            options ??= new RuntimeFlowSessionBootstrapInstallerOptions();

            RegisterSessionInfrastructure(builder, options.SessionInfrastructure);
            RegisterSessionSyncEntryPointsBootstrap(builder, options.SessionSyncEntryPointsBootstrap);
            RegisterLoadingAndRestartInfrastructure(builder, options.LoadingAndRestartInfrastructure);
        }

        private static void ApplyLazySingletonConfiguration(
            IContainerBuilder builder,
            bool enableLazySingletons,
            Action<IContainerBuilder>? configureLazySingletons)
        {
            if (enableLazySingletons)
                configureLazySingletons?.Invoke(builder);
        }
    }
}
