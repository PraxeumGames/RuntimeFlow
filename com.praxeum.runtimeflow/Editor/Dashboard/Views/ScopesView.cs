using System;
using System.Globalization;
using UnityEngine.UIElements;

namespace RuntimeFlow.Editor
{
    /// <summary>
    /// One card per scope of the selected host — global, session, then every child run — with its
    /// counters and services. Clicking a card filters the Graph tab to that scope.
    /// </summary>
    internal sealed class ScopesView : VisualElement
    {
        private readonly ScrollView _content;
        private DashboardSnapshot? _snapshot;

        /// <summary>Builds the scrolling card list; the view stays empty until <see cref="Refresh"/> runs.</summary>
        public ScopesView()
        {
            style.flexGrow = 1;
            _content = new ScrollView();
            _content.AddToClassList("rf-pane-right");
            _content.style.flexGrow = 1;
            Add(_content);
        }

        /// <summary>Raised with the captured run identity when a card is clicked.</summary>
        public event Action<string>? ScopeSelected;

        /// <summary>Rebuilds the cards from a new snapshot.</summary>
        /// <param name="snapshot">The current snapshot, or null when no host is selected.</param>
        public void Refresh(DashboardSnapshot? snapshot)
        {
            snapshot?.EnsureIdentities();
            _snapshot = snapshot;
            // Rebuilt wholesale on every tick, so keep the reader where they were scrolled to.
            var scroll = _content.scrollOffset;
            _content.Clear();
            _content.schedule.Execute(() => _content.scrollOffset = scroll);

            if (_snapshot?.HasError == true) _content.Add(ViewHelpers.HostFailure(_snapshot));

            if (_snapshot == null || _snapshot.Scopes.Count == 0)
            {
                _content.Add(ViewHelpers.Hint(
                    "No scope has been built yet. Enter Play Mode or run the demo to see global and session."));
                return;
            }

            var summary = ViewHelpers.Card(_snapshot.HostLabel);
            summary.Add(ViewHelpers.PropertyRow("State", _snapshot.State.ToString()));
            summary.Add(ViewHelpers.PropertyRow("Progress", ViewHelpers.Percent(_snapshot.Percent)));
            summary.Add(ViewHelpers.PropertyRow("Restarts", _snapshot.RestartCount.ToString(CultureInfo.InvariantCulture)));
            summary.Add(ViewHelpers.PropertyRow("Generation", _snapshot.Generation.ToString(CultureInfo.InvariantCulture)));
            if (_snapshot.HaltReason.Length > 0)
                summary.Add(ViewHelpers.PropertyRow("Halted by", _snapshot.HaltReason));
            _content.Add(summary);

            for (var i = 0; i < _snapshot.Scopes.Count; i++)
            {
                _content.Add(ScopeCard(_snapshot.Scopes[i], i));
            }
        }

        private VisualElement ScopeCard(DashboardScope scope, int depth)
        {
            var card = ViewHelpers.Card(scope.Name);
            card.AddToClassList("rf-scope-card");
            card.style.marginLeft = Math.Min(depth, 3) * 12;

            card.Add(ViewHelpers.PropertyRow("State", scope.State.ToString()));
            card.Add(ViewHelpers.PropertyRow(
                "Services",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} done / {1} failed / {2} pending — {3} total",
                    scope.Done, scope.Failed, scope.Skipped, scope.Total)));
            card.Add(ViewHelpers.PropertyRow("Progress", ViewHelpers.Percent(scope.Percent)));
            card.Add(ViewHelpers.PropertyRow("Restart count", scope.RestartCount.ToString(CultureInfo.InvariantCulture)));
            card.Add(ViewHelpers.PropertyRow("Elapsed", ViewHelpers.Seconds(scope.ElapsedMs)));

            var list = new VisualElement();
            list.AddToClassList("rf-scope-services");
            foreach (var service in scope.Services)
            {
                var row = new VisualElement();
                row.AddToClassList("rf-dependency-row");
                row.Add(ViewHelpers.StateBadge(service));

                var name = new Label(service.Name);
                name.AddToClassList("rf-item-name");
                row.Add(name);
                list.Add(row);
            }
            if (scope.Services.Count == 0) list.Add(ViewHelpers.Hint("No services registered in this scope."));
            card.Add(list);

            card.Add(ViewHelpers.Hint("Click to filter the Graph tab to this scope."));
            card.RegisterCallback<ClickEvent>(_ => ScopeSelected?.Invoke(scope.Id));
            return card;
        }
    }
}
