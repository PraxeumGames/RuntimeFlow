using System;
using System.Collections.Generic;
using UnityEngine;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace RuntimeFlow.Internal
{
    /// <summary>
    /// Diagnostics-only directory of the <see cref="RuntimeFlowHost"/> instances that are alive, so the
    /// editor dashboard can find them without any runtime code depending on a static. References are
    /// weak and pruned on read, and the list — subscribers of <see cref="Changed"/> included — is cleared
    /// when the player loop restarts. Thread-safe: a host may be constructed or collected anywhere.
    /// </summary>
    internal static class FlowRegistry
    {
        private static readonly object Gate = new object();
        private static readonly List<WeakReference<RuntimeFlowHost>> Hosts =
            new List<WeakReference<RuntimeFlowHost>>();

        private static Action? _changed;

        /// <summary>
        /// Raised after a host was added, removed or pruned; never raised from runtime logic. A subscriber
        /// that throws is reported and skipped: it never breaks a host's construction or disposal. Every
        /// subscription is dropped when a player-loop session starts (see <see cref="ResetForPlayerLoop"/>).
        /// </summary>
        public static event Action? Changed
        {
            add { lock (Gate) _changed += value; }
            remove { lock (Gate) _changed -= value; }
        }

        /// <summary>Registers a host; called from its constructor.</summary>
        /// <param name="logger">Receives the failure of a <see cref="Changed"/> subscriber.</param>
        public static void Add(RuntimeFlowHost host, ILogger? logger = null)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            lock (Gate)
            {
                Prune();
                Hosts.Add(new WeakReference<RuntimeFlowHost>(host));
            }
            RaiseChanged(logger);
        }

        /// <summary>Unregisters a host; called from its disposal.</summary>
        /// <param name="logger">Receives the failure of a <see cref="Changed"/> subscriber.</param>
        public static void Remove(RuntimeFlowHost host, ILogger? logger = null)
        {
            var removed = false;
            lock (Gate)
            {
                for (var i = Hosts.Count - 1; i >= 0; i--)
                {
                    if (!Hosts[i].TryGetTarget(out var candidate) || ReferenceEquals(candidate, host))
                    {
                        Hosts.RemoveAt(i);
                        removed = true;
                    }
                }
            }
            if (removed) RaiseChanged(logger);
        }

        /// <summary>Hosts that are still alive, oldest first; collected ones are pruned on read.</summary>
        public static IReadOnlyList<RuntimeFlowHost> Live
        {
            get
            {
                lock (Gate)
                {
                    var live = new List<RuntimeFlowHost>(Hosts.Count);
                    for (var i = Hosts.Count - 1; i >= 0; i--)
                    {
                        if (Hosts[i].TryGetTarget(out var host)) live.Add(host);
                        else Hosts.RemoveAt(i);
                    }
                    live.Reverse();
                    return live;
                }
            }
        }

        /// <summary>Drops every entry and notifies the subscribers.</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                if (Hosts.Count == 0) return;
                Hosts.Clear();
            }
            RaiseChanged(null);
        }

        /// <summary>
        /// Runs before the first scene of a player-loop session: drops every entry and every
        /// <see cref="Changed"/> subscriber. With domain reload disabled the statics survive from the previous
        /// session, and so would subscribers that belong to it; the editor dashboard subscribes again once
        /// Play Mode is entered.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void ResetForPlayerLoop()
        {
            lock (Gate)
            {
                Hosts.Clear();
                _changed = null;
            }
        }

        /// <summary>Invokes every subscriber outside the lock; a throwing one is reported and the rest still run.</summary>
        private static void RaiseChanged(ILogger? logger)
        {
            Action? handlers;
            lock (Gate) handlers = _changed;
            if (handlers == null) return;

            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action)handler)();
                }
                catch (Exception exception)
                {
                    Report(logger, exception);
                }
            }
        }

        private static void Report(ILogger? logger, Exception exception)
        {
            var message = $"[RuntimeFlow] a FlowRegistry.Changed subscriber threw {exception.GetType().Name}; ignored.";
            try
            {
                if (logger != null) logger.Error(message, exception);
                else Debug.LogWarning(message + " " + exception);
            }
            catch (Exception)
            {
                // Diagnostics only: nothing a subscriber or a logger does may reach the host.
            }
        }

        private static void Prune()
        {
            for (var i = Hosts.Count - 1; i >= 0; i--)
            {
                if (!Hosts[i].TryGetTarget(out _)) Hosts.RemoveAt(i);
            }
        }
    }
}
