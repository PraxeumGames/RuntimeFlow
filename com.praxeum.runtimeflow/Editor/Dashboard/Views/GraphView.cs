using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine.UIElements;

namespace RuntimeFlow.Editor
{
    /// <summary>
    /// The service list of every scope of the selected host: state, phase, elapsed and weight per row,
    /// a detail card with dependencies and failures for the selected row, and a timeline of the run.
    /// </summary>
    internal sealed class GraphView : VisualElement
    {
        private const float RowHeight = 22f;

        private readonly List<Row> _rows = new List<Row>();
        private readonly ListView _list;
        private readonly ScrollView _detail;
        private readonly TextField _filter;
        private readonly VisualElement _timeline;
        private readonly Label _timelineTitle;
        private readonly VisualElement _hostFailure = new VisualElement();
        private string _hostFailureSignature = string.Empty;

        private DashboardSnapshot? _snapshot;
        private string _scopeFilter = string.Empty;
        private bool _scopeFilterIsIdentity;
        private string _selected = string.Empty;
        private string _detailSignature = string.Empty;
        private Label? _detailElapsed;
        private ProgressBar? _detailProgress;
        private int _rowSignature;

        /// <summary>Builds the two-pane layout; the view stays empty until <see cref="Refresh"/> runs.</summary>
        public GraphView()
        {
            style.flexGrow = 1;
            Add(_hostFailure);

            var split = new TwoPaneSplitView(0, 320, TwoPaneSplitViewOrientation.Horizontal);
            split.AddToClassList("rf-split-view");

            var left = new VisualElement();
            left.AddToClassList("rf-pane-left");

            _filter = new TextField("Filter");
            _filter.AddToClassList("rf-filter-field");
            _filter.RegisterValueChangedCallback(_ => Rebuild());
            left.Add(_filter);

            _list = new ListView(_rows, RowHeight, MakeRow, BindRow)
            {
                selectionType = SelectionType.Single
            };
            _list.style.flexGrow = 1;
            _list.selectionChanged += OnSelectionChanged;
            left.Add(_list);

            var right = new VisualElement();
            right.AddToClassList("rf-pane-right");
            _detail = new ScrollView();
            _detail.style.flexGrow = 1;
            right.Add(_detail);

            split.Add(left);
            split.Add(right);

            var top = new VisualElement();
            top.AddToClassList("rf-graph-top");
            top.Add(split);
            Add(top);

            var timelineSection = new VisualElement();
            timelineSection.AddToClassList("rf-timeline");
            _timelineTitle = new Label("Timeline");
            _timelineTitle.AddToClassList("rf-card-title");
            timelineSection.Add(_timelineTitle);
            _timeline = new ScrollView();
            _timeline.style.flexGrow = 1;
            timelineSection.Add(_timeline);
            Add(timelineSection);
        }

        /// <summary>Shows only the services of one scope; an empty or null filter shows every scope.</summary>
        /// <param name="scope">Display name such as "session"; repeated names match every run with that name.</param>
        public void SetScopeFilter(string? scope)
        {
            _scopeFilter = scope ?? string.Empty;
            _scopeFilterIsIdentity = false;
            Rebuild();
        }

        /// <summary>Filters one captured run, even when several consumer-provided scope names are equal.</summary>
        public void SetScopeIdentityFilter(string id)
        {
            _scopeFilter = id;
            _scopeFilterIsIdentity = true;
            Rebuild();
        }

        /// <summary>Run identity or scope name the view is filtered to, or an empty string.</summary>
        public string ScopeFilter => _scopeFilter;

        /// <summary>Rebinds the list, the detail card and the timeline to a new snapshot.</summary>
        /// <param name="snapshot">The current snapshot, or null when no host is selected.</param>
        public void Refresh(DashboardSnapshot? snapshot)
        {
            snapshot?.EnsureIdentities();
            _snapshot = snapshot;
            // A restart replaces the session run and retires its children. A filter identifies the old
            // run, not an arbitrary replacement with the same display name; reveal all surviving runs.
            if (_scopeFilterIsIdentity && (_snapshot == null || !_snapshot.Scopes.Exists(scope => scope.Id == _scopeFilter)))
            {
                _scopeFilter = string.Empty;
                _scopeFilterIsIdentity = false;
            }
            Rebuild();
        }

        private void Rebuild()
        {
            var failureSignature = (_snapshot?.ErrorType ?? string.Empty) + "|" +
                (_snapshot?.ErrorMessage ?? string.Empty) + "|" + (_snapshot?.ErrorStack ?? string.Empty);
            if (failureSignature != _hostFailureSignature)
            {
                _hostFailureSignature = failureSignature;
                _hostFailure.Clear();
                if (_snapshot?.HasError == true) _hostFailure.Add(ViewHelpers.HostFailure(_snapshot));
            }
            var signature = BuildRows();
            if (signature != _rowSignature)
            {
                _rowSignature = signature;
                _list.Rebuild();
                RestoreSelection();
            }
            else
            {
                _list.RefreshItems();
            }

            UpdateDetail();
            UpdateTimeline();
        }

