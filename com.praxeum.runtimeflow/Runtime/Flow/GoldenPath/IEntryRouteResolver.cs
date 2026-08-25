using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Flow
{
    /// <summary>
    /// Resolves which scene the player lands in after boot. Registered by the game,
    /// consumed by <see cref="GameFlowBuilder"/> when an entry route is configured.
    ///
    /// Typical implementations inspect player profile, save data, feature flags and active
    /// session state to decide between tutorial, meta hub, session rejoin, or battle.
    /// </summary>
    public interface IEntryRouteResolver
    {
        Task<EntryRoute> ResolveAsync(CancellationToken cancellationToken);
    }

    /// <summary>The resolved destination: a scene type plus optional navigation metadata.</summary>
    public sealed class EntryRoute
    {
        public Type SceneType { get; }
        public string Reason { get; }

        public EntryRoute(Type sceneType, string reason)
        {
            SceneType = sceneType ?? throw new ArgumentNullException(nameof(sceneType));
            Reason = reason ?? "";
        }
    }
}
