using System;
using System.Collections.Generic;
using RuntimeFlow.Events;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Mutable scope state shared by the builder services (orchestrator, transitions,
    /// preload, disposal). Threading contract:
    /// - Writes happen only inside operations serialized by <see cref="ScopeOperationCoordinator"/>
    ///   locks or on the main thread via scheduler hops; every such transition carries a memory
    ///   barrier, so readers observe state no older than their last synchronization point.
    /// - Reference writes are atomic; readers must tolerate staleness (a just-cleared context
    ///   may still be visible) and treat disposed contexts defensively.
    /// - The preloaded/additive dictionaries are mutated under the side lock; lock-free
    ///   readers (the Editor dashboard) must handle mid-enumeration changes.
    /// Do not add fields here that require stronger guarantees without revisiting this contract.
    /// </summary>
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
