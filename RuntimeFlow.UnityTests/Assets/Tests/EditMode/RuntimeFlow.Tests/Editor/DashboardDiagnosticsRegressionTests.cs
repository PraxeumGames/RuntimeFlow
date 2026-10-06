using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Editor;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using UnityEngine.UIElements;
using VContainer;

namespace RuntimeFlow.Tests.Editor
{
    [TestFixture]
    public sealed class DashboardDiagnosticsRegressionTests
    {
        [Test]
        [Timeout(10000)]
        public async Task WindowCaptureAndRestartKeepTheSelectedHostWhenAnEarlierHostDisappears()
        {
            var options = new RuntimeFlowOptions { Logger = new CapturingLogger() };
            var first = new RuntimeFlowHost(_ => { }, _ => { }, options);
            var selected = new RuntimeFlowHost(_ => { }, _ => { }, options);
            var last = new RuntimeFlowHost(_ => { }, _ => { }, options);
            var window = UnityEngine.ScriptableObject.CreateInstance<RuntimeFlowDashboardWindow>();
            try
            {
                await first.StartAsync();
                await selected.StartAsync();
                await last.StartAsync();
                var selection = (DashboardHostSelection)WindowField("_hostSelection").GetValue(window);
                selection.Select(selected);
                await first.DisposeAsync();
                // The hidden window has not built its GUI or refreshed its dropdown: actions must still
                // resolve the selected host's identity against the newly changed registry.
                var captured = (DashboardSnapshot)WindowMethod("CaptureSelected").Invoke(window, null);
                Assert.That(captured.Scopes[1].Id, Is.EqualTo(DashboardData.Capture(selected, 0).Scopes[1].Id));
                WindowMethod("RestartSelectedHost").Invoke(window, null);
                await AsyncTestAssert.Until(() => selected.Generation == 1 && selected.State == RunState.Completed,
                    TimeSpan.FromSeconds(2), "The selected host should receive the dashboard restart.");
                Assert.That(last.Generation, Is.Zero, "Removing another host must never move the restart target.");

                await selected.DisposeAsync();
                Assert.That(selection.Resolve(DashboardData.LiveHosts, out _), Is.Null,
                    "A removed selection must not silently become the remaining host.");
                Assert.That(WindowMethod("CaptureSelected").Invoke(window, null), Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
                await last.DisposeAsync();
                await selected.DisposeAsync();
                await first.DisposeAsync();
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task LastRunTabPreservesAStoredFailureWhileAnotherHostIsLive()
        {
            await using var host = new RuntimeFlowHost(_ => { }, _ => { },
                new RuntimeFlowOptions { Logger = new CapturingLogger() });
            await host.StartAsync();
            var live = DashboardData.Capture(host, 0);
            var stored = new DashboardSnapshot
            {
                HostLabel = "previous failed host", State = RunState.Failed,
                ErrorType = "InvalidOperationException", ErrorMessage = "previous failure remains readable"
            };
            var window = UnityEngine.ScriptableObject.CreateInstance<RuntimeFlowDashboardWindow>();
            var view = new LastRunView();
            try
            {
                ((DashboardHostSelection)WindowField("_hostSelection").GetValue(window)).Select(host);
                WindowField("_snapshot").SetValue(window, live);
                WindowField("_selectedTab").SetValue(window, 2);
                WindowField("_lastRunView").SetValue(window, view);
                DashboardData.SaveLastRun(stored);
                WindowMethod("RenderCurrentTab").Invoke(window, null);
                AssertLabel(view, stored.ErrorMessage);
                Assert.That(view.Query<Label>().ToList().Exists(label => label.text.StartsWith("Last run — previous failed host", StringComparison.Ordinal)), Is.True);
                AssertLabel(view, "A host is alive right now: this is the stored snapshot, not the live one.");

                DashboardData.ClearLastRun();
                WindowMethod("RenderCurrentTab").Invoke(window, null);
                Assert.That(view.Query<Label>().ToList().Exists(label => label.text.StartsWith("Current run — ", StringComparison.Ordinal)), Is.True);
                AssertLabel(view, "No previous run has been stored yet; this shows the current live run.");
                Assert.That(view.Query<Label>().ToList().Exists(label => label.text == stored.ErrorMessage), Is.False);
            }
            finally
            {
                DashboardData.ClearLastRun();
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        private static FieldInfo WindowField(string name)
            => typeof(RuntimeFlowDashboardWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

        private static MethodInfo WindowMethod(string name)
            => typeof(RuntimeFlowDashboardWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

        [Test]
        [Timeout(10000)]
        public async Task RestartClearsTheRetiredSessionFilterAndRevealsTheReplacementRun()
        {
            await using var host = new RuntimeFlowHost(_ => { }, builder =>
            {
                builder.RegisterInstance(new ScopeProbe());
                builder.RegisterInitializable<SharedInitializer>();
            }, new RuntimeFlowOptions { Logger = new CapturingLogger() });
            await host.StartAsync();
            var before = DashboardData.Capture(host, 0);
            var graph = new GraphView();
            graph.Refresh(before);
            graph.SetScopeIdentityFilter(before.Scopes[1].Id);
            await host.RestartAsync("filtered restart");
            var after = DashboardData.Capture(host, 0);
            Assert.That(after.Scopes[1].Id, Is.Not.EqualTo(before.Scopes[1].Id));
            graph.Refresh(after);
            Assert.That(graph.ScopeFilter, Is.Empty);
            Assert.That(graph.Q<ListView>().itemsSource.Count, Is.EqualTo(2), "The replacement session remains visible.");
            Assert.That(graph.Query<Label>().ToList().Exists(label => label.text.StartsWith("Timeline — 1 service(s)", StringComparison.Ordinal)), Is.True);
        }

        [Test]
        public void LegacyStoredJsonLoadsAndRendersASelectedDependencyWithoutNewFields()
        {
            // This is the persisted shape before Id, DependencyIds and host Error fields were added.
            const string legacy = @"{
                ""HostLabel"":""host #1 (gen 0)"",""State"":2,""Scopes"":[{
                    ""Name"":""session"",""State"":2,""Services"":[
                        {""Name"":""dependency"",""Scope"":""session"",""Phase"":"""",""State"":2,
                         ""Dependencies"":[],""WaitingOn"":[],""ErrorType"":"""",""ErrorMessage"":"""",""ErrorStack"":""""},
                        {""Name"":""consumer"",""Scope"":""session"",""Phase"":"""",""State"":2,
                         ""Dependencies"":[""dependency""],""WaitingOn"":[],""ErrorType"":"""",""ErrorMessage"":"""",""ErrorStack"":""""}
                    ]
                }]
            }";
            try
            {
                UnityEditor.SessionState.SetString(DashboardData.LastRunKey, legacy);
                var loaded = DashboardData.LoadLastRun()!;
                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded.ErrorStack, Is.Empty);
                var consumer = loaded.Scopes[0].Services[1];
                Assert.That(consumer.DependencyIds, Is.Not.Null.And.Empty);
                var graph = new GraphView();
                graph.Refresh(loaded);
                graph.Q<ListView>().SetSelection(2);
                AssertLabel(graph, "Dependencies (1)");
                AssertLabel(graph, "external"); // Old names cannot prove which run a dependency refers to.
                var last = new LastRunView();
                last.Refresh(loaded, false);
            }
            finally
            {
                DashboardData.ClearLastRun();
            }
        }

        public sealed class ScopeProbe
        {
            public string Failure = string.Empty;
        }

        [Init(Optional = true)]
        public sealed class SharedInitializer : IAsyncInitializable
        {
            private readonly ScopeProbe _probe;
            public SharedInitializer(ScopeProbe probe) => _probe = probe;
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
                => _probe.Failure.Length == 0 ? Task.CompletedTask : Task.FromException(new InvalidOperationException(_probe.Failure));
        }

        public sealed class ChildConsumer : IAsyncInitializable
        {
            public ChildConsumer(SharedInitializer dependency) { }
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        [Test]
        [Timeout(10000)]
        public async Task RepeatedScopeNamesKeepSelectionAndDependenciesAttachedToTheCorrectRun()
        {
            await using var host = new RuntimeFlowHost(_ => { }, _ => { },
                new RuntimeFlowOptions { Logger = new CapturingLogger() });
            await host.StartAsync();
            foreach (var failure in new[] { string.Empty, "second child failed" })
            {
                var scope = host.Session.CreateScope(builder =>
                {
                    builder.RegisterInstance(new ScopeProbe { Failure = failure });
                    builder.RegisterInitializable<SharedInitializer>();
                    builder.RegisterInitializable<ChildConsumer>();
                });
                await host.InitializeScopeAsync(scope, "duplicate-child-name");
            }

            var snapshot = DashboardData.Capture(host, 0);
            var first = snapshot.Scopes[2];
            var second = snapshot.Scopes[3];
            Assert.That(first.Name, Is.EqualTo(second.Name));
            Assert.That(first.Id, Is.Not.EqualTo(second.Id));
            var firstInitializer = first.Services.Find(service => service.Name == nameof(SharedInitializer))!;
            var secondInitializer = second.Services.Find(service => service.Name == nameof(SharedInitializer))!;
            var consumer = second.Services.Find(service => service.Name == nameof(ChildConsumer))!;
            Assert.That(consumer.DependencyIds, Does.Contain(secondInitializer.Id).And.Not.Contain(firstInitializer.Id));

            var graph = new GraphView();
            graph.Refresh(snapshot);
            graph.Q<ListView>().SetSelection(4); // second scope's SharedInitializer after two first-scope rows
            AssertLabel(graph, "second child failed");
            graph.Refresh(DashboardData.Capture(host, 0));
            AssertLabel(graph, "second child failed");

            graph.Q<ListView>().SetSelection(5); // second scope's consumer
            var dependencyRows = graph.Query<VisualElement>(className: "rf-dependency-row").ToList();
            Assert.That(dependencyRows.Exists(row => row.Q<Label>(className: "rf-badge")?.text == "degraded"), Is.True);

            // Reordering scopes rebuilds rows; selection must restore by identity rather than name/index.
            snapshot.Scopes.Reverse();
            graph.Refresh(snapshot);
            Assert.That(graph.Q<ListView>().selectedIndex, Is.EqualTo(2));
            dependencyRows = graph.Query<VisualElement>(className: "rf-dependency-row").ToList();
            Assert.That(dependencyRows.Exists(row => row.Q<Label>(className: "rf-badge")?.text == "degraded"), Is.True);

            graph.SetScopeIdentityFilter(second.Id);
            Assert.That(graph.Q<ListView>().itemsSource.Count, Is.EqualTo(3), "A clicked scope ID filters only that run despite repeated names.");
        }

        [Test]
        public void SelectingSameNamedServiceFromSecondScopeMustShowItsFailure()
        {
            var snapshot = new DashboardSnapshot { HostLabel = "host #1 (gen 0)", State = RunState.Completed };
            var first = new DashboardScope { Name = "first-child", State = RunState.Completed, Total = 1, Done = 1 };
            first.Services.Add(new DashboardService
            {
                Name = "SharedInitializer", Scope = first.Name, State = ServiceState.Completed
            });
            var second = new DashboardScope { Name = "second-child", State = RunState.Failed, Total = 1, Failed = 1 };
            second.Services.Add(new DashboardService
            {
                Name = "SharedInitializer", Scope = second.Name, State = ServiceState.Failed,
                ErrorType = "InvalidOperationException", ErrorMessage = "second child failed"
            });
            snapshot.Scopes.Add(first);
            snapshot.Scopes.Add(second);

            // Repeated initializer types in separate child scopes legitimately share a service name.
            var graph = new GraphView();
            graph.Refresh(snapshot);
            graph.Q<ListView>().SetSelection(3); // header, first service, header, second service
            var labels = graph.Query<Label>().ToList();
            Assert.That(labels.Exists(label => label.text == "second child failed"), Is.True,
                "Selecting the second child's row must show its error, rather than the first same-named service's Completed state.");
        }

        [Test]
        [Timeout(10000)]
        public async Task SessionBuildFailureMustRemainInTheExportedDiagnostics()
        {
            const string message = "session installer DI wiring exploded";
            var host = new RuntimeFlowHost(_ => { }, _ => throw new InvalidOperationException(message),
                new RuntimeFlowOptions { Logger = new CapturingLogger() });
            try
            {
                await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
                var status = host.GetStatus();
                Assert.That(status.State, Is.EqualTo(RunState.Failed));
                Assert.That(status.Error, Is.Not.Null);
                Assert.That(status.Error!.Message, Is.EqualTo(message),
                    "The runtime retains the failure, so this proof checks only the dashboard/export boundary.");

                var json = DiagnosticsJson.Build(DashboardData.Capture(host, 0));
                Assert.That(json, Does.Contain(message),
                    "A failed session build has no failed service row; omitting host Error loses the sole cause of failure from the bug-report export.");
                var captured = DashboardData.Capture(host, 0);
                Assert.That(captured.HasError, Is.True);
                Assert.That(json, Does.Contain("\"ErrorType\": \"InvalidOperationException\""));
                DashboardData.SaveLastRun(captured);
                var restored = DashboardData.LoadLastRun()!;
                Assert.That(restored.ErrorMessage, Is.EqualTo(message));
                Assert.That(restored.ErrorStack, Is.EqualTo(captured.ErrorStack));
                Assert.That(restored.Scopes[0].Id, Is.EqualTo(captured.Scopes[0].Id));
                var graph = new GraphView();
                graph.Refresh(restored);
                AssertLabel(graph, message);
                var scopes = new ScopesView();
                scopes.Refresh(restored);
                AssertLabel(scopes, message);
                var last = new LastRunView();
                last.Refresh(restored, false);
                AssertLabel(last, message);
            }
            finally
            {
                await host.DisposeAsync();
                DashboardData.ClearLastRun();
            }
        }

        [Test]
        public void RefreshingSameServiceWithANewErrorUpdatesTheDetail()
        {
            var snapshot = new DashboardSnapshot();
            var scope = new DashboardScope { Id = "scope", Name = "child" };
            var service = new DashboardService
            {
                Id = "scope/node:0", Name = "service", Scope = "child", State = ServiceState.Failed,
                ErrorType = "InvalidOperationException", ErrorMessage = "first message"
            };
            scope.Services.Add(service);
            snapshot.Scopes.Add(scope);
            var graph = new GraphView();
            graph.Refresh(snapshot);
            graph.Q<ListView>().SetSelection(1);
            AssertLabel(graph, "first message");
            service.ErrorMessage = "second message";
            graph.Refresh(snapshot);
            AssertLabel(graph, "second message");
        }

        [Test]
        public void OldSnapshotsReceiveDistinctIdentitiesWithoutGuessingAmbiguousDependencies()
        {
            var snapshot = new DashboardSnapshot();
            for (var i = 0; i < 2; i++)
            {
                var scope = new DashboardScope { Name = "duplicate" };
                var service = new DashboardService { Name = "duplicate", Scope = "duplicate" };
                service.Dependencies.Add("duplicate");
                scope.Services.Add(service);
                snapshot.Scopes.Add(scope);
            }
            snapshot.EnsureIdentities();
            var firstId = snapshot.Scopes[0].Services[0].Id;
            var secondId = snapshot.Scopes[1].Services[0].Id;
            Assert.That(secondId, Is.Not.EqualTo(firstId));
            Assert.That(snapshot.Find(secondId), Is.SameAs(snapshot.Scopes[1].Services[0]));
            snapshot.EnsureIdentities();
            Assert.That(snapshot.Scopes[1].Services[0].Id, Is.EqualTo(secondId));
            Assert.That(snapshot.Scopes[1].Services[0].DependencyIds, Is.Empty);
        }

        [Test]
        public void HostFailureStringsRoundTripAndRefreshWithoutAServiceRow()
        {
            var snapshot = new DashboardSnapshot
            {
                State = RunState.Failed, ErrorType = "InvalidOperationException",
                ErrorMessage = "failed \"DI\" wiring\nsecond line", ErrorStack = "outer\n ---> inner: C:\\temp\\failure"
            };
            var json = DiagnosticsJson.Build(snapshot);
            var restored = UnityEngine.JsonUtility.FromJson<DashboardSnapshot>(json);
            Assert.That(restored.ErrorMessage, Is.EqualTo(snapshot.ErrorMessage));
            Assert.That(restored.ErrorStack, Is.EqualTo(snapshot.ErrorStack));
            Assert.That(restored.Scopes, Is.Empty);
            var graph = new GraphView();
            graph.Refresh(restored);
            AssertLabel(graph, snapshot.ErrorMessage);
            var last = new LastRunView();
            last.Refresh(restored, false);
            AssertLabel(last, snapshot.ErrorMessage);
            restored.ErrorMessage = "new cause with the same exception type";
            graph.Refresh(restored);
            last.Refresh(restored, false);
            AssertLabel(graph, restored.ErrorMessage);
            AssertLabel(last, restored.ErrorMessage);
        }

        private static void AssertLabel(VisualElement view, string text)
            => Assert.That(view.Query<Label>().ToList().Exists(label => label.text == text), Is.True, text);
    }
}
