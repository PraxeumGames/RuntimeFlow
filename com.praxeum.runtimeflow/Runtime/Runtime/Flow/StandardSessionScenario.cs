using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
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
