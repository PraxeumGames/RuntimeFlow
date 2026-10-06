using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Internal
{
    /// <summary>What a graph node stands for.</summary>
    internal enum NodeKind
    {
        /// <summary>A service registered in this scope; scheduled and initialized.</summary>
        Service,

        /// <summary>A synthetic phase barrier; completes when every service of its phase is done.</summary>
        Barrier,

        /// <summary>A service of a parent scope; participates in edges but is already initialized.</summary>
        External
    }

    /// <summary>A dependency edge with the reason it exists, used by diagnostics.</summary>
    internal readonly struct Edge
    {
        public Edge(ServiceNode target, string origin)
        {
            Target = target;
            Origin = origin;
        }

        /// <summary>The node that must finish first.</summary>
        public ServiceNode Target { get; }

        /// <summary>Human-readable origin, for example "ctor: IFoo foo", "DependsOn" or "phase barrier".</summary>
        public string Origin { get; }
    }

    /// <summary>One node of a scope's initialization graph: its metadata, edges and run state.</summary>
    internal sealed class ServiceNode
    {
        public ServiceNode(int index, NodeKind kind, string name, Type type, string scope)
        {
            Index = index;
            Kind = kind;
            Name = name;
            Type = type;
            Scope = scope;
        }

        public int Index { get; }
        public NodeKind Kind { get; }
        public string Name { get; set; }
        public Type Type { get; }
        public string Scope { get; }

        public object? Instance { get; set; }

        /// <summary>The VContainer registration the node was discovered from; edges are resolved against it.</summary>
        public VContainer.Registration? Registration { get; set; }
        public IAsyncInitializable? Service { get; set; }
        public InitContext? Context { get; set; }

        public string? Phase { get; set; }
        public int PhaseIndex { get; set; } = -1;
        public bool Optional { get; set; }
        public bool UserGated { get; set; }
        public double TimeoutSeconds { get; set; }
        public double Weight { get; set; } = 1.0;

        public List<Edge> Deps { get; } = new List<Edge>();

        /// <summary>Targets of <see cref="Deps"/>, so adding an edge checks for a duplicate in O(1).</summary>
        public HashSet<ServiceNode> DepTargets { get; } = new HashSet<ServiceNode>();

        /// <summary>
        /// For an external node built from a linked parent run: the parent's own node, whose state is read
        /// again when this run starts (the parent may have finished after this graph was built).
        /// </summary>
        public ServiceNode? Source { get; set; }

        /// <summary>The last status snapshot of a terminal node; a terminal node never changes again.</summary>
        public ServiceStatus? CachedStatus { get; set; }

        /// <summary>True once teardown reported the node as still running past the grace.</summary>
        public bool AbandonReported { get; set; }
        public List<ServiceNode> Dependents { get; } = new List<ServiceNode>();
        public List<string> LazyParameters { get; } = new List<string>();

        public int PendingDeps { get; set; }
        public ServiceState State { get; set; } = ServiceState.Pending;
        public Stopwatch Clock { get; } = new Stopwatch();
        public float Progress { get; set; }
        public CancellationTokenSource? Cts { get; set; }
        public Task? Task { get; set; }

        /// <summary>True once the raw task's outcome was processed, by its observation or a watch tick.</summary>
        public bool OutcomeObserved { get; set; }

        /// <summary>Meaning of cancellation for a raw task already complete before a stop or freeze.</summary>
        public bool? ExpectedCancellation { get; set; }

        /// <summary>The scheduler's bookkeeping of <see cref="Task"/>; awaited by teardown while the node is in flight.</summary>
        public Task? Observation { get; set; }
        public Exception? Error { get; set; }

        /// <summary>
        /// Failure captured while constructing the instance: a required node fails the run before anything
        /// starts, an optional one degrades once its own dependencies are done.
        /// </summary>
        public Exception? ConstructionError { get; set; }

        /// <summary>True once the node reached a state it can no longer leave.</summary>
        public bool IsTerminal => State != ServiceState.Pending && State != ServiceState.Running;

        /// <summary>True when the node counts as finished for its dependents.</summary>
        public bool IsSatisfied => State == ServiceState.Completed || State == ServiceState.Degraded;

        /// <summary>Name used in messages; barriers render as the phase they guard.</summary>
        public string DisplayName => Kind == NodeKind.Barrier ? $"phase '{Phase}'" : Name;

        private IReadOnlyList<string>? _dependencyNames;

        /// <summary>
        /// Display names of every dependency, in graph order. Edges never change once the graph is built, so
        /// the list is computed once and shared by every status snapshot.
        /// </summary>
        public IReadOnlyList<string> DependencyNames
        {
            get
            {
                if (_dependencyNames != null) return _dependencyNames;
                var names = new string[Deps.Count];
                for (var i = 0; i < names.Length; i++) names[i] = Deps[i].Target.DisplayName;
                return _dependencyNames = names;
            }
        }

        /// <summary>Dependencies that have not finished yet, in graph order; a shared empty list when none.</summary>
        public IReadOnlyList<string> UnmetDependencies()
        {
            List<string>? result = null;
            foreach (var edge in Deps)
            {
                if (!edge.Target.IsSatisfied)
                    (result ??= new List<string>()).Add(edge.Target.DisplayName);
            }
            return result ?? (IReadOnlyList<string>)Array.Empty<string>();
        }

        /// <inheritdoc />
        public override string ToString() => $"{Index}:{Name} ({State})";
    }

    /// <summary>Culture-invariant formatting helpers shared by the graph and the scheduler.</summary>
    internal static class Fmt
    {
        /// <summary>Formats a duration with one decimal, for example "3.2s".</summary>
        public static string S1(TimeSpan value)
            => value.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

        /// <summary>Formats a duration in seconds with one decimal, for example "3.2s".</summary>
        public static string S1(double seconds)
            => seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

        /// <summary>Lower-case state name used in messages, for example "degraded".</summary>
        public static string State(ServiceState state) => state.ToString().ToLowerInvariant();

        /// <summary>Formats a duration with two decimals, for example "3.21s".</summary>
        public static string S2(TimeSpan value)
            => value.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + "s";

        /// <summary>Formats a number with at most two decimals, for example "30" or "2.5".</summary>
        public static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>Formats a multiplier with at least one decimal, for example "1.0".</summary>
        public static string Mul(double value) => value.ToString("0.0#", CultureInfo.InvariantCulture);

        /// <summary>Joins names, capping the list at <paramref name="cap"/> and appending "and N more".</summary>
        public static string Capped(IReadOnlyList<string> items, int cap)
        {
            if (items.Count <= cap) return string.Join(", ", items);
            var head = new string[cap];
            for (var i = 0; i < cap; i++) head[i] = items[i];
            return string.Join(", ", head) + " and " + (items.Count - cap).ToString(CultureInfo.InvariantCulture) + " more";
        }

        /// <summary>Renders a type the way a C# signature would, for example "IReadOnlyList&lt;IFoo&gt;".</summary>
        public static string Type(Type type)
        {
            if (type.IsArray) return Type(type.GetElementType()!) + "[]";
            if (!type.IsGenericType) return type.Name;

            var name = type.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0) name = name.Substring(0, tick);
            var args = type.GetGenericArguments();
            var rendered = new string[args.Length];
            for (var i = 0; i < args.Length; i++) rendered[i] = Type(args[i]);
            return name + "<" + string.Join(", ", rendered) + ">";
        }
    }
}
