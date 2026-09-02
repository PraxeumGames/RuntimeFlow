using System;
using System.Globalization;
using UnityEngine.UIElements;

namespace RuntimeFlow.Editor
{
    /// <summary>Small builders every dashboard view shares: cards, property rows, state badges, formats.</summary>
    public static class ViewHelpers
    {
        /// <summary>A card with a title label; add the content to the returned element.</summary>
        /// <param name="title">Text of the card header.</param>
        public static VisualElement Card(string title)
        {
            var card = new VisualElement();
            card.AddToClassList("rf-card");
            var label = new Label(title);
            label.AddToClassList("rf-card-title");
            card.Add(label);
            return card;
        }

        /// <summary>A label/value line inside a card.</summary>
        /// <param name="label">Left-hand caption.</param>
        /// <param name="value">Right-hand value.</param>
        public static VisualElement PropertyRow(string label, string value)
        {
            var row = new VisualElement();
            row.AddToClassList("rf-property-row");

            var caption = new Label(label);
            caption.AddToClassList("rf-property-label");

            var text = new Label(value);
            text.AddToClassList("rf-property-value");

            row.Add(caption);
            row.Add(text);
            return row;
        }

        /// <summary>A colored badge naming a service state.</summary>
        /// <param name="state">The state to render.</param>
        public static Label StateBadge(ServiceState state)
        {
            var badge = new Label(state.ToString().ToLowerInvariant());
            badge.AddToClassList("rf-badge");
            badge.AddToClassList(StateClass(state));
            return badge;
        }

        /// <summary>USS modifier class of a service state, for example <c>rf-state--running</c>.</summary>
        /// <param name="state">The state to map.</param>
        public static string StateClass(ServiceState state) => state switch
        {
            ServiceState.Pending => "rf-state--pending",
            ServiceState.Running => "rf-state--running",
            ServiceState.Completed => "rf-state--completed",
            ServiceState.Degraded => "rf-state--degraded",
            ServiceState.Failed => "rf-state--failed",
            ServiceState.Cancelled => "rf-state--cancelled",
            ServiceState.Skipped => "rf-state--skipped",
            _ => "rf-state--pending"
        };

        /// <summary>USS modifier class of a service row, taking "awaiting player" into account.</summary>
        /// <param name="service">The row to map.</param>
        public static string StateClass(DashboardService service)
            => service.AwaitingPlayer && service.State == ServiceState.Running
                ? "rf-state--awaiting"
                : StateClass(service.State);

        /// <summary>Badge text of a service row: "awaiting" replaces "running" for user-gated services.</summary>
        /// <param name="service">The row to name.</param>
        public static string StateText(DashboardService service)
            => service.AwaitingPlayer && service.State == ServiceState.Running
                ? "awaiting"
                : service.State.ToString().ToLowerInvariant();

        /// <summary>A badge for a service row, using <see cref="StateText"/> and <see cref="StateClass(DashboardService)"/>.</summary>
        /// <param name="service">The row to render.</param>
        public static Label StateBadge(DashboardService service)
        {
            var badge = new Label(StateText(service));
            badge.AddToClassList("rf-badge");
            badge.AddToClassList(StateClass(service));
            return badge;
        }

        /// <summary>Milliseconds as seconds with two decimals, for example "1.25 s".</summary>
        /// <param name="milliseconds">Duration in milliseconds.</param>
        public static string Seconds(double milliseconds)
            => (milliseconds / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " s";

        /// <summary>A percentage with one decimal, for example "83.3 %".</summary>
        /// <param name="percent">Value in the 0..100 range.</param>
        public static string Percent(double percent)
            => percent.ToString("0.0", CultureInfo.InvariantCulture) + " %";

        /// <summary>A read-only, selectable multiline field, used for stack traces and descriptions.</summary>
        /// <param name="value">Text to show.</param>
        /// <param name="className">Extra USS class for the field.</param>
        public static TextField SelectableText(string value, string className)
        {
            var field = new TextField { multiline = true, isReadOnly = true, value = value ?? string.Empty };
            field.AddToClassList(className);
            return field;
        }

        /// <summary>A muted explanatory line.</summary>
        /// <param name="text">Text to show.</param>
        public static Label Hint(string text)
        {
            var label = new Label(text);
            label.AddToClassList("rf-hint");
            return label;
        }

        /// <summary>Joins names with commas, or returns a dash when the list is empty.</summary>
        /// <param name="values">Names to join.</param>
        public static string Join(System.Collections.Generic.IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0) return "—";
            return string.Join(", ", values);
        }

        /// <summary>Formats a capture timestamp for the "Last run" header; falls back to the raw text.</summary>
        /// <param name="capturedUtc">Round-trip UTC timestamp.</param>
        public static string Timestamp(string capturedUtc)
        {
            if (string.IsNullOrEmpty(capturedUtc)) return "unknown time";
            return DateTime.TryParse(
                capturedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                : capturedUtc;
        }
    }
}
