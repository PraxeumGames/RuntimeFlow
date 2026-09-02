using System;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Initialization.Planning
{
    /// <summary>
    /// Ambient pipe for player-gate progress. User-gated services publish through
    /// <see cref="UserGateProgressExtensions"/> without needing the notifier injected; the
    /// active loading pipeline (RuntimeLoadingProgressNotifierAdapter, test recorders)
    /// subscribes while it observes a startup and unsubscribes afterwards. Editor tooling
    /// may also subscribe permanently for dashboards.
    /// </summary>
    public static class UserGateProgress
    {
        private static volatile IUserGateProgressNotifier? _current;

        public static void Publish(IUserGateProgressNotifier? notifier) => _current = notifier;

        internal static void GateOpened(GameContextType scope, Type serviceType, string prompt)
        {
            var handler = _current;
            handler?.OnGateOpened(scope, serviceType, prompt);
        }

        internal static void GateClosed(GameContextType scope, Type serviceType)
        {
            var handler = _current;
            handler?.OnGateClosed(scope, serviceType);
        }
    }

    /// <summary>
    /// Extension surface for user-gated services inside the load graph: a service whose
    /// InitializeAsync blocks on a dialog calls <c>this.NotifyGateOpened(...)</c> /
    /// <c>NotifyGateClosed()</c>; loading UI shows a waiting-for-player state instead of an
    /// endless spinner.
    /// </summary>
    public static class UserGateProgressExtensions
    {
        public static void NotifyGateOpened(this object gatedService, GameContextType scope, string prompt)
            => UserGateProgress.GateOpened(scope, gatedService.GetType(), prompt);

        public static void NotifyGateClosed(this object gatedService, GameContextType scope)
            => UserGateProgress.GateClosed(scope, gatedService.GetType());
    }
}
