using RuntimeFlow.Contexts;
using VContainer;

namespace RuntimeFlow.Demo
{
    public sealed class GameplaySceneScope : ISceneScope
    {
        public void Configure(IGameScopeRegistrationBuilder builder)
        {
            builder.Register<IWorldGenerationService, WorldGenerationService>(Lifetime.Singleton);
            builder.Register<IPlayerSpawnService, PlayerSpawnService>(Lifetime.Singleton);
        }
    }

    public sealed class HudModuleScope : IModuleScope
    {
        public void Configure(IGameScopeRegistrationBuilder builder)
        {
            builder.Register<IHudService, HudService>(Lifetime.Singleton);
        }
    }

    public sealed class InventoryModuleScope : IModuleScope
    {
        public void Configure(IGameScopeRegistrationBuilder builder)
        {
            builder.Register<IInventoryViewService, InventoryViewService>(Lifetime.Singleton);
        }
    }

    public sealed class MinimapModuleScope : IModuleScope
    {
        public void Configure(IGameScopeRegistrationBuilder builder)
        {
            builder.Register<IMinimapService, MinimapService>(Lifetime.Singleton);
        }
    }
}
