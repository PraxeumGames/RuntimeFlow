using System;
using System.Collections.Generic;
using System.Linq;
using VContainer;

namespace RuntimeFlow.Contexts
{
    public static class RuntimeFlowInstallerModules
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

            if (options.EnableLazySingletons)
                options.ConfigureLazySingletons?.Invoke(builder);

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

            if (options.EnableLazySingletons)
                options.ConfigureLazySingletons?.Invoke(builder);

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

        public static void RegisterSessionSyncEntryPointsBootstrap(
            IContainerBuilder builder,
            RuntimeFlowSessionSyncEntryPointsBootstrapOptions? options = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));

            var settings = (options ?? new RuntimeFlowSessionSyncEntryPointsBootstrapOptions()).BuildSettings();
            builder.RegisterInstance(settings);
            builder.Register<RuntimeFlowSessionSyncEntryPointsInitializationService>(Lifetime.Singleton)
                .AsSelf()
                .AsImplementedInterfaces();
        }

        public static void RegisterGlobalVContainerEntryPoints(
            IContainerBuilder builder,
            RuntimeFlowVContainerEntryPointsInstallerOptions? options = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));

            var settings = (options ?? new RuntimeFlowVContainerEntryPointsInstallerOptions()).BuildSettings();
            builder.RegisterInstance(settings);

            builder.Register<RuntimeFlowGlobalVContainerEntryPointsInitializationService>(Lifetime.Singleton)
                .AsSelf()
                .AsImplementedInterfaces();
        }

        public static void RegisterLoadingAndRestartInfrastructure(
            IContainerBuilder builder,
            RuntimeFlowLoadingRestartInstallerOptions? options = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            options ??= new RuntimeFlowLoadingRestartInstallerOptions();

            var registerLoadingState = options.ResolveRegisterLoadingState(fallbackValue: false);
            var registerRestartHandler = options.ResolveRegisterRestartHandler(fallbackValue: false);
            if (!registerLoadingState && !registerRestartHandler)
                return;

            if (registerLoadingState)
            {
                var implementationType = options.ResolveLoadingStateImplementationType(null);
                if (implementationType == null)
                    throw new InvalidOperationException(
                        "Loading state implementation type must be provided when RegisterLoadingState is enabled.");

                if (!typeof(IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>)
                    .IsAssignableFrom(implementationType))
                {
                    throw new InvalidOperationException(
                        $"Loading state implementation type '{implementationType.FullName}' must implement '{typeof(IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>).FullName}'.");
                }

                var loadingServiceType = options.ResolveLoadingStateServiceType(
                    typeof(IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>));
                if (loadingServiceType != null && !loadingServiceType.IsAssignableFrom(implementationType))
                {
                    throw new InvalidOperationException(
                        $"Loading state implementation type '{implementationType.FullName}' is not assignable to '{loadingServiceType.FullName}'.");
                }

                var loadingStateOptions = options.ResolveLoadingStateOptionsInstance();
                if (loadingStateOptions != null)
                {
                    RegisterLoadingStateOptions(builder, implementationType, loadingStateOptions);
                }

                RegisterType(builder, implementationType, loadingServiceType == null
                    ? null
                    : new[] { loadingServiceType, typeof(IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>) });
            }

            if (registerRestartHandler)
            {
                var implementationType = options.ResolveRestartHandlerImplementationType(null);
                if (implementationType == null)
                    throw new InvalidOperationException(
                        "Restart handler implementation type must be provided when RegisterRestartHandler is enabled.");

                var serviceTypes = options.ResolveRestartHandlerServiceTypes(null);
                RegisterType(builder, implementationType, serviceTypes);
            }

            var additional = options.ResolveAdditionalRegistrations(null);
            if (additional == null)
                return;

            foreach (var registration in additional)
            {
                RegisterType(builder, registration.ImplementationType, registration.ServiceTypes);
            }
        }

        private static void RegisterType(
            IContainerBuilder builder,
            Type implementationType,
            IReadOnlyCollection<Type>? serviceTypes)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (implementationType == null) throw new ArgumentNullException(nameof(implementationType));

            var registrationBuilder = builder.Register(implementationType, Lifetime.Singleton).AsSelf();
            if (serviceTypes == null)
            {
                registrationBuilder.AsImplementedInterfaces();
                return;
            }

            var hasServiceType = false;
            foreach (var serviceType in serviceTypes.Where(type => type != null).Distinct())
            {
                if (!serviceType.IsAssignableFrom(implementationType))
                {
                    throw new InvalidOperationException(
                        $"Type '{implementationType.FullName}' cannot be exposed as '{serviceType.FullName}'.");
                }

                hasServiceType = true;
                registrationBuilder.As(serviceType);
            }

            if (!hasServiceType)
            {
                registrationBuilder.AsImplementedInterfaces();
            }
        }

        private static void RegisterLoadingStateOptions(
            IContainerBuilder builder,
            Type implementationType,
            object optionsInstance)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (implementationType == null) throw new ArgumentNullException(nameof(implementationType));
            if (optionsInstance == null) throw new ArgumentNullException(nameof(optionsInstance));

            if (optionsInstance is RuntimePipelineStringStageStateProviderOptions stringOptions)
            {
                builder.RegisterInstance(stringOptions);
                return;
            }

            if (typeof(RuntimePipelineStringStageStateProvider).IsAssignableFrom(implementationType))
            {
                throw new InvalidOperationException(
                    $"Loading state options instance type '{optionsInstance.GetType().FullName}' is not supported for '{implementationType.FullName}'. " +
                    $"Expected '{typeof(RuntimePipelineStringStageStateProviderOptions).FullName}'.");
            }

            builder.RegisterInstance(optionsInstance);
        }
    }
}
