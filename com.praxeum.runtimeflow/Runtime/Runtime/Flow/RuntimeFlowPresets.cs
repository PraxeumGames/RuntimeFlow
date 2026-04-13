using System;
namespace RuntimeFlow.Contexts
{
    public static class RuntimeFlowPresets
    {
        /// <summary>
        /// Minimal runtime flow that initializes registered services without loading any Unity scenes.
        /// </summary>
        public static IRuntimeFlowScenario InitializeOnly()
        {
            return InitializeOnlyScenario.Instance;
        }

        /// <summary>
        /// Ensures a named session scene is available, loading it additively only when needed,
        /// and then initializes registered services.
        /// </summary>
        public static IRuntimeFlowScenario EnsureSceneLoadedThenInitialize(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
                throw new ArgumentException("Session scene name is required.", nameof(sceneName));

            return new EnsureSceneLoadedThenInitializeScenario(sceneName);
        }

        public static IRuntimeFlowScenario RestartAwareSceneBootstrap(RestartAwareSceneBootstrapScenarioOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            return new RestartAwareSceneBootstrapScenario(options);
        }

        public static IRuntimeFlowScenario StandardSession(
            SceneRoute fallbackRoute,
            Action<StandardSessionFlowBuilder>? configure = null)
        {
            if (fallbackRoute == null) throw new ArgumentNullException(nameof(fallbackRoute));

            var builder = new StandardSessionFlowBuilder(fallbackRoute);
            configure?.Invoke(builder);
            return builder.Build();
        }
    }
}
