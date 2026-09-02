using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Editor;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Editor
{
    /// <summary>
    /// The dashboard reads live hosts out of the registry, turns one into a snapshot of scopes and
    /// services, renders it as escaped JSON, and stores it so the "Last run" tab survives Play Mode.
    /// No <c>EditorWindow</c> is instantiated here: everything under test is plain data.
    /// </summary>
    [TestFixture]
    public sealed class DashboardDataTests
    {
        private const string NastyMessage = "profile \"api\" broke: C:\\tmp\\log.txt\nsecond line\tafter a tab";

        [Init(Phase = "platform")]
        public sealed class ConfigService : IAsyncInitializable
        {
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        [Init(Phase = "content", Weight = 2)]
        public sealed class ProfileService : IAsyncInitializable
        {
            /// <summary>Constructor edge: the profile starts after the global config.</summary>
            public ProfileService(ConfigService config)
            {
                Config = config;
            }

            public ConfigService Config { get; }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        [Init(Phase = "content", Optional = true)]
        public sealed class FlakyService : IAsyncInitializable
        {
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
                => throw new InvalidOperationException(NastyMessage);
        }

        [SetUp]
        public void ForgetStoredSnapshot() => DashboardData.ClearLastRun();

        [TearDown]
        public void ClearStoredSnapshot() => DashboardData.ClearLastRun();

        private static Task<TestFlow> StartAsync()
            => TestFlow
                .Create(
                    builder => builder.Add<ConfigService>(),
                    builder =>
                    {
                        builder.Add<ProfileService>();
                        builder.Add<FlakyService>();
                    })
                .Configure(options => options.Phases = new[] { "platform", "content" })
                .StartAsync();

        [Test]
        public async Task Registry_lists_a_live_host_and_drops_it_after_disposal()
        {
            var app = await StartAsync();
            try
            {
                Assert.That(DashboardData.LiveHosts, Has.Member(app.Host));
            }
            finally
            {
                var host = app.Host;
                await app.DisposeAsync();
                Assert.That(DashboardData.LiveHosts, Has.No.Member(host));
            }
        }

        [Test]
        public async Task Snapshot_lists_global_and_session_scopes_with_their_services()
        {
            await using var app = await StartAsync();
            var snapshot = DashboardData.Capture(app.Host, 0);

            Assert.That(snapshot.HostLabel, Is.EqualTo("host #1 (gen 0)"));
            Assert.That(snapshot.Live, Is.True);
            Assert.That(Names(snapshot), Is.EquivalentTo(new[] { "global", "session" }));

            var global = Scope(snapshot, "global");
            Assert.That(global.Services.Count, Is.EqualTo(1));
            Assert.That(global.Services[0].Name, Is.EqualTo(nameof(ConfigService)));
            Assert.That(global.Services[0].State, Is.EqualTo(ServiceState.Completed));
            Assert.That(global.Services[0].Phase, Is.EqualTo("platform"));
            Assert.That(global.Done, Is.EqualTo(1));
            Assert.That(global.Total, Is.EqualTo(1));

            var session = Scope(snapshot, "session");
            var profile = Service(session, nameof(ProfileService));
            Assert.That(profile.State, Is.EqualTo(ServiceState.Completed));
            Assert.That(profile.Weight, Is.EqualTo(2.0));
            Assert.That(profile.Phase, Is.EqualTo("content"));
            Assert.That(profile.Dependencies, Does.Contain(nameof(ConfigService)));

            var flaky = Service(session, nameof(FlakyService));
            Assert.That(flaky.State, Is.EqualTo(ServiceState.Degraded));
            Assert.That(flaky.Optional, Is.True);
            Assert.That(flaky.ErrorType, Is.EqualTo(nameof(InvalidOperationException)));
            Assert.That(flaky.ErrorMessage, Is.EqualTo(NastyMessage));

            Assert.That(session.Failed, Is.EqualTo(0));
            Assert.That(session.Done, Is.EqualTo(2));
            Assert.That(snapshot.Describe, Does.Contain("global").And.Contain("session"));
            Assert.That(snapshot.CanRestart, Is.True);
        }

        [Test]
        public async Task Diagnostics_json_lists_every_service_with_state_elapsed_and_phase()
        {
            await using var app = await StartAsync();
            var json = DiagnosticsJson.Build(DashboardData.Capture(app.Host, 0));

            AssertWellFormed(json);
            Assert.That(json, Does.Contain("\"packageVersion\""));
            Assert.That(json, Does.Contain("\"scopes\""));
            Assert.That(json, Does.Contain("\"name\": \"global\""));
            Assert.That(json, Does.Contain("\"name\": \"session\""));

            foreach (var name in new[] { nameof(ConfigService), nameof(ProfileService), nameof(FlakyService) })
            {
                Assert.That(json, Does.Contain("\"name\": \"" + name + "\""), name + " is missing from the JSON.");
            }

            Assert.That(json, Does.Contain("\"state\": \"Completed\""));
            Assert.That(json, Does.Contain("\"state\": \"Degraded\""));
            Assert.That(json, Does.Contain("\"phase\": \"platform\""));
            Assert.That(json, Does.Contain("\"phase\": \"content\""));
            Assert.That(json, Does.Contain("\"elapsedMs\""));
            Assert.That(json, Does.Contain("\"weight\": 2"));
            Assert.That(json, Does.Contain("\"dependencies\": ["));
            Assert.That(json, Does.Contain("\"waitingOn\": []"));
            Assert.That(json, Does.Contain("\"type\": \"" + nameof(InvalidOperationException) + "\""));
            Assert.That(json, Does.Contain("\"describe\": \""));
        }

        [Test]
        public async Task Diagnostics_json_escapes_quotes_backslashes_and_newlines()
        {
            await using var app = await StartAsync();
            var json = DiagnosticsJson.Build(DashboardData.Capture(app.Host, 0));

            AssertWellFormed(json);
            Assert.That(json, Does.Contain("profile \\\"api\\\" broke: C:\\\\tmp\\\\log.txt\\nsecond line\\tafter a tab"));
            Assert.That(json, Does.Not.Contain(NastyMessage), "the raw message must never reach the document");
        }

        [Test]
        public void Escape_encodes_control_characters()
        {
            Assert.That(DiagnosticsJson.Escape("a\u0001b"), Is.EqualTo("a\\u0001b"));
            Assert.That(DiagnosticsJson.Escape(null), Is.EqualTo(string.Empty));
            Assert.That(DiagnosticsJson.Escape("plain"), Is.EqualTo("plain"));
        }

        [Test]
        public async Task Describe_text_is_part_of_the_snapshot_and_of_the_document()
        {
            await using var app = await StartAsync();
            var snapshot = DashboardData.Capture(app.Host, 0);

            Assert.That(snapshot.Describe, Does.Contain(nameof(ProfileService)));
            Assert.That(DiagnosticsJson.Build(snapshot), Does.Contain(nameof(ProfileService)));
        }

        [Test]
        public async Task Last_run_snapshot_round_trips_through_session_state()
        {
            await using var app = await StartAsync();
            var captured = DashboardData.Capture(app.Host, 0);

            Assert.That(DashboardData.LoadLastRun(), Is.Null, "nothing is stored before the first save");

            DashboardData.SaveLastRun(captured);
            var restored = DashboardData.LoadLastRun();

            Assert.That(restored, Is.Not.Null);
            Assert.That(restored!.Live, Is.False, "a restored snapshot is not live");
            Assert.That(restored.HostLabel, Is.EqualTo(captured.HostLabel));
            Assert.That(restored.State, Is.EqualTo(captured.State));
            Assert.That(restored.Describe, Is.EqualTo(captured.Describe));
            Assert.That(restored.ServiceCount, Is.EqualTo(captured.ServiceCount));
            Assert.That(Names(restored), Is.EqualTo(Names(captured)));

            var flaky = Service(Scope(restored, "session"), nameof(FlakyService));
            Assert.That(flaky.State, Is.EqualTo(ServiceState.Degraded));
            Assert.That(flaky.ErrorMessage, Is.EqualTo(NastyMessage));
            Assert.That(flaky.HasError, Is.True);

            DashboardData.ClearLastRun();
            Assert.That(DashboardData.LoadLastRun(), Is.Null);
        }

        [Test]
        public async Task Views_bind_a_snapshot_without_throwing()
        {
            await using var app = await StartAsync();
            var snapshot = DashboardData.Capture(app.Host, 0);

            var graph = new GraphView();
            graph.Refresh(snapshot);
            graph.SetScopeFilter("session");
            Assert.That(graph.ScopeFilter, Is.EqualTo("session"));
            graph.SetScopeFilter(null);
            graph.Refresh(null);

            var scopes = new ScopesView();
            var selected = string.Empty;
            scopes.ScopeSelected += name => selected = name;
            scopes.Refresh(snapshot);
            scopes.Refresh(null);
            Assert.That(selected, Is.Empty);

            var lastRun = new LastRunView();
            lastRun.Refresh(snapshot, true);
            lastRun.Refresh(null, false);
        }

        private static string[] Names(DashboardSnapshot snapshot)
        {
            var names = new string[snapshot.Scopes.Count];
            for (var i = 0; i < names.Length; i++) names[i] = snapshot.Scopes[i].Name;
            return names;
        }

        private static DashboardScope Scope(DashboardSnapshot snapshot, string name)
        {
            foreach (var scope in snapshot.Scopes)
            {
                if (scope.Name == name) return scope;
            }
            throw new AssertionException($"No scope named '{name}' in the snapshot.");
        }

        private static DashboardService Service(DashboardScope scope, string name)
        {
            foreach (var service in scope.Services)
            {
                if (service.Name == name) return service;
            }
            throw new AssertionException($"No service named '{name}' in scope '{scope.Name}'.");
        }

        /// <summary>
        /// Minimal structural validation: JsonUtility cannot parse an arbitrary document, so the test
        /// walks the text itself and checks that strings are terminated, braces and brackets balance,
        /// and no comma is left dangling before a closing token.
        /// </summary>
        private static void AssertWellFormed(string json)
        {
            var depth = 0;
            var inString = false;
            var escaped = false;
            var lastMeaningful = '\0';

            foreach (var c in json)
            {
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    else if (c < ' ') Assert.Fail("raw control character inside a JSON string");
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inString = true;
                        break;
                    case '{':
                    case '[':
                        depth++;
                        break;
                    case '}':
                    case ']':
                        Assert.That(lastMeaningful, Is.Not.EqualTo(','), "dangling comma before " + c);
                        depth--;
                        Assert.That(depth, Is.GreaterThanOrEqualTo(0), "unbalanced closing token");
                        break;
                }

                if (!char.IsWhiteSpace(c)) lastMeaningful = c;
            }

            Assert.That(inString, Is.False, "unterminated string");
            Assert.That(depth, Is.EqualTo(0), "unbalanced braces");
            Assert.That(json.TrimStart()[0], Is.EqualTo('{'));
        }
    }
}
