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

    internal sealed class RestartAwareSceneBootstrapScenario : IRuntimeFlowScenario
    {
        private static readonly PreBootstrapProjectionStatusMap<PreBootstrapStageStatus> PreBootstrapStatusMap =
            new(
                notStartedStatus: PreBootstrapStageStatus.NotStarted,
                runningStatus: PreBootstrapStageStatus.Running,
                succeededStatus: PreBootstrapStageStatus.Succeeded,
                failedStatus: PreBootstrapStageStatus.Failed);

        private readonly string _sceneName;
        private readonly IPreBootstrapStageService? _preBootstrapStageService;
        private readonly IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>? _loadingState;
        private readonly Type _replayReloadScopeType;
        private readonly string _runStageName;
        private readonly string _preBootstrapStageName;
        private readonly string _runStartReasonCode;
        private readonly string _replayRunStartReasonCode;
        private readonly string _runCompleteReasonCode;
        private readonly string _runFailReasonCode;
        private readonly Func<PreBootstrapStageStatus, string, string> _preBootstrapReasonCodeResolver;
        private readonly string? _preBootstrapFailedReasonCodeFallback;
        private readonly string? _preBootstrapFailedDiagnosticFallback;
        private bool _isPreBootstrapCompleted;
        private bool _isPreBootstrapProjected;

        public RestartAwareSceneBootstrapScenario(RestartAwareSceneBootstrapScenarioOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            options.Validate();

            _sceneName = options.SceneName!;
            _preBootstrapStageService = options.PreBootstrapStageService;
            _loadingState = options.LoadingState;
            _replayReloadScopeType = options.ReplayReloadScopeType!;
            _runStageName = options.RunStageName;
            _preBootstrapStageName = options.PreBootstrapStageName;
            _runStartReasonCode = options.RunStartReasonCode;
            _replayRunStartReasonCode = options.ReplayRunStartReasonCode;
            _runCompleteReasonCode = options.RunCompleteReasonCode;
            _runFailReasonCode = options.RunFailReasonCode;
            _preBootstrapReasonCodeResolver = options.PreBootstrapReasonCodeResolver!;
            _preBootstrapFailedReasonCodeFallback = options.PreBootstrapFailedReasonCodeFallback;
            _preBootstrapFailedDiagnosticFallback = options.PreBootstrapFailedDiagnosticFallback;
        }

        public async Task ExecuteAsync(IRuntimeFlowContext context, CancellationToken cancellationToken = default)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var loadingState = ResolveLoadingState(context) ?? _loadingState;
            EnsurePreBootstrapProjection(loadingState);
            var runReasonCode = RuntimeFlowReplayScope.IsActive
                ? _replayRunStartReasonCode
                : _runStartReasonCode;
            loadingState?.StartStage(_runStageName, runReasonCode);

            try
            {
                await EnsurePreBootstrapAsync(loadingState, cancellationToken).ConfigureAwait(false);

                var sceneWasUnloadedForReplay = false;
                if (RuntimeFlowReplayScope.IsActive)
                {
                    sceneWasUnloadedForReplay = await PrepareSceneForReplayAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                if (sceneWasUnloadedForReplay
                    || !await IsSceneLoadedAsync(_sceneName, cancellationToken).ConfigureAwait(false))
                {
                    await context.LoadSceneAdditiveAsync(_sceneName, cancellationToken).ConfigureAwait(false);
                }

                if (RuntimeFlowReplayScope.IsActive)
                {
                    await context.ReloadScopeAsync(_replayReloadScopeType, cancellationToken).ConfigureAwait(false);
                    loadingState = ResolveLoadingState(context) ?? loadingState;
                    loadingState?.CompleteStage(_runStageName, _runCompleteReasonCode);
                    return;
                }

                await context.InitializeAsync(cancellationToken).ConfigureAwait(false);
                loadingState?.CompleteStage(_runStageName, _runCompleteReasonCode);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                loadingState?.FailStage(_runStageName, _runFailReasonCode, ex, ex.Message);
                throw;
            }
        }

        private async Task EnsurePreBootstrapAsync(
            IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>? loadingState,
            CancellationToken cancellationToken)
        {
            if (_isPreBootstrapCompleted || _preBootstrapStageService == null)
            {
                return;
            }

            try
            {
                await _preBootstrapStageService.EnsureCompletedAsync(cancellationToken).ConfigureAwait(false);
                _isPreBootstrapCompleted = true;
                ProjectPreBootstrapState(loadingState);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                ProjectPreBootstrapState(loadingState);
                throw;
            }
        }

        private void EnsurePreBootstrapProjection(
            IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>? loadingState)
        {
            if (_isPreBootstrapProjected || loadingState == null || _preBootstrapStageService == null)
            {
                return;
            }

            ProjectPreBootstrapState(loadingState);
            _isPreBootstrapProjected = true;
        }

        private static IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>? ResolveLoadingState(
            IRuntimeFlowContext context)
        {
            return RuntimeFlowServiceResolver.TryResolveFromContext(
                context,
                out IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>> service)
                ? service
                : null;
        }

        private void ProjectPreBootstrapState(
            IRuntimePipelineStageStateProvider<string, RuntimePipelineStageSnapshot<string>>? loadingState)
        {
            if (loadingState == null || _preBootstrapStageService == null)
            {
                return;
            }

            PreBootstrapPipelineStageProjector.Project(
                preBootstrapStageService: _preBootstrapStageService,
                pipelineState: loadingState,
                pipelineStage: _preBootstrapStageName,
                statusMap: PreBootstrapStatusMap,
                reasonCodeResolver: _preBootstrapReasonCodeResolver,
                failedReasonCodeFallback: _preBootstrapFailedReasonCodeFallback,
                failedDiagnosticFallback: _preBootstrapFailedDiagnosticFallback);
        }

        private async Task<bool> PrepareSceneForReplayAsync(CancellationToken cancellationToken)
        {
            var scene = await ExecuteOnMainThreadAsync(
                    () => SceneManager.GetSceneByName(_sceneName),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!scene.IsValid()
                || !scene.isLoaded
                || !await CanUnloadLoadedSceneAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            var unloadOperation = await ExecuteOnMainThreadAsync(
                    () => SceneManager.UnloadSceneAsync(scene),
                    cancellationToken)
                .ConfigureAwait(false);
            if (unloadOperation == null)
            {
                return false;
            }

            while (!await ExecuteOnMainThreadAsync(() => unloadOperation.isDone, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            await Task.Yield();
            return true;
        }

        private static Task<bool> CanUnloadLoadedSceneAsync(CancellationToken cancellationToken)
        {
            return ExecuteOnMainThreadAsync(() =>
            {
                var loadedScenes = 0;
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    if (SceneManager.GetSceneAt(i).isLoaded)
                    {
                        loadedScenes++;
                    }
                }

                return loadedScenes > 1;
            }, cancellationToken);
        }

        internal static Task<bool> IsSceneLoadedAsync(string sceneName, CancellationToken cancellationToken)
        {
            return ExecuteOnMainThreadAsync(() =>
            {
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (scene.isLoaded && scene.name == sceneName)
                        return true;
                }

                return false;
            }, cancellationToken);
        }

        private static Task<T> ExecuteOnMainThreadAsync<T>(
            Func<T> action,
            CancellationToken cancellationToken)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            cancellationToken.ThrowIfCancellationRequested();

            var context = GameContext.MainThreadContext;
            if (context == null || GameContext.IsOnMainThread())
            {
                return Task.FromResult(action());
            }

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationRegistration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
            context.Post(_ =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    completion.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }, null);

            return AwaitMainThreadCompletionAsync(completion.Task, cancellationRegistration);
        }

        private static async Task<T> AwaitMainThreadCompletionAsync<T>(
            Task<T> task,
            CancellationTokenRegistration cancellationRegistration)
        {
            try
            {
                return await task.ConfigureAwait(false);
            }
            finally
            {
                cancellationRegistration.Dispose();
            }
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

            if (!await RestartAwareSceneBootstrapScenario
                    .IsSceneLoadedAsync(_sceneName, cancellationToken)
                    .ConfigureAwait(false))
                await context.LoadSceneAdditiveAsync(_sceneName, cancellationToken).ConfigureAwait(false);

            await context.InitializeAsync(cancellationToken).ConfigureAwait(false);
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
