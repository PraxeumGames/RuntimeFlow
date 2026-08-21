using RuntimeFlow.Contexts;

namespace RuntimeFlow.Demo
{
    public sealed class GameplaySceneScope : ISceneScope
    {
        public void Configure(IGameScopeRegistrationBuilder builder)
        {
            builder.Register<IWorldGenerationService, WorldGenerationService>(DiLifetime.Singleton);
            builder.Register<IPlayerSpawnService, PlayerSpawnService>(DiLifetime.Singleton);
        }
    }

    public sealed class HudModuleScope : IModuleScope
    {
        public void Configure(IGameScopeRegistrationBuilder builder)
        {
            builder.Register<IHudService, HudService>(DiLifetime.Singleton);
        }
    }

    public sealed class InventoryModuleScope : IModuleScope
    {
        public void Configure(IGameScopeRegistrationBuilder builder)
        {
            builder.Register<IInventoryViewService, InventoryViewService>(DiLifetime.Singleton);
        }
    }

    public sealed class MinimapModuleScope : IModuleScope
    {
        public void Configure(IGameScopeRegistrationBuilder builder)
        {
            builder.Register<IMinimapService, MinimapService>(DiLifetime.Singleton);
        }
    }
}
