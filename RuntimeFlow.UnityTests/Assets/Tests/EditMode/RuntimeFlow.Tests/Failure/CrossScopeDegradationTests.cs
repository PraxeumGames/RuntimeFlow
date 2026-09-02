using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Failure
{
    /// <summary>
    /// A global service that degraded stays visible to the session: in the host's
    /// <see cref="StartupResult.Degraded"/>, in <see cref="InitContext.DegradedServices"/> of the session
    /// services, in the session's <c>Describe()</c> and in the annotation of a failure that follows it.
    /// </summary>
    [TestFixture]
    public sealed class CrossScopeDegradationTests
    {
        [Init(Optional = true)]
        public sealed class Auth : IAsyncInitializable
        {
            /// <inheritdoc />
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
                => throw new InvalidOperationException("auth is down");
        }

        [Init(Optional = true)]
        public sealed class Telemetry : IAsyncInitializable
        {
            /// <inheritdoc />
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
                => throw new InvalidOperationException("telemetry is down");
        }

        public sealed class Profile : IAsyncInitializable
        {
            /// <summary>Takes the global service as an ordering edge.</summary>
            public Profile(Auth auth) => Auth = auth;

            /// <summary>The upstream service, degraded or not.</summary>
            public Auth Auth { get; }

            /// <summary>What the context reported while this service was initializing.</summary>
            public IReadOnlyList<string> DegradedDuringInit { get; private set; } = Array.Empty<string>();

            /// <inheritdoc />
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                DegradedDuringInit = new List<string>(context.DegradedServices);
                return Task.CompletedTask;
            }
        }

        public sealed class ThrowingProfile : IAsyncInitializable
        {
            /// <summary>Takes the global service as an ordering edge.</summary>
            public ThrowingProfile(Auth auth) => Auth = auth;

            /// <summary>The upstream service, degraded or not.</summary>
            public Auth Auth { get; }

            /// <inheritdoc />
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
                => throw new NullReferenceException("no profile without a player id");
        }

        private CapturingLogger _log = null!;
        private RuntimeFlowOptions _options = null!;

        private readonly RunTracker _tracker = new RunTracker();

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _options = TestScope.Options(_log);
        }

        /// <summary>Disposes every run and container this fixture created, so nothing leaks into the next test.</summary>
        [TearDown]
        public void DisposeTrackedRuns() => _tracker.DisposeAll();

        [Test]
        [Timeout(10000)]
        public async Task TheHostResultListsGlobalAndSessionDegradedServices()
        {
            await using var host = new RuntimeFlowHost(
                builder => builder.Add<Auth>(),
                builder =>
                {
                    builder.Add<Telemetry>();
                    builder.Add<Profile>();
                },
                _options);

            var result = await host.StartAsync();

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(result.Scope, Is.EqualTo("session"));
            Assert.That(result.Degraded, Is.EqualTo(new[] { "Auth", "Telemetry" }), "global first, then the session's own");
        }

        [Test]
        [Timeout(10000)]
        public async Task ASessionServiceSeesTheGlobalDegradationThroughItsContext()
        {
            await using var host = new RuntimeFlowHost(
                builder => builder.Add<Auth>(),
                builder => builder.Add<Profile>(),
                _options);

            await host.StartAsync();

            Assert.That(host.Session.Resolve<Profile>().DegradedDuringInit, Is.EqualTo(new[] { "Auth" }));
        }

        [Test]
        [Timeout(10000)]
        public async Task TheSessionDescribesTheGlobalServiceAsDegraded()
        {
            await using var host = new RuntimeFlowHost(
                builder => builder.Add<Auth>(),
                builder => builder.Add<Profile>(),
                _options);

            await host.StartAsync();

            var describe = host.Describe();
            Assert.That(describe, Does.Contain("after Auth  (ctor: Auth auth) [global, degraded]"), describe);
            Assert.That(describe, Does.Contain("external (from parent scopes): Auth [global, degraded]"), describe);
        }

        [Test]
        [Timeout(10000)]
        public async Task AScopeRunResultStaysPerScope()
        {
            var global = _tracker.Build(builder => builder.Add<Auth>());
            var globalRun = _tracker.Create(global, "global", _options);
            var globalResult = await globalRun.RunAsync();

            var session = global.CreateScope(builder => builder.Add<Profile>());
            var sessionRun = _tracker.Create(session, "session", _options, new List<ScopeRun> { globalRun });
            var sessionResult = await sessionRun.RunAsync();

            Assert.That(globalResult.Degraded, Is.EqualTo(new[] { "Auth" }));
            Assert.That(sessionResult.Degraded, Is.Empty, "ScopeRun reports only what degraded in its own scope");
            Assert.That(session.Resolve<Profile>().DegradedDuringInit, Is.EqualTo(new[] { "Auth" }),
                "the live view still names the parent-scope degradation");
            Assert.That(sessionRun.GetStatus().Service("Profile").State, Is.EqualTo(ServiceState.Completed));

            await sessionRun.DisposeAsync();
            await globalRun.DisposeAsync();
            session.Dispose();
            global.Dispose();
        }

        [Test]
        [Timeout(10000)]
        public async Task AFailureAfterAGlobalDegradationIsAnnotated()
        {
            await using var host = new RuntimeFlowHost(
                builder => builder.Add<Auth>(),
                builder => builder.Add<ThrowingProfile>(),
                _options);

            var failure = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => host.StartAsync());

            Assert.That(failure.Message, Does.StartWith(
                "Initialization of scope 'session' failed: ThrowingProfile threw NullReferenceException after "));
            Assert.That(failure.Message, Does.Contain("s (after upstream Auth degraded). Completed (0): none;"));
        }
    }
}
