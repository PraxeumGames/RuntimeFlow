using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.SceneManagement;

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

    internal sealed class InitializeOnlyScenario : IRuntimeFlowScenario
    {
        internal static readonly InitializeOnlyScenario Instance = new();

        private InitializeOnlyScenario()
        {
        }

        public Task ExecuteAsync(IRuntimeFlowContext context, CancellationToken cancellationToken = default)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            return context.InitializeAsync(cancellationToken);
        }
    }

    internal sealed class EnsureSceneLoadedThenInitializeScenario : IRuntimeFlowScenario
    {
        private readonly string _sceneName;

        public EnsureSceneLoadedThenInitializeScenario(string sceneName)
        {
            _sceneName = sceneName ?? throw new ArgumentNullException(nameof(sceneName));
        }

        public async Task ExecuteAsync(IRuntimeFlowContext context, CancellationToken cancellationToken = default)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            if (!IsSceneLoaded(_sceneName))
                await context.LoadSceneAdditiveAsync(_sceneName, cancellationToken).ConfigureAwait(false);

            await context.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        private static bool IsSceneLoaded(string sceneName)
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.name == sceneName)
                    return true;
            }

            return false;
        }
    }

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

    internal sealed class StandardSessionScenario : IRuntimeFlowScenario
    {
        private readonly SceneRoute _fallbackRoute;
        private readonly string? _startupSceneName;
        private readonly string? _sessionAdditiveSceneName;
        private readonly Type? _sessionAdditiveSceneScopeKey;
        private readonly ISessionSceneRouteResolver? _routeResolver;
        private readonly IReadOnlyCollection<IRuntimeFlowGuard> _guards;

        public StandardSessionScenario(
            SceneRoute fallbackRoute,
            string? startupSceneName,
            string? sessionAdditiveSceneName,
            Type? sessionAdditiveSceneScopeKey,
            ISessionSceneRouteResolver? routeResolver,
            IReadOnlyCollection<IRuntimeFlowGuard> guards)
        {
            _fallbackRoute = fallbackRoute ?? throw new ArgumentNullException(nameof(fallbackRoute));
            _startupSceneName = startupSceneName;
            _sessionAdditiveSceneName = sessionAdditiveSceneName;
            _sessionAdditiveSceneScopeKey = sessionAdditiveSceneScopeKey;
            _routeResolver = routeResolver;
            _guards = guards ?? throw new ArgumentNullException(nameof(guards));
        }

        public async Task ExecuteAsync(IRuntimeFlowContext context, CancellationToken cancellationToken = default)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            if (!string.IsNullOrWhiteSpace(_startupSceneName))
            {
                await context.LoadSceneSingleAsync(_startupSceneName, cancellationToken).ConfigureAwait(false);
            }

            await EnsureGuardsAsync(RuntimeFlowGuardStage.BeforeInitialize, context, targetRoute: null, cancellationToken)
                .ConfigureAwait(false);
            await context.InitializeAsync(cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(_sessionAdditiveSceneName))
            {
                await EnsureGuardsAsync(RuntimeFlowGuardStage.BeforeSessionSceneLoad, context, targetRoute: null, cancellationToken)
                    .ConfigureAwait(false);
                if (_sessionAdditiveSceneScopeKey != null)
                    await context.LoadScopeSceneAsync(_sessionAdditiveSceneScopeKey, cancellationToken).ConfigureAwait(false);

                await context.LoadSceneAdditiveAsync(_sessionAdditiveSceneName, cancellationToken).ConfigureAwait(false);
            }

            await EnsureGuardsAsync(RuntimeFlowGuardStage.BeforeRouteResolution, context, targetRoute: null, cancellationToken)
                .ConfigureAwait(false);
            var route = await context
                .ResolveRouteAsync(_fallbackRoute, _routeResolver, cancellationToken)
                .ConfigureAwait(false);
            await EnsureGuardsAsync(RuntimeFlowGuardStage.BeforeNavigation, context, route, cancellationToken)
                .ConfigureAwait(false);
            await context.GoToAsync(route, cancellationToken).ConfigureAwait(false);
        }

        private async Task EnsureGuardsAsync(
            RuntimeFlowGuardStage stage,
            IRuntimeFlowContext context,
            SceneRoute? targetRoute,
            CancellationToken cancellationToken)
        {
            foreach (var guard in _guards)
            {
                var result = await guard
                    .EvaluateAsync(new RuntimeFlowGuardContext(stage, context, targetRoute), cancellationToken)
                    .ConfigureAwait(false);

                if (!result.IsAllowed)
                {
                    throw new RuntimeFlowGuardFailedException(
                        stage,
                        result.ReasonCode ?? "FLOW_GUARD_BLOCKED",
                        result.Reason);
                }
            }
        }
    }
}