        private int BuildRows()
        {
            _rows.Clear();
            var signature = 17;
            if (_snapshot == null) return signature;

            var needle = _filter.value ?? string.Empty;
            foreach (var scope in _snapshot.Scopes)
            {
                if (!MatchesScope(scope))
                    continue;

                var matches = new List<DashboardService>();
                foreach (var service in scope.Services)
                {
                    if (Matches(service, needle)) matches.Add(service);
                }
                if (matches.Count == 0) continue;

                _rows.Add(Row.ForHeader(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} — {1}/{2} done, {3} % ({4})",
                    scope.Name, scope.Done, scope.Total,
                    scope.Percent.ToString("0", CultureInfo.InvariantCulture), scope.State)));
                signature = unchecked(signature * 31 + scope.Id.GetHashCode());

                foreach (var service in matches)
                {
                    _rows.Add(Row.ForService(scope, service));
                    signature = unchecked(signature * 31 + service.Id.GetHashCode());
                }
            }

            return signature;
        }

        private static bool Matches(DashboardService service, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return true;
            return service.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                   || service.Phase.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                   || service.Scope.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                   || service.State.ToString().IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool MatchesScope(DashboardScope scope)
            => _scopeFilter.Length == 0 || (_scopeFilterIsIdentity ? scope.Id == _scopeFilter : scope.Name == _scopeFilter);

        private static VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("rf-list-item");

            var header = new Label { name = "header" };
            header.AddToClassList("rf-group-header");
            row.Add(header);

            var body = new VisualElement { name = "body" };
            body.AddToClassList("rf-row-body");

            var badge = new Label { name = "badge" };
            badge.AddToClassList("rf-badge");
            body.Add(badge);

            var serviceName = new Label { name = "name" };
            serviceName.AddToClassList("rf-item-name");
            body.Add(serviceName);

            var phase = new Label { name = "phase" };
            phase.AddToClassList("rf-item-desc");
            body.Add(phase);

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            body.Add(spacer);

            var elapsed = new Label { name = "elapsed" };
            elapsed.AddToClassList("rf-item-metric");
            body.Add(elapsed);

            var weight = new Label { name = "weight" };
            weight.AddToClassList("rf-item-desc");
            body.Add(weight);

            row.Add(body);
            return row;
        }

        private void BindRow(VisualElement element, int index)
        {
            if (index < 0 || index >= _rows.Count) return;
            var row = _rows[index];

            var header = element.Q<Label>("header");
            var body = element.Q<VisualElement>("body");

            if (row.IsHeader)
            {
                header.text = row.Header;
                header.style.display = DisplayStyle.Flex;
                body.style.display = DisplayStyle.None;
                return;
            }

            header.style.display = DisplayStyle.None;
            body.style.display = DisplayStyle.Flex;

            var service = row.Service!;
            var badge = element.Q<Label>("badge");
            badge.text = ViewHelpers.StateText(service);
            SetStateClass(badge, service);

            element.Q<Label>("name").text = service.Name;
            element.Q<Label>("phase").text = service.Phase.Length > 0 ? "[" + service.Phase + "]" : string.Empty;
            element.Q<Label>("elapsed").text = ViewHelpers.Seconds(service.ElapsedMs);
            element.Q<Label>("weight").text = "w " + service.Weight.ToString("0.#", CultureInfo.InvariantCulture);
        }

        private static void SetStateClass(VisualElement element, DashboardService service)
        {
            element.RemoveFromClassList("rf-state--pending");
            element.RemoveFromClassList("rf-state--running");
            element.RemoveFromClassList("rf-state--awaiting");
            element.RemoveFromClassList("rf-state--completed");
            element.RemoveFromClassList("rf-state--degraded");
            element.RemoveFromClassList("rf-state--failed");
            element.RemoveFromClassList("rf-state--cancelled");
            element.RemoveFromClassList("rf-state--skipped");
            element.AddToClassList(ViewHelpers.StateClass(service));
        }

        private void OnSelectionChanged(IEnumerable<object> selection)
        {
            foreach (var item in selection)
            {
                if (item is Row row && !row.IsHeader)
                {
                    _selected = row.Service!.Id;
                    UpdateDetail();
                    return;
                }
            }
        }

        private void RestoreSelection()
        {
            if (_selected.Length == 0) return;
            for (var i = 0; i < _rows.Count; i++)
            {
                if (!_rows[i].IsHeader && _rows[i].Service!.Id == _selected)
                {
                    _list.SetSelectionWithoutNotify(new[] { i });
                    return;
                }
            }
            _list.ClearSelection();
        }

        private DashboardService? SelectedService()
        {
            if (_snapshot == null || _selected.Length == 0) return null;
            return _snapshot.Find(_selected);
        }

        /// <summary>
        /// Rebuilds the detail pane only when the shape of the selection changes (state, failure,
        /// dependency states); a tick that only moves elapsed and sub-progress updates those two labels
        /// in place, so a selection inside the stack trace is not wiped four times a second.
        /// </summary>
        private void UpdateDetail()
        {
            var selected = SelectedService();
            var signature = DetailSignature(selected);
            if (selected != null && signature == _detailSignature)
            {
                if (_detailElapsed != null) _detailElapsed.text = ViewHelpers.Seconds(selected.ElapsedMs);
                if (_detailProgress != null) SetProgress(_detailProgress, selected);
                return;
            }

            _detailSignature = signature;
            _detailElapsed = null;
            _detailProgress = null;
            _detail.Clear();

            var service = selected;
            if (service == null)
            {
                _detail.Add(ViewHelpers.Hint(_snapshot == null
                    ? "No host selected. Enter Play Mode or run the demo to see a live graph."
                    : "Select a service to see its dependencies, sub-progress and failure."));
                return;
            }

            var card = ViewHelpers.Card(service.Name);
            card.Add(ViewHelpers.PropertyRow("Scope", service.Scope));
            card.Add(ViewHelpers.PropertyRow("Phase", service.Phase.Length > 0 ? service.Phase : "—"));
            card.Add(ViewHelpers.PropertyRow("State", ViewHelpers.StateText(service)));
            var elapsedRow = ViewHelpers.PropertyRow("Elapsed", ViewHelpers.Seconds(service.ElapsedMs));
            _detailElapsed = elapsedRow.Q<Label>(className: "rf-property-value");
            card.Add(elapsedRow);
            card.Add(ViewHelpers.PropertyRow("Weight", service.Weight.ToString("0.##", CultureInfo.InvariantCulture)));
            card.Add(ViewHelpers.PropertyRow("Optional", service.Optional ? "yes" : "no"));
            card.Add(ViewHelpers.PropertyRow("User-gated", service.UserGated ? "yes" : "no"));
            if (service.AwaitingPlayer) card.Add(ViewHelpers.PropertyRow("Awaiting player", "yes"));

            var progress = new ProgressBar { lowValue = 0f, highValue = 100f };
            progress.AddToClassList("rf-progress");
            SetProgress(progress, service);
            _detailProgress = progress;
            card.Add(progress);
            _detail.Add(card);

            var dependencies = ViewHelpers.Card("Dependencies (" + service.Dependencies.Count + ")");
            if (service.Dependencies.Count == 0)
            {
                dependencies.Add(ViewHelpers.Hint("None: this service starts as soon as the run does."));
            }
            else
            {
                for (var i = 0; i < service.Dependencies.Count; i++)
                    dependencies.Add(DependencyRow(service.Dependencies[i], DependencyId(service, i)));
            }
            dependencies.Add(ViewHelpers.PropertyRow("Waiting on", ViewHelpers.Join(service.WaitingOn)));
            _detail.Add(dependencies);

            if (service.HasError) _detail.Add(FailureCard(service));
        }

        private static void SetProgress(ProgressBar bar, DashboardService service)
        {
            var value = Math.Max(0f, Math.Min(1f, service.Progress));
            bar.value = value * 100f;
            bar.title = "sub-progress " + (value * 100f).ToString("0", CultureInfo.InvariantCulture) + " %";
        }

        private string DetailSignature(DashboardService? service)
        {
            if (service == null) return "none";
            var text = new System.Text.StringBuilder(service.Id).Append('|').Append(service.State)
                .Append('|').Append(service.AwaitingPlayer).Append('|').Append(service.ErrorType)
                .Append('|').Append(service.ErrorMessage).Append('|').Append(service.ErrorStack)
                .Append('|').Append(service.Phase).Append('|').Append(service.Weight)
                .Append('|').Append(service.Optional).Append('|').Append(service.UserGated)
                .Append('|').Append(string.Join(",", service.WaitingOn));
            for (var i = 0; i < service.Dependencies.Count; i++)
            {
                var id = DependencyId(service, i);
                text.Append('|').Append(service.Dependencies[i]).Append(':').Append(id)
                    .Append(':').Append(_snapshot?.Find(id)?.State.ToString() ?? "-");
            }
            return text.ToString();
        }

        private static string DependencyId(DashboardService service, int index)
            => service.DependencyIds != null && index < service.DependencyIds.Count ? service.DependencyIds[index] : string.Empty;

        private VisualElement DependencyRow(string name, string id)
        {
            var row = new VisualElement();
            row.AddToClassList("rf-dependency-row");

            var dependency = id.Length == 0 ? null : _snapshot?.Find(id);
            if (dependency != null)
            {
                var badge = ViewHelpers.StateBadge(dependency);
                row.Add(badge);
            }
            else
            {
                var badge = new Label("external");
                badge.AddToClassList("rf-badge");
                badge.AddToClassList("rf-state--completed");
                row.Add(badge);
            }

            var label = new Label(name);
            label.AddToClassList("rf-item-name");
            row.Add(label);
            return row;
        }

        private static VisualElement FailureCard(DashboardService service)
        {
            var failed = service.State == ServiceState.Failed;
            var card = ViewHelpers.Card(failed
                ? "Failed: " + service.ErrorType
                : "Degraded: " + service.ErrorType);
            card.AddToClassList(failed ? "rf-card--failed" : "rf-card--degraded");

            var message = new Label(service.ErrorMessage);
            message.AddToClassList("rf-error-message");
            card.Add(message);

            card.Add(ViewHelpers.PropertyRow("Was waiting on", ViewHelpers.Join(service.WaitingOn)));

            var stack = ViewHelpers.SelectableText(
                service.ErrorStack.Length > 0 ? service.ErrorStack : "(no stack trace)", "rf-stack");
            card.Add(stack);
            return card;
        }

        /// <summary>
        /// One bar per service. A start timestamp is not part of the status snapshot, so the bar starts at
        /// <c>scope.Elapsed - service.Elapsed</c>: exact for a service that is still running or finished
        /// last, an approximation for everything that finished earlier (it drifts right as the run goes on).
        /// Pending and skipped services get a zero-width marker at the origin.
        /// </summary>
        private void UpdateTimeline()
        {
            _timeline.Clear();

            if (_snapshot == null || _snapshot.Scopes.Count == 0)
            {
                _timelineTitle.text = "Timeline";
                _timeline.Add(ViewHelpers.Hint("No run to plot yet."));
                return;
            }

            var plotted = 0;
            foreach (var scope in _snapshot.Scopes)
            {
                if (!MatchesScope(scope))
                    continue;

                var total = scope.ElapsedMs > 1.0 ? scope.ElapsedMs : 1.0;
                foreach (var service in scope.Services)
                {
                    _timeline.Add(TimelineRow(service, total));
                    plotted++;
                }
            }

            _timelineTitle.text = string.Format(
                CultureInfo.InvariantCulture,
                "Timeline — {0} service(s), bars are relative to the elapsed time of their scope", plotted);
        }

        private static VisualElement TimelineRow(DashboardService service, double scopeElapsedMs)
        {
            var row = new VisualElement();
            row.AddToClassList("rf-timeline-row");

            var name = new Label(service.Name);
            name.AddToClassList("rf-timeline-name");
            row.Add(name);

            var track = new VisualElement();
            track.AddToClassList("rf-timeline-track");

            var started = service.State != ServiceState.Pending && service.State != ServiceState.Skipped;
            var width = started ? Math.Max(0.5, service.ElapsedMs / scopeElapsedMs * 100.0) : 0.5;
            var left = started
                ? Math.Max(0.0, Math.Min(99.5, (scopeElapsedMs - service.ElapsedMs) / scopeElapsedMs * 100.0))
                : 0.0;
            if (left + width > 100.0) width = Math.Max(0.5, 100.0 - left);

            var bar = new VisualElement();
            bar.AddToClassList("rf-timeline-bar");
            bar.AddToClassList(ViewHelpers.StateClass(service));
            bar.style.left = new StyleLength(Length.Percent((float)left));
            bar.style.width = new StyleLength(Length.Percent((float)width));
            bar.tooltip = service.Name + " — " + ViewHelpers.Seconds(service.ElapsedMs);
            track.Add(bar);

            row.Add(track);

            var elapsed = new Label(ViewHelpers.Seconds(service.ElapsedMs));
            elapsed.AddToClassList("rf-timeline-elapsed");
            row.Add(elapsed);
            return row;
        }

        private sealed class Row
        {
            private Row(bool isHeader, string header, DashboardScope? scope, DashboardService? service)
            {
                IsHeader = isHeader;
                Header = header;
                Scope = scope;
                Service = service;
            }

            public bool IsHeader { get; }
            public string Header { get; }
            public DashboardScope? Scope { get; }
            public DashboardService? Service { get; }

            public static Row ForHeader(string text) => new Row(true, text, null, null);

            public static Row ForService(DashboardScope scope, DashboardService service)
                => new Row(false, string.Empty, scope, service);
        }
    }
}
