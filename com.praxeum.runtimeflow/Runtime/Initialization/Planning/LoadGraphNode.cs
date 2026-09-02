using System;
using System.Collections.Generic;

namespace RuntimeFlow.Initialization.Planning
{
    /// <summary>Kind of a node in the load graph.</summary>
    public enum LoadGraphNodeKind
    {
        /// <summary>VContainer entry-point phase (IInitializable/IStartable stage runner).</summary>
        EntryPoint = 0,

        /// <summary><see cref="IGlobalBootstrapOperation"/> executed in priority order.</summary>
        GlobalBootstrapOperation = 1,

        /// <summary>A registered async-initializable service (wave participant).</summary>
        Initializer = 2,

        /// <summary>An auto-constructed service built before waves run.</summary>
        AutoConstruct = 3,
    }

    /// <summary>
    /// One node of the load graph: a unit of loading work with typed dependencies on other
    /// nodes. Keys are canonical service types; delegate content sources participate as
    /// Initializer nodes keyed by their concrete class.
    /// </summary>
    internal sealed class LoadGraphNode
    {
        public LoadGraphNode(
            Type key,
            string displayName,
            LoadGraphNodeKind kind,
            IReadOnlyCollection<Type> dependencies,
            double weight = 1.0)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key));
            DisplayName = displayName ?? key.Name;
            Kind = kind;
            Dependencies = dependencies;
            Weight = weight > 0 ? weight : 1.0;
        }

        /// <summary>Canonical node identity: usually the service type.</summary>
        public Type Key { get; }

        public string DisplayName { get; }

        public LoadGraphNodeKind Kind { get; }

        /// <summary>Keys of nodes that must complete before this one starts.</summary>
        public IReadOnlyCollection<Type> Dependencies { get; }

        /// <summary>Progress weight (phase 2). Defaults to 1.</summary>
        public double Weight { get; }

        public bool HasDependencyOn(Type key)
        {
            foreach (var dep in Dependencies)
                if (dep == key) return true;
            return false;
        }
    }

    /// <summary>
    /// Result of building a load graph for one scope: ordered nodes plus precomputed layers
    /// and diagnostics that composition-time validation reports to the developer.
    /// </summary>
    internal sealed class LoadGraphPlan
    {
        public LoadGraphPlan(
            IReadOnlyList<LoadGraphNode> nodes,
            IReadOnlyList<IReadOnlyList<LoadGraphNode>> layers,
            IReadOnlyDictionary<Type, LoadGraphNode> byKey)
        {
            Nodes = nodes;
            Layers = layers;
            ByKey = byKey;
        }

        public IReadOnlyList<LoadGraphNode> Nodes { get; }

        /// <summary>Topological layers: layer 0 has no in-plan dependencies, layer N depends only on earlier layers.</summary>
        public IReadOnlyList<IReadOnlyList<LoadGraphNode>> Layers { get; }

        public IReadOnlyDictionary<Type, LoadGraphNode> ByKey { get; }

        public int TotalNodeCount => Nodes.Count;
    }
}
