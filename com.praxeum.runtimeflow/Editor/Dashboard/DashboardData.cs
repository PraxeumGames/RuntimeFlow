using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using RuntimeFlow.Internal;
using UnityEditor;
using UnityEngine;

namespace RuntimeFlow.Editor
{
    /// <summary>
    /// One service row of a dashboard snapshot: the editor-side copy of a <see cref="ServiceStatus"/>,
    /// with the exception flattened to strings so the row survives serialization and domain reloads.
    /// </summary>
    [Serializable]
    internal sealed class DashboardService
    {
        /// <summary>Identity of this node within its run; distinct even when display names repeat.</summary>
        public string Id = string.Empty;
        /// <summary>Service name, as reported by the run.</summary>
        public string Name = string.Empty;

        /// <summary>Name of the scope the service belongs to.</summary>
        public string Scope = string.Empty;

        /// <summary>Declared phase, or an empty string when the run has no phases.</summary>
        public string Phase = string.Empty;

        /// <summary>Lifecycle state at capture time.</summary>
        public ServiceState State;

        /// <summary>Name of <see cref="State"/>, so the diagnostics JSON reads without the enum at hand.</summary>
        public string StateName = string.Empty;

        /// <summary>True when a failure degrades rather than fails the run.</summary>
        public bool Optional;

        /// <summary>True when the service waits for the player and never times out.</summary>
        public bool UserGated;

        /// <summary>True while a user-gated service is running.</summary>
        public bool AwaitingPlayer;

        /// <summary>Time spent running, in milliseconds.</summary>
        public double ElapsedMs;

        /// <summary>Last reported sub-progress, 0..1.</summary>
        public float Progress;

        /// <summary>Progress weight of the service.</summary>
        public double Weight;

        /// <summary>Names of every service this one waits for.</summary>
        public List<string> Dependencies = new List<string>();

        /// <summary>Node identities parallel to Dependencies; empty for an uncaptured external/barrier.</summary>
        public List<string> DependencyIds = new List<string>();

        /// <summary>Dependencies that had not finished at capture time.</summary>
        public List<string> WaitingOn = new List<string>();

        /// <summary>Type name of the failure, or an empty string.</summary>
        public string ErrorType = string.Empty;

        /// <summary>Message of the failure, or an empty string.</summary>
        public string ErrorMessage = string.Empty;

        /// <summary>Stack trace of the failure, or an empty string.</summary>
        public string ErrorStack = string.Empty;

        /// <summary>True when this row carries a captured exception.</summary>
        public bool HasError => !string.IsNullOrEmpty(ErrorType);
    }

    /// <summary>One scope of a dashboard snapshot: the run's counters plus its service rows.</summary>
    [Serializable]
    internal sealed class DashboardScope
    {
        /// <summary>Identity of the run, independent of its consumer-provided display name.</summary>
        public string Id = string.Empty;
        /// <summary>Name of the scope, for example "global", "session" or a child scope name.</summary>
        public string Name = string.Empty;

        /// <summary>State of the run of this scope.</summary>
        public RunState State;

        /// <summary>Name of <see cref="State"/>, so the diagnostics JSON reads without the enum at hand.</summary>
        public string StateName = string.Empty;

        /// <summary>Weighted completion percentage of this scope, 0..100.</summary>
        public double Percent;

        /// <summary>Time since the run of this scope started, in milliseconds.</summary>
        public double ElapsedMs;

        /// <summary>Number of restarts this scope has been through.</summary>
        public int RestartCount;

        /// <summary>Services that completed or degraded.</summary>
        public int Done;

        /// <summary>Services that failed or were cancelled.</summary>
        public int Failed;

        /// <summary>Services that never started.</summary>
        public int Skipped;

        /// <summary>Number of services in the scope.</summary>
        public int Total;

        /// <summary>Every service of the scope, in graph order.</summary>
        public List<DashboardService> Services = new List<DashboardService>();
    }

    /// <summary>
    /// Everything the dashboard shows for one host at one instant. Plain serializable data: it is built
    /// from a live host, kept for a single refresh, and stored in <see cref="SessionState"/> so the
    /// "Last run" tab survives Play Mode exits and domain reloads.
    /// </summary>
    [Serializable]
    internal sealed class DashboardSnapshot
    {
        /// <summary>Label of the host the snapshot was taken from, for example "host #1 (gen 2)".</summary>
        public string HostLabel = string.Empty;

        /// <summary>Version of com.praxeum.runtimeflow at capture time.</summary>
        public string PackageVersion = "?";

        /// <summary>Unity version at capture time.</summary>
        public string UnityVersion = string.Empty;

