using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RuntimeFlow.Internal
{
    /// <summary>
    /// Renders a scope's graph as an aligned, copy-pasteable table: one row per service with its flags,
    /// one indented row per edge with the reason it exists, and the parent-scope services it can see.
    /// </summary>
    internal static class GraphDescriber
    {
        /// <summary>Produces the human-readable description of <paramref name="graph"/>.</summary>
        public static string Describe(ServiceGraph graph)
        {
            var text = new StringBuilder();
            text.Append("scope '").Append(graph.Scope).Append("' — ")
                .Append(graph.Services.Count.ToString(CultureInfo.InvariantCulture)).Append(" services");
            if (graph.Phases.Count > 0)
                text.Append(", phases: ").Append(string.Join(" > ", graph.Phases));
            text.AppendLine();

            var phaseWidth = 0;
            var nameWidth = 0;
            var afterWidth = 0;
            foreach (var node in graph.Services)
            {
                phaseWidth = Math.Max(phaseWidth, PhaseCell(node).Length);
                nameWidth = Math.Max(nameWidth, node.Name.Length);
                foreach (var edge in node.Deps)
                    afterWidth = Math.Max(afterWidth, edge.Target.DisplayName.Length);
            }

            for (var i = 0; i < graph.Services.Count; i++)
            {
                var node = graph.Services[i];
                text.Append((i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(2))
                    .Append(' ').Append(PhaseCell(node).PadRight(phaseWidth))
                    .Append(' ').Append(node.Name.PadRight(nameWidth))
                    .Append("  ").Append(string.Join(", ", Flags(node)))
                    .AppendLine();

                foreach (var edge in node.Deps)
                {
                    text.Append("      after ").Append(edge.Target.DisplayName.PadRight(afterWidth))
                        .Append("  (").Append(edge.Origin).Append(')');
                    if (edge.Target.Kind == NodeKind.External)
                        text.Append(' ').Append(ExternalTag(edge.Target));
                    text.AppendLine();
                }

                foreach (var lazy in node.LazyParameters)
                    text.Append("      lazy: ").Append(lazy).AppendLine();
            }

            if (graph.Externals.Count > 0)
            {
                text.Append("external (from parent scopes): ")
                    .Append(string.Join(", ", graph.Externals.Select(n => $"{n.Name} {ExternalTag(n)}")))
                    .AppendLine();
            }

            return text.ToString();
        }

        /// <summary>
        /// Scope and state of an external node, for example "[global, initialized]", "[global, degraded]" or,
        /// for a parent service its run never initialized, "[global, skipped]".
        /// </summary>
        private static string ExternalTag(ServiceNode node)
            => "[" + node.Scope + ", " + (node.State == ServiceState.Degraded ? "degraded"
                : node.State == ServiceState.Completed ? "initialized"
                : Fmt.State(node.State)) + "]";

        private static string PhaseCell(ServiceNode node) => node.Phase == null ? "[-]" : "[" + node.Phase + "]";

        private static IEnumerable<string> Flags(ServiceNode node)
        {
            yield return node.Optional ? "optional" : "required";
            if (node.UserGated) yield return "user-gated";
            if (node.TimeoutSeconds > 0) yield return "timeout " + Fmt.N(node.TimeoutSeconds) + "s";
            yield return "weight " + Fmt.N(node.Weight);
        }
    }
}
