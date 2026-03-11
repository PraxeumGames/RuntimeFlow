using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    /// <summary>Provides scope and scene management operations available to a runtime flow scenario.</summary>
    public interface IRuntimeFlowContext
    {
        IGameContext SessionContext { get; }
        Task InitializeAsync(CancellationToken cancellationToken = default);
        Task LoadScopeSceneAsync(Type sceneScopeKey, CancellationToken cancellationToken = default);
        Task LoadScopeSceneAsync<TSceneScope>(CancellationToken cancellationToken = default);
        Task LoadScopeModuleAsync(Type moduleScopeKey, CancellationToken cancellationToken = default);
        Task LoadScopeModuleAsync<TModuleScope>(CancellationToken cancellationToken = default);
        Task ReloadScopeModuleAsync(Type moduleScopeKey, CancellationToken cancellationToken = default);
        Task ReloadScopeModuleAsync<TModuleScope>(CancellationToken cancellationToken = default);
        Task ReloadScopeAsync(Type scopeType, CancellationToken cancellationToken = default);
        Task ReloadScopeAsync<TScope>(CancellationToken cancellationToken = default);
        Task LoadSceneSingleAsync(string sceneName, CancellationToken cancellationToken = default);
        Task LoadSceneAdditiveAsync(string sceneName, CancellationToken cancellationToken = default);
        Task GoToAsync(SceneRoute route, CancellationToken cancellationToken = default);
        Task<SceneRoute> ResolveRouteAsync(
            SceneRoute fallbackRoute,
            ISessionSceneRouteResolver? routeResolver = null,
            CancellationToken cancellationToken = default);
        TService ResolveSessionService<TService>() where TService : class;
        bool TryResolveSessionService<TService>(out TService? service) where TService : class;
        Task PreloadSceneAsync<TSceneScope>(CancellationToken cancellationToken = default);
        Task PreloadModuleAsync<TModuleScope>(CancellationToken cancellationToken = default);
        bool HasPreloadedScope<TScope>();
        Task LoadAdditiveModuleAsync<TModuleScope>(CancellationToken cancellationToken = default);
        Task UnloadAdditiveModuleAsync<TModuleScope>(CancellationToken cancellationToken = default);
    }
}
