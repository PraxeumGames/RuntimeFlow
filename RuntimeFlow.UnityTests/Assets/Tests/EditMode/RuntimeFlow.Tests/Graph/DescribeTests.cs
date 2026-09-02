using System;
using System.Collections.Generic;
using NUnit.Framework;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Graph
{
    /// <summary>Describe() renders the whole graph, its edge origins and the services inherited from parents.</summary>
    [TestFixture]
    public sealed class DescribeTests
    {
        public interface ICdnMirrorSelector { }

        public interface IAnalytics { }

        [Init(Phase = "platform")]
        public sealed class Mirrors : AutoService, ICdnMirrorSelector { }

        [Init(Phase = "content", Weight = 2, UserGated = true)]
        public sealed class RemoteCatalog : AutoService
        {
            public RemoteCatalog(ICdnMirrorSelector mirrorSelector, Func<IAnalytics> analytics)
            {
                MirrorSelector = mirrorSelector;
                Analytics = analytics;
            }

            public ICdnMirrorSelector MirrorSelector { get; }
            public Func<IAnalytics> Analytics { get; }
        }

        [Init(Phase = "content", Optional = true, TimeoutSeconds = 10)]
        public sealed class Telemetry : AutoService { }

        public sealed class GlobalConfig : AutoService { }

        public sealed class SessionUser : AutoService
        {
            public SessionUser(GlobalConfig config) => Config = config;

            public GlobalConfig Config { get; }
        }

        private CapturingLogger _log = null!;

        [SetUp]
        public void SetUp() => _log = new CapturingLogger();

        [Test]
        public void DescribeRendersServicesFlagsAndEdges()
        {
            var options = TestScope.Options(_log);
            options.Phases = new[] { "platform", "content" };
            var container = TestScope.Build(b =>
            {
                b.Add<Mirrors>();
                b.Add<RemoteCatalog>();
                b.Add<Telemetry>();
                b.RegisterFactory<IAnalytics>(() => new Analytics());
            });

            var describe = ScopeRun.Create(container, "session", options).Describe();

            Assert.That(describe, Is.EqualTo(string.Join(Environment.NewLine, new[]
            {
                "scope 'session' — 3 services, phases: platform > content",
                " 1 [platform] Mirrors        required, weight 1",
                " 2 [content]  RemoteCatalog  required, user-gated, weight 2",
                "      after Mirrors           (ctor: ICdnMirrorSelector mirrorSelector)",
                "      after phase 'platform'  (phase barrier)",
                "      lazy: Func<IAnalytics> analytics",
                " 3 [content]  Telemetry      optional, timeout 10s, weight 1",
                "      after phase 'platform'  (phase barrier)",
                ""
            })), describe);
        }

        [Test]
        public void DescribeListsParentScopeServicesAsExternal()
        {
            var options = TestScope.Options(_log);
            var global = TestScope.Build(b => b.Add<GlobalConfig>());
            var globalRun = ScopeRun.Create(global, "global", options);
            var session = global.CreateScope(b => b.Add<SessionUser>());

            var describe = ScopeRun.Create(session, "session", options, new List<ScopeRun> { globalRun }).Describe();

            Assert.That(describe, Is.EqualTo(string.Join(Environment.NewLine, new[]
            {
                "scope 'session' — 1 services",
                " 1 [-] SessionUser  required, weight 1",
                "      after GlobalConfig  (ctor: GlobalConfig config) [global, initialized]",
                "external (from parent scopes): GlobalConfig [global, initialized]",
                ""
            })), describe);
        }

        private sealed class Analytics : IAnalytics { }
    }
}
