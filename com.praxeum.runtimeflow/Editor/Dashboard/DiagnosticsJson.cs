using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace RuntimeFlow.Editor
{
    /// <summary>
    /// Turns a <see cref="DashboardSnapshot"/> into the text a bug report carries: every scope, every
    /// service, every failure with its type, message and stack, plus the graph description.
    /// <para>
    /// The document is produced by <see cref="JsonUtility"/> over the snapshot model itself, which is
    /// already <c>[Serializable]</c> because the "Last run" tab stores it in <see cref="SessionState"/>.
    /// Unity owns the escaping, so quotes, backslashes and newlines inside an exception message cannot
    /// break the document, and the shape can never drift from the model. Field names are therefore the
    /// model's own; states appear both as the enum ordinal (<c>State</c>) and as text
    /// (<c>StateName</c>), since a bug report has to be readable without the enum at hand.
    /// </para>
    /// </summary>
    internal static class DiagnosticsJson
    {
        /// <summary>Builds the diagnostics document of one snapshot; a null snapshot renders as "{}".</summary>
        /// <param name="snapshot">The snapshot to render, or null.</param>
        public static string Build(DashboardSnapshot? snapshot)
            => snapshot == null ? "{}" : JsonUtility.ToJson(snapshot, true);

        /// <summary>
        /// Builds the document, puts it on the system clipboard and logs a one-line confirmation.
        /// </summary>
        /// <param name="snapshot">The snapshot to copy.</param>
        /// <returns>The JSON that was copied.</returns>
        public static string CopyToClipboard(DashboardSnapshot? snapshot)
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
        public static void CopyText(string what, string? text)
        {
            EditorGUIUtility.systemCopyBuffer = text ?? string.Empty;
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[RuntimeFlow] copied {0} to the clipboard: {1} characters.", what, text?.Length ?? 0));
        }
    }
}
