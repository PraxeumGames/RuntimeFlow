using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace RuntimeFlow.Editor
{
    /// <summary>
    /// Renders a <see cref="DashboardSnapshot"/> as JSON for bug reports: one object per host, scope and
    /// service, every failure with its type, message and stack, plus the graph description. Hand-rolled
    /// so the shape stays readable and stable; every string goes through <see cref="Escape"/>.
    /// </summary>
    public static class DiagnosticsJson
    {
        /// <summary>Builds the diagnostics document of one snapshot.</summary>
        /// <param name="snapshot">The snapshot to render; must not be null.</param>
        public static string Build(DashboardSnapshot snapshot)
        {
            if (snapshot == null) return "{}";

            var json = new StringBuilder(4096);
            json.Append("{\n");
            Field(json, 1, "timestampUtc", snapshot.CapturedUtc, true);
            Field(json, 1, "packageVersion", snapshot.PackageVersion, true);
            Field(json, 1, "unityVersion", snapshot.UnityVersion, true);
            Field(json, 1, "host", snapshot.HostLabel, true);
            Field(json, 1, "state", snapshot.State.ToString(), true);
            Raw(json, 1, "percent", Number(snapshot.Percent));
            Raw(json, 1, "elapsedMs", Number(snapshot.ElapsedMs));
            Raw(json, 1, "restartCount", snapshot.RestartCount.ToString(CultureInfo.InvariantCulture));
            Raw(json, 1, "generation", snapshot.Generation.ToString(CultureInfo.InvariantCulture));
            Field(json, 1, "haltReason", snapshot.HaltReason, true);

            json.Append("  \"scopes\": [");
            for (var i = 0; i < snapshot.Scopes.Count; i++)
            {
                if (i > 0) json.Append(',');
                AppendScope(json, snapshot.Scopes[i]);
            }
            json.Append(snapshot.Scopes.Count > 0 ? "\n  ],\n" : "],\n");

            Field(json, 1, "describe", snapshot.Describe, false);
            json.Append("}\n");
            return json.ToString();
        }

        /// <summary>
        /// Builds the document, puts it on the system clipboard and logs a one-line confirmation.
        /// </summary>
        /// <param name="snapshot">The snapshot to copy.</param>
        /// <returns>The JSON that was copied.</returns>
        public static string CopyToClipboard(DashboardSnapshot snapshot)
        {
            var json = Build(snapshot);
            EditorGUIUtility.systemCopyBuffer = json;
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[RuntimeFlow] copied diagnostics JSON to the clipboard: {0}, {1} scope(s), {2} service(s), {3} characters.",
                snapshot?.HostLabel ?? "no host", snapshot?.Scopes.Count ?? 0, snapshot?.ServiceCount ?? 0, json.Length));
            return json;
        }

        /// <summary>Copies arbitrary text to the clipboard and logs a one-line confirmation.</summary>
        /// <param name="what">Short description of the payload, used in the log line.</param>
        /// <param name="text">The text to copy.</param>
        public static void CopyText(string what, string text)
        {
            EditorGUIUtility.systemCopyBuffer = text ?? string.Empty;
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[RuntimeFlow] copied {0} to the clipboard: {1} characters.", what, text?.Length ?? 0));
        }

        /// <summary>Escapes a string for a JSON document: quotes, backslashes and control characters.</summary>
        /// <param name="value">The raw text; null becomes an empty string.</param>
        public static string Escape(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var text = new StringBuilder(value!.Length + 8);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    case '\b': text.Append("\\b"); break;
                    case '\f': text.Append("\\f"); break;
                    default:
                        if (c < ' ') text.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                        else text.Append(c);
                        break;
                }
            }
            return text.ToString();
        }

        private static void AppendScope(StringBuilder json, DashboardScope scope)
        {
            json.Append("\n    {\n");
            Field(json, 3, "name", scope.Name, true);
            Field(json, 3, "state", scope.State.ToString(), true);
            Raw(json, 3, "percent", Number(scope.Percent));
            Raw(json, 3, "elapsedMs", Number(scope.ElapsedMs));
            Raw(json, 3, "restartCount", scope.RestartCount.ToString(CultureInfo.InvariantCulture));
            Raw(json, 3, "done", scope.Done.ToString(CultureInfo.InvariantCulture));
            Raw(json, 3, "failed", scope.Failed.ToString(CultureInfo.InvariantCulture));
            Raw(json, 3, "skipped", scope.Skipped.ToString(CultureInfo.InvariantCulture));
            Raw(json, 3, "total", scope.Total.ToString(CultureInfo.InvariantCulture));

            json.Append("      \"services\": [");
            for (var i = 0; i < scope.Services.Count; i++)
            {
                if (i > 0) json.Append(',');
                AppendService(json, scope.Services[i]);
            }
            json.Append(scope.Services.Count > 0 ? "\n      ]\n    }" : "]\n    }");
        }

        private static void AppendService(StringBuilder json, DashboardService service)
        {
            json.Append("\n        {\n");
            Field(json, 5, "name", service.Name, true);
            Field(json, 5, "scope", service.Scope, true);
            Field(json, 5, "phase", service.Phase, true);
            Field(json, 5, "state", service.State.ToString(), true);
            Raw(json, 5, "optional", Bool(service.Optional));
            Raw(json, 5, "userGated", Bool(service.UserGated));
            Raw(json, 5, "awaitingPlayer", Bool(service.AwaitingPlayer));
            Raw(json, 5, "elapsedMs", Number(service.ElapsedMs));
            Raw(json, 5, "progress", Number(service.Progress));
            Raw(json, 5, "weight", Number(service.Weight));
            Raw(json, 5, "dependencies", Array(service.Dependencies));
            Raw(json, 5, "waitingOn", Array(service.WaitingOn));

            Indent(json, 5).Append("\"error\": ");
            if (!service.HasError)
            {
                json.Append("null\n");
            }
            else
            {
                json.Append("{\n");
                Field(json, 6, "type", service.ErrorType, true);
                Field(json, 6, "message", service.ErrorMessage, true);
                Field(json, 6, "stack", service.ErrorStack, false);
                Indent(json, 5).Append("}\n");
            }

            json.Append("        }");
        }

        private static string Array(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0) return "[]";
            var text = new StringBuilder("[");
            for (var i = 0; i < values.Count; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append('"').Append(Escape(values[i])).Append('"');
            }
            return text.Append(']').ToString();
        }

        private static void Field(StringBuilder json, int indent, string name, string? value, bool comma)
        {
            Indent(json, indent).Append('"').Append(name).Append("\": \"").Append(Escape(value)).Append('"');
            json.Append(comma ? ",\n" : "\n");
        }

        private static void Raw(StringBuilder json, int indent, string name, string value)
            => Indent(json, indent).Append('"').Append(name).Append("\": ").Append(value).Append(",\n");

        private static StringBuilder Indent(StringBuilder json, int levels)
            => json.Append(' ', levels * 2);

        private static string Number(double value)
            => value.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Bool(bool value) => value ? "true" : "false";
    }
}
