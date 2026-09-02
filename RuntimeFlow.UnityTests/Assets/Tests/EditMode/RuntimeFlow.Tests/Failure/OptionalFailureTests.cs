using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Failure
{
    /// <summary>An optional failure degrades the run: dependents still run and can see what degraded.</summary>
    [TestFixture]
    public sealed class OptionalFailureTests
    {
        [Init(Optional = true)]
        public sealed class RemoteConfig : ControlledService { }

        public sealed class Catalog : ControlledService
        {
            public Catalog(RemoteConfig config) => Config = config;

            public RemoteConfig Config { get; }
        }

        public sealed class Profile : ControlledService
        {
            public Profile(RemoteConfig config) => Config = config;

            public RemoteConfig Config { get; }
        }

        private CapturingLogger _log = null!;
        private RuntimeFlowOptions _options = null!;

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _options = TestScope.Options(_log);
        }

        [Test]
        [Timeout(10000)]
        public async Task AnOptionalFailureDegradesTheRunAndLetsDependentsRun()
        {
            var container = TestScope.Build(b =>
            {
                b.Add<RemoteConfig>();
                b.Add<Catalog>();
                b.Add<Profile>();
            });
            var config = container.Resolve<RemoteConfig>();
            var catalog = container.Resolve<Catalog>();
            var profile = container.Resolve<Profile>();
            catalog.AutoComplete = true;
            profile.AutoComplete = true;
            var run = ScopeRun.Create(container, "session", _options);

            var running = run.RunAsync();
            await config.Started;
            config.Fail(new TimeoutException("config timed out"));

            var result = await running;

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(result.Degraded, Is.EqualTo(new[] { "RemoteConfig" }));
            Assert.That(run.GetStatus().Service("RemoteConfig").State, Is.EqualTo(ServiceState.Degraded));
            Assert.That(catalog.Finished, Is.True);
            Assert.That(profile.Finished, Is.True);
        }

        [Test]
        [Timeout(10000)]
        public async Task DependentsSeeTheDegradedServiceThroughTheContext()
        {
            var container = TestScope.Build(b =>
            {
                b.Add<RemoteConfig>();
                b.Add<Catalog>();
            });
            var config = container.Resolve<RemoteConfig>();
            var catalog = container.Resolve<Catalog>();
            catalog.AutoComplete = true;
            var run = ScopeRun.Create(container, "session", _options);

            var running = run.RunAsync();
            await config.Started;
            config.Fail(new TimeoutException("config timed out"));
            await running;

            Assert.That(catalog.Context, Is.Not.Null);
            Assert.That(catalog.Context!.DegradedServices, Is.EqualTo(new[] { "RemoteConfig" }));
        }

        [Test]
        [Timeout(10000)]
        public async Task DegradationIsLoggedAsAWarningWithItsDependents()
        {
            var container = TestScope.Build(b =>
            {
                b.Add<RemoteConfig>();
                b.Add<Catalog>();
                b.Add<Profile>();
            });
            var config = container.Resolve<RemoteConfig>();
            container.Resolve<Catalog>().AutoComplete = true;
            container.Resolve<Profile>().AutoComplete = true;
            var run = ScopeRun.Create(container, "session", _options);

            var running = run.RunAsync();
            await config.Started;
            config.Fail(new TimeoutException("config timed out"));
            await running;

            var message = _log.Find(LogLevel.Warning, "is optional and failed");
            Assert.That(message, Is.Not.Null, _log.Dump());
            Assert.That(message, Does.StartWith("[RuntimeFlow] session: RemoteConfig is optional and failed after "));
            Assert.That(message, Does.EndWith(
                "(TimeoutException: config timed out); continuing degraded. Dependents: Catalog, Profile."));
        }

        [Test]
        [Timeout(10000)]
        public async Task TheCompletionLineNamesTheDegradedServices()
        {
            var container = TestScope.Build(b => b.Add<RemoteConfig>());
            var config = container.Resolve<RemoteConfig>();
            var run = ScopeRun.Create(container, "session", _options);

            var running = run.RunAsync();
            await config.Started;
            config.Fail(new TimeoutException("nope"));
            await running;

            var message = _log.Find(LogLevel.Information, "completed in");
            Assert.That(message, Is.Not.Null, _log.Dump());
            Assert.That(message, Does.EndWith("(1 services, 1 degraded: RemoteConfig)"));
        }
    }
}
