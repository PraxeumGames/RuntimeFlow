using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using RuntimeFlow.Flow;

namespace RuntimeFlow.Demo.Midcore
{
    // ---------- routing target scenes ----------

    public sealed class TutorialScene : ISceneScope
    {
        public void Configure(IGameScopeRegistrationBuilder builder)
        {
            builder.Register<TutorialController>(DiLifetime.Singleton);
        }
    }

    public sealed class TutorialController : ISceneInitializableService
    {
        public static bool Completed { get; private set; }
        public static void Reset() => Completed = false;

        public Task InitializeAsync(CancellationToken ct)
        {
            Completed = true;
            return Task.CompletedTask;
        }
    }

    public sealed class SessionRejoinScene : ISceneScope
    {
        public void Configure(IGameScopeRegistrationBuilder builder)
        {
            builder.Register<SessionRejoinService>(DiLifetime.Singleton);
        }
    }

    public sealed class SessionRejoinService : ISceneInitializableService
    {
        public static bool Rejoined { get; private set; }
        public static void Reset() => Rejoined = false;

        public Task InitializeAsync(CancellationToken ct)
        {
            Rejoined = true;
            return Task.CompletedTask;
        }
    }

    // ---------- entry route resolver ----------

    /// <summary>
    /// Resolves the entry scene based on player state:
    /// - New player (no save version) → Tutorial
    /// - Active network session → Session rejoin
    /// - Default → Meta hub
    /// </summary>
    public sealed class MidcoreEntryRouteResolver : IEntryRouteResolver
    {
        /// <summary>Set by tests to simulate different routing scenarios.</summary>
        public static string Scenario { get; set; } = "default";

        public Task<EntryRoute> ResolveAsync(CancellationToken cancellationToken)
        {
            switch (Scenario)
            {
                case "new-player":
                    return Task.FromResult(new EntryRoute(typeof(TutorialScene), "tutorial"));
                case "session-rejoin":
                    return Task.FromResult(new EntryRoute(typeof(SessionRejoinScene), "rejoin"));
                default:
                    return Task.FromResult(new EntryRoute(typeof(MetaScene), "meta"));
            }
        }
    }
}