        /// <summary>Capture time, round-trip UTC format.</summary>
        public string CapturedUtc = string.Empty;

        /// <summary>Aggregated state of the host.</summary>
        public RunState State;

        /// <summary>Name of <see cref="State"/>, so the diagnostics JSON reads without the enum at hand.</summary>
        public string StateName = string.Empty;

        /// <summary>Weighted completion percentage across every scope, 0..100.</summary>
        public double Percent;

        /// <summary>Elapsed time of the leading run, in milliseconds.</summary>
        public double ElapsedMs;

        /// <summary>Number of session restarts.</summary>
        public int RestartCount;

        /// <summary>Current generation of the session scope.</summary>
        public int Generation;

        /// <summary>True when the host has a session scope that can be restarted.</summary>
        public bool CanRestart;

        /// <summary>True when the snapshot was taken from a live host rather than read back from storage.</summary>
        public bool Live;

        /// <summary>Halt reason of the run, or an empty string.</summary>
        public string HaltReason = string.Empty;

        /// <summary>Failure of the host, including a failed scope build with no service rows.</summary>
        public string ErrorType = string.Empty;
        public string ErrorMessage = string.Empty;
        public string ErrorStack = string.Empty;
        public bool HasError => !string.IsNullOrEmpty(ErrorType);

        /// <summary>Rendering of both graphs, as returned by <see cref="RuntimeFlowHost.Describe"/>.</summary>
        public string Describe = string.Empty;

        /// <summary>Scopes of the host: global first, then the session, then the child runs.</summary>
        public List<DashboardScope> Scopes = new List<DashboardScope>();

        /// <summary>Total number of service rows across every scope.</summary>
        public int ServiceCount
        {
            get
            {
                var count = 0;
                foreach (var scope in Scopes) count += scope.Services.Count;
                return count;
            }
        }

        /// <summary>Finds a service row by its captured node identity, or null.</summary>
        /// <param name="id">Identity of the service.</param>
        public DashboardService? Find(string id)
        {
            foreach (var scope in Scopes)
            {
                foreach (var service in scope.Services)
                {
                    if (service.Id == id) return service;
                }
            }
            return null;
        }

        /// <summary>Normalizes missing serialized fields and assigns identities to older stored snapshots.</summary>
        public void EnsureIdentities()
        {
            // JsonUtility deserialization does not run field initializers for fields absent from old JSON.
            HostLabel ??= string.Empty;
            PackageVersion ??= "?";
            UnityVersion ??= string.Empty;
            CapturedUtc ??= string.Empty;
            StateName ??= State.ToString();
            HaltReason ??= string.Empty;
            ErrorType ??= string.Empty;
            ErrorMessage ??= string.Empty;
            ErrorStack ??= string.Empty;
            Describe ??= string.Empty;
            Scopes ??= new List<DashboardScope>();
            for (var i = 0; i < Scopes.Count; i++)
            {
                var scope = Scopes[i];
                scope.Name ??= string.Empty;
                scope.StateName ??= scope.State.ToString();
                scope.Services ??= new List<DashboardService>();
                if (string.IsNullOrEmpty(scope.Id)) scope.Id = "stored-scope:" + i.ToString(CultureInfo.InvariantCulture);
                for (var j = 0; j < scope.Services.Count; j++)
                {
                    var service = scope.Services[j];
                    service.Name ??= string.Empty;
                    service.Scope ??= scope.Name;
                    service.Phase ??= string.Empty;
                    service.StateName ??= service.State.ToString();
                    service.Dependencies ??= new List<string>();
                    service.DependencyIds ??= new List<string>();
                    service.WaitingOn ??= new List<string>();
                    service.ErrorType ??= string.Empty;
                    service.ErrorMessage ??= string.Empty;
                    service.ErrorStack ??= string.Empty;
                    if (string.IsNullOrEmpty(service.Id))
                        service.Id = scope.Id + "/node:" + j.ToString(CultureInfo.InvariantCulture);
                }
            }
        }
    }

    /// <summary>Keeps a dashboard selection attached to a host without retaining that host.</summary>
    internal sealed class DashboardHostSelection
    {
        private WeakReference<RuntimeFlowHost>? _selected;

        public void Select(RuntimeFlowHost host) => _selected = new WeakReference<RuntimeFlowHost>(host);

