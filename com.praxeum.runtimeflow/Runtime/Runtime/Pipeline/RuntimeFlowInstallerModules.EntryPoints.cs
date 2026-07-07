using System;
using VContainer;

namespace RuntimeFlow.Contexts
{
    public static partial class RuntimeFlowInstallerModules
    {
        public static void RegisterSessionVContainerEntryPoints(
            IContainerBuilder builder,
            RuntimeFlowSessionVContainerEntryPointsInstallerOptions? options = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));

            var settings = (options ?? new RuntimeFlowSessionVContainerEntryPointsInstallerOptions()).BuildSettings();
            RegisterEntryPointsSettings(builder, settings);
        }

        public static void RegisterGlobalVContainerEntryPoints(
            IContainerBuilder builder,
            RuntimeFlowVContainerEntryPointsInstallerOptions? options = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));

            var settings = (options ?? new RuntimeFlowVContainerEntryPointsInstallerOptions()).BuildSettings();
            RegisterEntryPointsSettings(builder, settings);
        }

        private static void RegisterEntryPointsSettings(
            IContainerBuilder builder,
            RuntimeFlowVContainerEntryPointsSettings settings)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            builder.RegisterInstance(settings);
        }
    }
}
