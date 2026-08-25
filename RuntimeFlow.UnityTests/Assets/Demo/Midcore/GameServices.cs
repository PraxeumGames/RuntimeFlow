using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Demo.Midcore
{
    public sealed class EconomyService : ISessionInitializableService
    {
        public static int StartingCoins { get; private set; }
        public static void ResetStartingCoins() => StartingCoins = 0;
        public Task InitializeAsync(CancellationToken ct)
        {
            StartingCoins = RemoteConfigService.GiftCoins;
            return Task.CompletedTask;
        }
    }

    public sealed class QuestBoardService : ISessionInitializableService, IUiStartupInitializableService
    {
        public static string? Header { get; private set; }
        public static void ResetHeader() => Header = null;
        public Task InitializeAsync(CancellationToken ct)
        {
            Header = $"{ServerProfileService.DisplayName} (lvl {ServerProfileService.Level})";
            return Task.CompletedTask;
        }
    }

    public sealed class PreloaderScene : ISceneScope
    {
        public void Configure(IGameScopeRegistrationBuilder builder)
        {
            builder.Register<PreloaderController>(DiLifetime.Singleton);
        }
    }

    public sealed class PreloaderController : ISceneInitializableService
    {
        public static readonly List<string> ProgressLog = new();
        public static bool Ready { get; private set; }
        public static void Reset() { ProgressLog.Clear(); Ready = false; }
        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            ProgressLog.Add($"player={ServerProfileService.DisplayName}");
            Ready = true;
            return Task.CompletedTask;
        }
    }

    public sealed class MetaScene : ISceneScope
    {
        public void Configure(IGameScopeRegistrationBuilder builder)
        {
            builder.Register<MetaBootstrap>(DiLifetime.Singleton);
        }
    }

    public sealed class MetaBootstrap : ISceneInitializableService
    {
        public static string? Summary { get; private set; }
        public static void ResetSummary() => Summary = null;
        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Summary = $"{ServerProfileService.DisplayName} coins={EconomyService.StartingCoins}";
            return Task.CompletedTask;
        }
    }
}
