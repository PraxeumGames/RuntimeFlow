using System;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.Contexts
{
    public sealed class RuntimeFlowGlobalInstallerOptions
    {
        public bool EnableLazySingletons { get; set; } = true;
        public bool RegisterPipelineProvider { get; set; } = true;
        public Action<IContainerBuilder>? ConfigureLazySingletons { get; set; }
    }

    public sealed class RuntimeFlowSessionInstallerOptions
    {
        public bool EnableLazySingletons { get; set; } = true;
        public bool RegisterBuildCallbackResolverWarmup { get; set; }
        public Type? BuildCallbackResolverType { get; set; }
        public Action<IObjectResolver>? BuildCallbackResolverAction { get; set; }
        public Action<IContainerBuilder>? ConfigureLazySingletons { get; set; }
    }

    public sealed class RuntimeFlowSessionBootstrapInstallerOptions
    {
        public RuntimeFlowSessionInstallerOptions? SessionInfrastructure { get; set; }
        public RuntimeFlowSessionSyncEntryPointsBootstrapOptions? SessionSyncEntryPointsBootstrap { get; set; }
        public RuntimeFlowLoadingRestartInstallerOptions? LoadingAndRestartInfrastructure { get; set; }
    }

    public sealed class RuntimeFlowGlobalBootstrapPresetOptions
    {
        public RuntimeFlowGlobalInstallerOptions GlobalInfrastructure { get; } = new();
        public RuntimeFlowVContainerEntryPointsInstallerOptions GlobalVContainerEntryPoints { get; } = new();
        public bool RegisterGlobalVContainerEntryPoints { get; set; } = true;

        public RuntimeFlowGlobalBootstrapPresetOptions ConfigureGlobalInfrastructure(
            Action<RuntimeFlowGlobalInstallerOptions> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            configure(GlobalInfrastructure);
            return this;
        }

        public RuntimeFlowGlobalBootstrapPresetOptions ConfigureGlobalVContainerEntryPoints(
            Action<RuntimeFlowVContainerEntryPointsInstallerOptions> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            configure(GlobalVContainerEntryPoints);
            return this;
        }
    }

    public sealed class RuntimeFlowSessionBootstrapPresetOptions
    {
        public RuntimeFlowSessionInstallerOptions SessionInfrastructure { get; } = new();
        public RuntimeFlowSessionSyncEntryPointsBootstrapOptions SessionSyncEntryPointsBootstrap { get; } = new();
        public RuntimeFlowLoadingRestartInstallerOptions LoadingAndRestartInfrastructure { get; } = new();

        public RuntimeFlowSessionBootstrapPresetOptions ConfigureSessionInfrastructure(
            Action<RuntimeFlowSessionInstallerOptions> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            configure(SessionInfrastructure);
            return this;
        }

        public RuntimeFlowSessionBootstrapPresetOptions ConfigureSessionSyncEntryPointsBootstrap(
            Action<RuntimeFlowSessionSyncEntryPointsBootstrapOptions> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            configure(SessionSyncEntryPointsBootstrap);
            return this;
        }

        public RuntimeFlowSessionBootstrapPresetOptions ConfigureLoadingAndRestartInfrastructure(
            Action<RuntimeFlowLoadingRestartInstallerOptions> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            configure(LoadingAndRestartInfrastructure);
            return this;
        }

        internal RuntimeFlowSessionBootstrapInstallerOptions BuildInstallerOptions()
        {
            return new RuntimeFlowSessionBootstrapInstallerOptions
            {
                SessionInfrastructure = SessionInfrastructure,
                SessionSyncEntryPointsBootstrap = SessionSyncEntryPointsBootstrap,
                LoadingAndRestartInfrastructure = LoadingAndRestartInfrastructure
            };
        }
    }
}
