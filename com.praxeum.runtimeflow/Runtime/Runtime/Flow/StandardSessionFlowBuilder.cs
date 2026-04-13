using System;
using System.Collections.Generic;

namespace RuntimeFlow.Contexts
{
    public sealed class StandardSessionFlowBuilder
    {
        private readonly SceneRoute _fallbackRoute;
        private string? _startupSceneName;
        private string? _sessionAdditiveSceneName;
        private Type? _sessionAdditiveSceneScopeKey;
        private ISessionSceneRouteResolver? _routeResolver;
        private readonly List<IRuntimeFlowGuard> _guards = new();

        internal StandardSessionFlowBuilder(SceneRoute fallbackRoute)
        {
            _fallbackRoute = fallbackRoute ?? throw new ArgumentNullException(nameof(fallbackRoute));
        }

        public StandardSessionFlowBuilder WithStartupScene(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
                throw new ArgumentException("Startup scene name is required.", nameof(sceneName));
            _startupSceneName = sceneName;
            return this;
        }

        public StandardSessionFlowBuilder WithSessionAdditiveScene(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
                throw new ArgumentException("Session additive scene name is required.", nameof(sceneName));
            _sessionAdditiveSceneName = sceneName;
            _sessionAdditiveSceneScopeKey = null;
            return this;
        }

        public StandardSessionFlowBuilder WithSessionAdditiveScene<TSceneScope>(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
                throw new ArgumentException("Session additive scene name is required.", nameof(sceneName));
            _sessionAdditiveSceneName = sceneName;
            _sessionAdditiveSceneScopeKey = typeof(TSceneScope);
            return this;
        }

        public StandardSessionFlowBuilder WithRouteResolver(ISessionSceneRouteResolver routeResolver)
        {
            _routeResolver = routeResolver ?? throw new ArgumentNullException(nameof(routeResolver));
            return this;
        }

        public StandardSessionFlowBuilder WithGuard(IRuntimeFlowGuard guard)
        {
            _guards.Add(guard ?? throw new ArgumentNullException(nameof(guard)));
            return this;
        }

        internal IRuntimeFlowScenario Build()
        {
            return new StandardSessionScenario(
                _fallbackRoute,
                _startupSceneName,
                _sessionAdditiveSceneName,
                _sessionAdditiveSceneScopeKey,
                _routeResolver,
                new List<IRuntimeFlowGuard>(_guards));
        }
    }
}
