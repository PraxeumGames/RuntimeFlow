using System;
using System.Collections.Generic;
using UnityEngine;

namespace RuntimeFlow.Internal
{
    /// <summary>
    /// Diagnostics-only directory of the <see cref="RuntimeFlowHost"/> instances that are alive, so the
    /// editor dashboard can find them without any runtime code depending on a static. References are
    /// weak and pruned on read, and the list is cleared when the player loop restarts.
    /// </summary>
    internal static class FlowRegistry
    {
        private static readonly List<WeakReference<RuntimeFlowHost>> Hosts =
            new List<WeakReference<RuntimeFlowHost>>();

        /// <summary>Raised after a host was added, removed or pruned; never raised from runtime logic.</summary>
        public static event Action? Changed;

        /// <summary>Registers a host; called from its constructor.</summary>
        public static void Add(RuntimeFlowHost host)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            Prune();
            Hosts.Add(new WeakReference<RuntimeFlowHost>(host));
            Changed?.Invoke();
        }

        /// <summary>Unregisters a host; called from its disposal.</summary>
        public static void Remove(RuntimeFlowHost host)
        {
            var removed = false;
            for (var i = Hosts.Count - 1; i >= 0; i--)
            {
                if (!Hosts[i].TryGetTarget(out var candidate) || ReferenceEquals(candidate, host))
                {
                    Hosts.RemoveAt(i);
                    removed = true;
                }
            }
            if (removed) Changed?.Invoke();
        }

        /// <summary>Hosts that are still alive, oldest first; collected ones are pruned on read.</summary>
        public static IReadOnlyList<RuntimeFlowHost> Live
        {
            get
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

        /// <summary>Drops every entry; runs before the first scene of a player-loop session.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Clear()
        {
            if (Hosts.Count == 0) return;
            Hosts.Clear();
            Changed?.Invoke();
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