        /// <summary>
        /// Resolves the selected identity in a fresh registry snapshot. Initially selects the first host;
        /// when a selected host disappears, no other host becomes a destructive-action target implicitly.
        /// </summary>
        public RuntimeFlowHost? Resolve(IReadOnlyList<RuntimeFlowHost> hosts, out int index)
        {
            index = -1;
            if (_selected == null && hosts.Count > 0) Select(hosts[0]);
            if (_selected == null || !_selected.TryGetTarget(out var selected)) return null;
            for (var i = 0; i < hosts.Count; i++)
            {
                if (!ReferenceEquals(hosts[i], selected)) continue;
                index = i;
                return selected;
            }
            return null;
        }
    }

    /// <summary>
    /// Reads the live hosts out of the runtime registry and turns one of them into a
    /// <see cref="DashboardSnapshot"/>. Host references are never kept: every refresh asks the registry
    /// again, because the registry holds weak references and prunes them on read.
    /// </summary>
    internal static class DashboardData
    {
        /// <summary>Key the last snapshot is stored under in <see cref="SessionState"/>.</summary>
        public const string LastRunKey = "RuntimeFlow.Dashboard.LastRun";

        private static string? _packageVersion;
        private static readonly ConditionalWeakTable<ScopeRun, RunIdentity> RunIdentities =
            new ConditionalWeakTable<ScopeRun, RunIdentity>();
        private static long _nextRunIdentity;

        private sealed class RunIdentity
        {
            // The value holds only a string: the weak key does not keep runs, graphs or services alive.
            public readonly string Id = "run:" + Interlocked.Increment(ref _nextRunIdentity).ToString(CultureInfo.InvariantCulture);
        }

        private static string IdentityOf(ScopeRun run) => RunIdentities.GetValue(run, _ => new RunIdentity()).Id;

        /// <summary>Hosts that are alive right now, oldest first.</summary>
        public static IReadOnlyList<RuntimeFlowHost> LiveHosts => FlowRegistry.Live;

        /// <summary>Version of the package this assembly ships in, or "?" when it cannot be determined.</summary>
        public static string PackageVersion =>
            _packageVersion ??= UnityEditor.PackageManager.PackageInfo
                .FindForAssembly(typeof(DashboardData).Assembly)?.version ?? "?";

        /// <summary>Label of a host in the toolbar dropdown, for example "host #1 (gen 2)".</summary>
        /// <param name="index">Zero-based position of the host in <see cref="LiveHosts"/>.</param>
        /// <param name="host">The host to label.</param>
        public static string Label(int index, RuntimeFlowHost host)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            return string.Format(
                CultureInfo.InvariantCulture, "host #{0} (gen {1})", index + 1, host.Generation);
        }

