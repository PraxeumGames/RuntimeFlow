using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Content;
using RuntimeFlow.Contexts;
using RuntimeFlow.Testing;

namespace RuntimeFlow.Tests
{
    /// <summary>
    /// Dogfood coverage for the universal content-source primitive: remote-config-style
    /// loading across scopes, degraded (optional) mode, failure policy, and same-scope
    /// dependency ordering via [DependsOn].
    /// </summary>
    public sealed class ContentSourceTests
    {
        public sealed class RemoteConfigSnapshot
        {
            public string Environment { get; set; } = "fallback";
        }

        private abstract class RemoteConfigSourceBase : ContentSource<RemoteConfigSnapshot>
        {
            private readonly bool _optional;

            protected RemoteConfigSourceBase(bool optional) => _optional = optional;

            public override string SourceName => "remote-config";
            public override bool IsOptional => _optional;
            protected override RemoteConfigSnapshot? FallbackData { get; } = new();

            protected override Task<RemoteConfigSnapshot> LoadAsync(CancellationToken cancellationToken)
                => throw new InvalidOperationException("network unreachable");
        }

        private sealed class OptionalFailingSource : RemoteConfigSourceBase
        {
            public OptionalFailingSource() : base(optional: true) { }
        }

        private sealed class RequiredFailingSource : RemoteConfigSourceBase
        {
            public RequiredFailingSource() : base(optional: false) { }
        }

        private sealed class ProdConfigSource : ContentSource<RemoteConfigSnapshot>
        {
            public override string SourceName => "remote-config";

            protected override Task<RemoteConfigSnapshot> LoadAsync(CancellationToken cancellationToken)
                => Task.FromResult(new RemoteConfigSnapshot { Environment = "prod" });
        }

        private sealed class SessionScopedSource : ContentSource<RemoteConfigSnapshot>
        {
            public override string SourceName => "session-config";

            protected override Task<RemoteConfigSnapshot> LoadAsync(CancellationToken cancellationToken)
                => Task.FromResult(new RemoteConfigSnapshot { Environment = "session" });
        }

        [DependsOn(typeof(SessionScopedSource))]
        private sealed class ConfigConsumer : ISessionInitializableService
        {
            private readonly IContentSource<RemoteConfigSnapshot> _source;

            public ConfigConsumer(IContentSource<RemoteConfigSnapshot> source) => _source = source;

            public string? ObservedEnvironment { get; private set; }

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                ObservedEnvironment = _source.Data.Environment;
                return Task.CompletedTask;
            }
        }

        [Test]
        public async Task GlobalContent_LoadsBeforeSessionConsumers()
        {
            await using var app = await TestPipeline.Create(cfg =>
                {
                    cfg.Global().Content<ProdConfigSource, RemoteConfigSnapshot>();
                    cfg.Session().Register<ConfigConsumer>(DiLifetime.Singleton);
                })
                .StartAsync();

            var consumer = app.SessionContext.Resolve<ConfigConsumer>();
            Assert.AreEqual("prod", consumer.ObservedEnvironment,
                "Session consumers must observe loaded global content without explicit ordering.");
        }

        [Test]
        public async Task SameScopeContent_DependsOnAttribute_OrdersInitialization()
        {
            await using var app = await TestPipeline.Create(cfg =>
                    cfg.Session()
                       .Content<SessionScopedSource, RemoteConfigSnapshot>()
                       .Register<ConfigConsumer>(DiLifetime.Singleton))
                .StartAsync();

            var consumer = app.SessionContext.Resolve<ConfigConsumer>();
            StringAssert.Contains("session", consumer.ObservedEnvironment);
        }

        [Test]
        public async Task OptionalContent_Failure_DegradesToFallbackAndStaysGreen()
        {
            await using var app = await TestPipeline.Create(cfg =>
                    cfg.Global().Content<OptionalFailingSource, RemoteConfigSnapshot>())
                .StartAsync();

            var source = app.SessionContext.Resolve<IContentSource<RemoteConfigSnapshot>>();
            Assert.IsTrue(source.UsedFallback, "Failed optional content must degrade to fallback.");
            Assert.IsTrue(source.IsLoaded);
            Assert.AreEqual("fallback", source.Data.Environment);
        }

        [Test]
        public void RequiredContent_Failure_FailsStartup()
        {
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await TestPipeline.Create(cfg =>
                        cfg.Global().Content<RequiredFailingSource, RemoteConfigSnapshot>())
                    .StartAsync(),
                "Required content failures must fail startup.");
        }
    }
}
