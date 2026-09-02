using System.Globalization;
using UnityEngine.UIElements;

namespace RuntimeFlow.Editor
{
    /// <summary>
    /// The snapshot persisted when Play Mode ended: the same rows in their final states, the failure
    /// cards, and the graph description. Falls back to an explainer when nothing has run yet.
    /// </summary>
    public sealed class LastRunView : VisualElement
    {
        private readonly ScrollView _content;
        private string _signature = string.Empty;

        /// <summary>Builds the scrolling container; the view stays empty until <see cref="Refresh"/> runs.</summary>
        public LastRunView()
        {
            style.flexGrow = 1;
            _content = new ScrollView();
            _content.AddToClassList("rf-pane-right");
            _content.style.flexGrow = 1;
            Add(_content);
        }

        /// <summary>Renders the stored snapshot, or the explainer when there is none.</summary>
        /// <param name="snapshot">The snapshot read back from storage, or null.</param>
        /// <param name="hasLiveHost">True when a host is alive right now, which the header mentions.</param>
        public void Refresh(DashboardSnapshot? snapshot, bool hasLiveHost)
        {
            // This tab shows a finished run, so it is rebuilt only when the states actually change:
            // a selection inside a stack trace survives the four-per-second refresh of Play Mode.
            var signature = Signature(snapshot, hasLiveHost);
            if (signature == _signature) return;
            _signature = signature;

            // Rebuilt wholesale, so keep the reader where they were scrolled to.
            var scroll = _content.scrollOffset;
            _content.Clear();
            _content.schedule.Execute(() => _content.scrollOffset = scroll);

            if (snapshot == null)
            {
                var explainer = ViewHelpers.Card("Nothing has run yet");
                explainer.Add(ViewHelpers.Hint(
                    "Enter Play Mode or run the demo; the dashboard shows every service, its state, elapsed, " +
                    "dependencies and failures."));
                explainer.Add(ViewHelpers.Hint(
                    "When Play Mode ends the last snapshot is kept here, so a failure stays readable after the run."));
                _content.Add(explainer);
                return;
            }

            var header = ViewHelpers.Card(
                "Last run — " + snapshot.HostLabel + " at " + ViewHelpers.Timestamp(snapshot.CapturedUtc));
            header.Add(ViewHelpers.PropertyRow("State", snapshot.State.ToString()));
            header.Add(ViewHelpers.PropertyRow("Progress", ViewHelpers.Percent(snapshot.Percent)));
            header.Add(ViewHelpers.PropertyRow("Elapsed", ViewHelpers.Seconds(snapshot.ElapsedMs)));
            header.Add(ViewHelpers.PropertyRow("Restarts", snapshot.RestartCount.ToString(CultureInfo.InvariantCulture)));
            header.Add(ViewHelpers.PropertyRow("Package", snapshot.PackageVersion));
            header.Add(ViewHelpers.PropertyRow("Unity", snapshot.UnityVersion));
            if (snapshot.HaltReason.Length > 0)
                header.Add(ViewHelpers.PropertyRow("Halted by", snapshot.HaltReason));
            if (hasLiveHost)
                header.Add(ViewHelpers.Hint("A host is alive right now: this is the stored snapshot, not the live one."));
            _content.Add(header);

            foreach (var scope in snapshot.Scopes)
            {
                var card = ViewHelpers.Card(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} — {1} ({2} done / {3} failed / {4} pending of {5})",
                    scope.Name, scope.State, scope.Done, scope.Failed, scope.Skipped, scope.Total));

                foreach (var service in scope.Services)
                {
                    var row = new VisualElement();
                    row.AddToClassList("rf-dependency-row");
                    row.Add(ViewHelpers.StateBadge(service));

                    var name = new Label(service.Name);
                    name.AddToClassList("rf-item-name");
                    row.Add(name);

                    var spacer = new VisualElement();
                    spacer.style.flexGrow = 1;
                    row.Add(spacer);

                    var elapsed = new Label(ViewHelpers.Seconds(service.ElapsedMs));
                    elapsed.AddToClassList("rf-item-metric");
                    row.Add(elapsed);
                    card.Add(row);

                    if (service.HasError) card.Add(FailureBox(service));
                }

                _content.Add(card);
            }

            var describe = ViewHelpers.Card("Describe()");
            describe.Add(ViewHelpers.SelectableText(
                snapshot.Describe.Length > 0 ? snapshot.Describe : "(no description was captured)", "rf-stack"));
            _content.Add(describe);
        }

        private static string Signature(DashboardSnapshot? snapshot, bool hasLiveHost)
        {
            if (snapshot == null) return "none";
            var text = new System.Text.StringBuilder(snapshot.HostLabel)
                .Append('|').Append(snapshot.State).Append('|').Append(hasLiveHost);
            foreach (var scope in snapshot.Scopes)
            {
                text.Append('|').Append(scope.Name).Append(':').Append(scope.State);
                foreach (var service in scope.Services)
                {
                    text.Append(',').Append(service.Name).Append('=').Append(service.State)
                        .Append('/').Append(service.ErrorType);
                }
            }
            return text.ToString();
        }

        private static VisualElement FailureBox(DashboardService service)
        {
            var box = new VisualElement();
            box.AddToClassList("rf-card");
            box.AddToClassList(service.State == ServiceState.Failed ? "rf-card--failed" : "rf-card--degraded");

            var title = new Label(service.Name + " — " + service.ErrorType);
            title.AddToClassList("rf-card-title");
            box.Add(title);

            var message = new Label(service.ErrorMessage);
            message.AddToClassList("rf-error-message");
            box.Add(message);

            box.Add(ViewHelpers.PropertyRow("Was waiting on", ViewHelpers.Join(service.WaitingOn)));
            box.Add(ViewHelpers.SelectableText(
                service.ErrorStack.Length > 0 ? service.ErrorStack : "(no stack trace)", "rf-stack"));
            return box;
        }
    }
}
