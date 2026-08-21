using System;
using System.Collections.Generic;
using RuntimeFlow.Events;

namespace RuntimeFlow.Contexts
{
    internal sealed class ActiveScopeState
    {
        public IGameContext? GlobalContext { get; set; }
        public GameContext? SessionContext { get; set; }
        public GameContext? SceneContext { get; set; }
        public GameContext? ModuleContext { get; set; }

        public Type? ActiveSceneScopeKey { get; set; }
        public Type? ActiveModuleScopeKey { get; set; }

        public bool OwnsGlobalContext { get; set; } = true;

        public Dictionary<Type, GameContext> PreloadedContexts { get; } = new();

        public Dictionary<Type, GameContext> AdditiveModuleContexts { get; } = new();

        public ScopeEventBus? GlobalEventBus { get; set; }
        public ScopeEventBus? SessionEventBus { get; set; }
        public ScopeEventBus? SceneEventBus { get; set; }
        public ScopeEventBus? ModuleEventBus { get; set; }

        public Action<IGameContext>? OnGlobalInitialized { get; set; }
        public Action<IGameContext>? OnSessionInitialized { get; set; }
        public Action<IGameContext>? OnSceneInitialized { get; set; }
        public Action<IGameContext>? OnModuleInitialized { get; set; }
    }
}
