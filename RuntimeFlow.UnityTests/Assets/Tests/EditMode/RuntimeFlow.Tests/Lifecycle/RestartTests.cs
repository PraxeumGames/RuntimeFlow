using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Lifecycle
{
    /// <summary>
    /// A restart rebuilds only the session scope: global stays warm, the new generation sees
    /// <see cref="InitContext.IsRestart"/>, in-flight work and background tokens are cancelled, and
    /// every awaiter of the chain receives the result of the last run.
    /// </summary>
    [TestFixture]
    public sealed class RestartTests
    {
        /// <summary>Shared state that survives the session rebuild because it lives in the global scope.</summary>
        public sealed class Recorder
        {
            public int GlobalAttempts { get; set; }
            public int SessionAttempts { get; set; }
            public bool LastIsRestart { get; set; }
            public int LastGeneration { get; set; }
            public CancellationToken BackgroundToken { get; set; }
            public bool BlockFirstGeneration { get; set; }
            public List<string> Reasons { get; } = new List<string>();

            public TaskCompletionSource<bool> FirstStarted { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public sealed class WarmGlobal : IAsyncInitializable
        {
            private readonly Recorder _recorder;

            public WarmGlobal(Recorder recorder) => _recorder = recorder;

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _recorder.GlobalAttempts++;
                return Task.CompletedTask;
            }
        }

        public sealed class SessionProbe : IAsyncInitializable
        {
            private readonly Recorder _recorder;

            public SessionProbe(Recorder recorder) => _recorder = recorder;

            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _recorder.SessionAttempts++;
                _recorder.LastIsRestart = context.IsRestart;
                _recorder.LastGeneration = context.Generation;
                _recorder.BackgroundToken = cancellationToken;

                if (context.Generation != 0 || !_recorder.BlockFirstGeneration) return;

                _recorder.FirstStarted.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }

        public sealed class SelfRestarting : IAsyncInitializable
        {
            private readonly Recorder _recorder;
            private readonly RuntimeFlowHost _host;

            public SelfRestarting(Recorder recorder, RuntimeFlowHost host)
            {
                _recorder = recorder;
                _host = host;
            }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                if (context.Generation == 0) _ = _host.RestartAsync("from-service");
                return Task.CompletedTask;
            }
        }

        private CapturingLogger _log = null!;
        private RuntimeFlowOptions _options = null!;
        private Recorder _recorder = null!;

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _options = TestScope.Options(_log);
            _recorder = new Recorder();
        }

        private RuntimeFlowHost Host(Action<IContainerBuilder>? session = null) => new RuntimeFlowHost(
            builder =>
            {
                builder.RegisterInstance(_recorder);
                builder.Add<WarmGlobal>();
            },
            session ?? (builder => builder.Add<SessionProbe>()),
            _options);

        [Test]
        [Timeout(10000)]
        public async Task GlobalStaysWarmWhileTheSessionIsRebuilt()
        {
            await using var host = Host();
            await host.StartAsync();

            var warm = host.Global.Resolve<WarmGlobal>();
            var firstSession = host.Session;
            var firstProbe = host.Session.Resolve<SessionProbe>();

            var result = await host.RestartAsync("bundles-updated");

            Assert.That(_recorder.GlobalAttempts, Is.EqualTo(1), "the global graph must run exactly once");
            Assert.That(host.Global.Resolve<WarmGlobal>(), Is.SameAs(warm));
            Assert.That(host.Session, Is.Not.SameAs(firstSession));
            Assert.That(host.Session.Resolve<SessionProbe>(), Is.Not.SameAs(firstProbe));

            Assert.That(_recorder.SessionAttempts, Is.EqualTo(2));
            Assert.That(_recorder.LastIsRestart, Is.True);
            Assert.That(_recorder.LastGeneration, Is.EqualTo(1));
            Assert.That(host.Generation, Is.EqualTo(1));
            Assert.That(host.RestartCount, Is.EqualTo(1));
            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(host.GetStatus().RestartCount, Is.EqualTo(1));
        }

        [Test]
        [Timeout(10000)]
        public async Task ARestartDuringStartupCancelsItAndRedirectsTheOriginalAwaiter()
        {
            _recorder.BlockFirstGeneration = true;
            await using var host = Host();

            var startup = host.StartAsync();
            await _recorder.FirstStarted.Task;

            var restart = host.RestartAsync("mid-flight");
            var fromStartup = await startup;
            var fromRestart = await restart;

            Assert.That(fromStartup, Is.SameAs(fromRestart), "both awaiters get the last run in the chain");
            Assert.That(fromStartup.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(_recorder.LastGeneration, Is.EqualTo(1));
            Assert.That(_recorder.SessionAttempts, Is.EqualTo(2));
            Assert.That(_log.Has(LogLevel.Information,
                "[RuntimeFlow] restart requested: 'mid-flight' (session run in progress: cancelling)"),
                Is.True, _log.Dump());
        }

        [Test]
        [Timeout(10000)]
        public async Task TenConcurrentRequestsRebuildTheSessionOnce()
        {
            await using var host = Host();
            await host.StartAsync();

            var requests = new List<Task<StartupResult>>();
            for (var i = 0; i < 10; i++) requests.Add(host.RestartAsync("storm"));
            var results = await Task.WhenAll(requests);

            Assert.That(host.RestartCount, Is.EqualTo(1));
            Assert.That(_recorder.SessionAttempts, Is.EqualTo(2));
            foreach (var result in results) Assert.That(result, Is.SameAs(results[0]));
            Assert.That(_log.Has(LogLevel.Information,
                "[RuntimeFlow] restart 'storm' coalesced with the restart already in progress"),
                Is.True, _log.Dump());
        }

        [Test]
        [Timeout(10000)]
        public async Task ExceedingTheBudgetFailsWithTheListOfReasons()
        {
            _options.MaxRestartsPerWindow = 2;
            await using var host = Host();
            await host.StartAsync();

            await host.RestartAsync("first");
            await host.RestartAsync("second");

            var failure = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => host.RestartAsync("third"));

            Assert.That(failure.Scope, Is.EqualTo("session"));
            Assert.That(failure.Message, Is.EqualTo(
                "Restart budget exceeded: 3 restarts within 60s (limit 2). Reasons: first, second, third"));
            Assert.That(host.RestartCount, Is.EqualTo(2), "the refused restart never rebuilds the scope");
        }

        [Test]
        [Timeout(10000)]
        public async Task AServiceCanRequestARestartFromInsideItsOwnInitialization()
        {
            await using var host = Host(builder =>
            {
                builder.Add<SessionProbe>();
                builder.Add<SelfRestarting>();
            });

            var result = await host.StartAsync();

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(host.RestartCount, Is.EqualTo(1));
            Assert.That(_recorder.SessionAttempts, Is.EqualTo(2));
            Assert.That(_recorder.LastGeneration, Is.EqualTo(1));
            Assert.That(_recorder.LastIsRestart, Is.True);
        }

        [Test]
        [Timeout(10000)]
        public async Task ARestartCancelsTheTokenAServiceCapturedForBackgroundWork()
        {
            await using var host = Host();
            await host.StartAsync();

            var background = _recorder.BackgroundToken;
            Assert.That(background.IsCancellationRequested, Is.False, "the token outlives InitializeAsync");

            await host.RestartAsync("bundles-updated");

            Assert.That(background.IsCancellationRequested, Is.True);
            Assert.That(_recorder.BackgroundToken.IsCancellationRequested, Is.False, "the new generation gets a fresh token");
        }

        [Test]
        [Timeout(10000)]
        public async Task RunRestartRunLeavesTheHostHealthy()
        {
            await using var host = Host();
            await host.StartAsync();
            await host.RestartAsync("one");
            var result = await host.RestartAsync("two");

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(host.State, Is.EqualTo(RunState.Completed));
            Assert.That(host.Generation, Is.EqualTo(2));
            Assert.That(host.RestartCount, Is.EqualTo(2));
            Assert.That(_recorder.GlobalAttempts, Is.EqualTo(1));
            Assert.That(_recorder.SessionAttempts, Is.EqualTo(3));
            ProgressAssertions.AllCompleted(host.GetStatus());
        }
    }
}
