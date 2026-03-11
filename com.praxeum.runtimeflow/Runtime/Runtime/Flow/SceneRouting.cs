using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    public interface ISessionSceneRouteResolver
    {
        Task<SceneRoute> DecideNextSceneAsync(SceneRouteDecisionContext context, CancellationToken cancellationToken = default);
    }

    public sealed class SceneRouteDecisionContext
    {
        public SceneRouteDecisionContext(IGameContext sessionContext, IRuntimeFlowContext flowContext)
        {
            SessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
            FlowContext = flowContext ?? throw new ArgumentNullException(nameof(flowContext));
        }

        public IGameContext SessionContext { get; }
        public IRuntimeFlowContext FlowContext { get; }
    }

    public sealed class SceneRoute
    {
        private SceneRoute(string sceneName, Type sceneScopeKey)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
                throw new ArgumentException("Scene name is required.", nameof(sceneName));
            SceneName = sceneName;
            SceneScopeKey = sceneScopeKey ?? throw new ArgumentNullException(nameof(sceneScopeKey));
        }

        public string SceneName { get; }
        public Type SceneScopeKey { get; }
        public Type? ModuleScopeKey { get; private set; }
        public bool LoadSceneAdditively { get; private set; }

        public static SceneRoute ToScene<TSceneScope>(string sceneName)
        {
            return new SceneRoute(sceneName, typeof(TSceneScope));
        }

        public static SceneRoute ToScene(Type sceneScopeKey, string sceneName)
        {
            return new SceneRoute(sceneName, sceneScopeKey);
        }

        public SceneRoute WithModule<TModuleScope>()
        {
            ModuleScopeKey = typeof(TModuleScope);
            return this;
        }

        public SceneRoute WithModule(Type moduleScopeKey)
        {
            ModuleScopeKey = moduleScopeKey ?? throw new ArgumentNullException(nameof(moduleScopeKey));
            return this;
        }

        public SceneRoute AsAdditive()
        {
            LoadSceneAdditively = true;
            return this;
        }
    }
}