        /// <summary>
        /// Builds the view model of one host: the aggregated status, the description of both graphs and
        /// one <see cref="DashboardScope"/> per run (global, session, then every child run).
        /// </summary>
        /// <param name="host">A host obtained from <see cref="LiveHosts"/> during this refresh.</param>
        /// <param name="index">Its position in <see cref="LiveHosts"/>, used for the label.</param>
        public static DashboardSnapshot Capture(RuntimeFlowHost host, int index)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));

            var aggregate = host.GetStatus();
            var snapshot = new DashboardSnapshot
            {
                HostLabel = Label(index, host),
                PackageVersion = PackageVersion,
                UnityVersion = Application.unityVersion,
                CapturedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                State = aggregate.State,
                StateName = aggregate.State.ToString(),
                Percent = Finite(aggregate.Percent),
                ElapsedMs = Finite(aggregate.Elapsed.TotalMilliseconds),
                RestartCount = host.RestartCount,
                Generation = host.Generation,
                CanRestart = host.CanRestart,
                Live = true,
                HaltReason = aggregate.HaltReason ?? string.Empty,
                ErrorType = aggregate.Error?.GetType().Name ?? string.Empty,
                ErrorMessage = aggregate.Error?.Message ?? string.Empty,
                ErrorStack = aggregate.Error?.ToString() ?? string.Empty,
                Describe = host.Describe()
            };

            var runs = new List<ScopeRun>();
            if (host.GlobalRun != null) runs.Add(host.GlobalRun);
            if (host.SessionRun != null) runs.Add(host.SessionRun);
            runs.AddRange(host.ChildRuns);
            var nodes = new Dictionary<ServiceNode, string>();
            foreach (var run in runs)
            {
                var scope = CaptureScope(run)!;
                snapshot.Scopes.Add(scope);
                for (var i = 0; i < run.Graph.Services.Count; i++)
                    nodes[run.Graph.Services[i]] = scope.Services[i].Id;
            }
            for (var r = 0; r < runs.Count; r++)
            {
                var run = runs[r];
                for (var i = 0; i < run.Graph.Services.Count; i++)
                {
                    foreach (var edge in run.Graph.Services[i].Deps)
                    {
                        var target = edge.Target.Source ?? edge.Target;
                        snapshot.Scopes[r].Services[i].DependencyIds.Add(
                            nodes.TryGetValue(target, out var id) ? id : string.Empty);
                    }
                }
            }
            return snapshot;
        }

        /// <summary>Converts one run into a scope row; a null run contributes nothing.</summary>
        /// <param name="run">The run to read, or null.</param>
        public static DashboardScope? CaptureScope(ScopeRun? run)
        {
            if (run == null) return null;
            var status = run.GetStatus();
            var scope = new DashboardScope
            {
                Id = IdentityOf(run),
                Name = status.Scope,
                State = status.State,
                StateName = status.State.ToString(),
                Percent = Finite(status.Percent),
                ElapsedMs = Finite(status.Elapsed.TotalMilliseconds),
                RestartCount = status.RestartCount,
                Total = status.Services.Count
            };

            for (var i = 0; i < status.Services.Count; i++)
            {
                var service = status.Services[i];
                var row = Convert(service);
                row.Id = scope.Id + "/node:" + run.Graph.Services[i].Index.ToString(CultureInfo.InvariantCulture);
                scope.Services.Add(row);
                switch (service.State)
                {
                    case ServiceState.Completed:
                    case ServiceState.Degraded:
                        scope.Done++;
                        break;
                    case ServiceState.Failed:
                    case ServiceState.Cancelled:
                        scope.Failed++;
                        break;
                    case ServiceState.Pending:
                    case ServiceState.Skipped:
                        scope.Skipped++;
                        break;
                }
            }

            return scope;
        }

        /// <summary>Stores a snapshot so the "Last run" tab can show it after Play Mode ends.</summary>
        /// <param name="snapshot">The snapshot to keep; null clears the stored one.</param>
        public static void SaveLastRun(DashboardSnapshot? snapshot)
        {
            if (snapshot == null)
            {
                ClearLastRun();
                return;
            }
            SessionState.SetString(LastRunKey, JsonUtility.ToJson(snapshot));
        }

        /// <summary>Reads back the stored snapshot, or null when there is none or it cannot be parsed.</summary>
        public static DashboardSnapshot? LoadLastRun()
        {
            var json = SessionState.GetString(LastRunKey, string.Empty);
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var snapshot = JsonUtility.FromJson<DashboardSnapshot>(json);
                if (snapshot == null) return null;
                snapshot.EnsureIdentities();
                snapshot.Live = false;
                return snapshot;
            }
            catch (ArgumentException)
            {
                // Stored by an older layout of the model; drop it rather than nagging the user.
                ClearLastRun();
                return null;
            }
        }

        /// <summary>The last persisted run takes precedence; current data is a fallback before any run was stored.</summary>
        public static DashboardSnapshot? LastRunOrLive(DashboardSnapshot? live)
            => LoadLastRun() ?? (live?.Live == true ? live : null);

        /// <summary>Forgets the stored snapshot.</summary>
        public static void ClearLastRun() => SessionState.EraseString(LastRunKey);

        /// <summary>
        /// Replaces a non-finite number with zero. A scope whose services all carry <c>Weight = 0</c>
        /// yields a NaN percentage, and <see cref="JsonUtility"/> would write it as a bare <c>NaN</c>,
        /// which is not valid JSON — so nothing non-finite is ever stored in a snapshot.
        /// </summary>
        /// <param name="value">The raw number from a status snapshot.</param>
        private static double Finite(double value) => double.IsNaN(value) || double.IsInfinity(value) ? 0.0 : value;

        private static DashboardService Convert(ServiceStatus status)
        {
            var service = new DashboardService
            {
                Name = status.Name,
                Scope = status.Scope,
                Phase = status.Phase ?? string.Empty,
                State = status.State,
                StateName = status.State.ToString(),
                Optional = status.Optional,
                UserGated = status.UserGated,
                AwaitingPlayer = status.AwaitingPlayer,
                ElapsedMs = Finite(status.Elapsed.TotalMilliseconds),
                Progress = (float)Finite(status.Progress),
                Weight = Finite(status.Weight)
            };

            service.Dependencies.AddRange(status.Dependencies);
            service.WaitingOn.AddRange(status.WaitingOn);

            if (status.Error != null)
            {
                service.ErrorType = status.Error.GetType().Name;
                service.ErrorMessage = status.Error.Message;
                service.ErrorStack = status.Error.StackTrace ?? string.Empty;
            }

            return service;
        }
    }
}
