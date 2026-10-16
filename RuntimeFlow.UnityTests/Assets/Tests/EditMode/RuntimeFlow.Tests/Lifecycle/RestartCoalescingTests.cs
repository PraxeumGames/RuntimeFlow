using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Lifecycle
{
    /// <summary>
    /// <see cref="RuntimeFlowHost.IsRestarting"/>, <see cref="IRuntimeFlowObserver.OnRestartRequested"/> and
    /// <see cref="RuntimeFlowOptions.RestartCoalescing"/>: a game must be able to tell that a restart is in
    /// progress (downloads hold off, background work stops), and a service of the restarted generation
    /// that asks again while that generation initializes must not open yet another one by default.
    /// </summary>
    [TestFixture]
    public sealed class RestartCoalescingTests
    {
        public sealed class Journal
        {
            public List<string> Log { get; } = new List<string>();
            public HashSet<int> RequestAt { get; } = new HashSet<int>();
            public int FailGeneration { get; set; } = -1;
            public List<Task<StartupResult>> Requests { get; } = new List<Task<StartupResult>>();
            public int StuckGeneration { get; set; } = -1;
            public TaskCompletionSource<bool> StuckEntered { get; } = new TaskCompletionSource<bool>();
            public TaskCompletionSource<bool>? Answer { get; set; }
        }

        /// <summary>
        /// Never finishes in <see cref="Journal.StuckGeneration"/> until <see cref="Journal.Answer"/> is set
        /// or its token is cancelled: the login screen nobody answers, the request that never returns.
        /// </summary>
        public abstract class StuckService : IAsyncInitializable
        {
            private readonly Journal _journal;

            protected StuckService(Journal journal) => _journal = journal;

            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                if (context.Generation != _journal.StuckGeneration) return;
                var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _journal.Answer = answer;
                _journal.StuckEntered.TrySetResult(true);
                using (cancellationToken.Register(() => answer.TrySetCanceled()))
                {
                    try
                    {
                        await answer.Task;
                    }
                    catch (OperationCanceledException)
                    {
                        _journal.Log.Add($"stuck-cancelled:{context.Generation}");
                        throw;
                    }
                }
            }
        }

        [Init(UserGated = true)]
        public sealed class GatedLogin : StuckService
        {
            public GatedLogin(Journal journal) : base(journal) { }
        }

        public sealed class HungRequest : StuckService
        {
            public HungRequest(Journal journal) : base(journal) { }
        }

        /// <summary>Records whether the host says it is restarting, and requests a restart in chosen generations.</summary>
        public sealed class Requester : IAsyncInitializable
        {
            private readonly Journal _journal;
            private readonly RuntimeFlowHost _host;

            public Requester(Journal journal, RuntimeFlowHost host)
            {
                _journal = journal;
                _host = host;
            }

            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                await Task.Yield();
                _journal.Log.Add($"init:{context.Generation}:restarting={_host.IsRestarting}");
                if (_journal.RequestAt.Remove(context.Generation))
                    _journal.Requests.Add(_host.RestartAsync($"again-{context.Generation}"));
                if (context.Generation == _journal.FailGeneration) throw new InvalidOperationException("session failed");
            }
        }

        public sealed class RestartObserver : IRuntimeFlowObserver
        {
            public List<string> Requests { get; } = new List<string>();

            public void OnRestartRequested(RestartRequestInfo request)
                => Requests.Add($"{request.Reason}:{(request.Coalesced ? "coalesced" : "accepted")}:{request.Generation}");
        }

        /// <summary>Requests another restart from inside the notification.</summary>
        public sealed class ReentrantObserver : IRuntimeFlowObserver
        {
            public RuntimeFlowHost? Host { get; set; }
            public int Calls { get; private set; }
            public Task<StartupResult>? Nested { get; private set; }

            public void OnRestartRequested(RestartRequestInfo request)
            {
                Calls++;
                Nested = Host!.RestartAsync("from-observer");
            }
        }

        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

        private CapturingLogger _log = null!;
        private RuntimeFlowOptions _options = null!;
        private RestartObserver _observer = null!;
        private Journal _journal = null!;

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _observer = new RestartObserver();
            _options = TestScope.Options(_log, _observer);
            _journal = new Journal();
        }

        private RuntimeFlowHost Host() => new RuntimeFlowHost(
            builder => builder.RegisterInstance(_journal),
            builder => builder.Add<Requester>(),
            _options);

        private RuntimeFlowHost StuckHost(bool userGated) => new RuntimeFlowHost(
            builder => builder.RegisterInstance(_journal),
            builder =>
            {
                if (userGated) builder.Add<GatedLogin>();
                else builder.Add<HungRequest>();
            },
            _options);

        private async Task<T> Within<T>(Task<T> task, RuntimeFlowHost host, string what)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Bound));
            Assert.That(winner, Is.SameAs(task),
                $"{what} never settled: state {host.State}, generation {host.Generation}.\n{_log.Dump()}");
            return await task;
        }

        [Test]
        [Timeout(15000)]
        public async Task IsRestartingSpansFromTheAcceptedRequestUntilTheNewSessionConcluded()
        {
            await using var host = Host();
            Assert.That(host.IsRestarting, Is.False);
            await Within(host.StartAsync(), host, "the startup");
            Assert.That(host.IsRestarting, Is.False);

            var restart = host.RestartAsync("bundles-updated");
            Assert.That(host.IsRestarting, Is.True, "set synchronously when the request is accepted");
            await Within(restart, host, "the restart");

            Assert.That(host.IsRestarting, Is.False);
            Assert.That(_journal.Log, Is.EqualTo(new[] { "init:0:restarting=False", "init:1:restarting=True" }));
        }

        [Test]
        [Timeout(15000)]
        public async Task IsRestartingEndsWhenTheNewSessionFailsAndARetryIsAccepted()
        {
            _journal.FailGeneration = 1;
            _options.MaxRestartsPerWindow = 2;
            await using var host = Host();
            await Within(host.StartAsync(), host, "the startup");

            await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => Within(host.RestartAsync("bundles-updated"), host, "the restart"));
            Assert.That(host.IsRestarting, Is.False);

            var retry = await Within(host.RestartAsync("retry"), host, "the retry");

            Assert.That(retry.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(host.Generation, Is.EqualTo(2));
            Assert.That(host.IsRestarting, Is.False);
            Assert.That(_observer.Requests, Is.EqualTo(new[] { "bundles-updated:accepted:0", "retry:accepted:1" }));
        }

        [Test]
        [Timeout(15000)]
        public async Task ObserversAreToldAboutAcceptedAndCoalescedRequestsButNotRefusals()
        {
            _options.MaxRestartsPerWindow = 1;
            await using var host = Host();
            await Within(host.StartAsync(), host, "the startup");

            var first = host.RestartAsync("first");
            var second = host.RestartAsync("second");
            Assert.That(_observer.Requests, Is.EqualTo(new[] { "first:accepted:0", "second:coalesced:0" }),
                "raised synchronously inside RestartAsync");

            var firstResult = await Within(first, host, "the restart");
            var secondResult = await Within(second, host, "the coalesced request");
            Assert.That(secondResult, Is.SameAs(firstResult));
            Assert.That(host.RestartCount, Is.EqualTo(1));

            await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => host.RestartAsync("over-budget"));
            Assert.That(_observer.Requests, Has.Count.EqualTo(2), "a refused request is not reported");
        }

        [Test]
        [Timeout(15000)]
        public async Task ARestartRequestedFromTheNotificationIsCoalescedAndNotReportedAgain()
        {
            var reentrant = new ReentrantObserver();
            _options.Observers.Add(reentrant);
            await using var host = Host();
            reentrant.Host = host;
            await Within(host.StartAsync(), host, "the startup");

            var result = await Within(host.RestartAsync("bundles-updated"), host, "the restart");
            var nested = await Within(reentrant.Nested!, host, "the nested request");

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(nested, Is.SameAs(result));
            Assert.That(reentrant.Calls, Is.EqualTo(1));
            Assert.That(host.RestartCount, Is.EqualTo(1));
            Assert.That(_observer.Requests, Is.EqualTo(new[] { "bundles-updated:accepted:0" }));
        }

        /// <summary>
        /// The request comes from inside the restarted generation's own initialization, with a reason of its
        /// own ("again-1", not "bundles-updated"): it is coalesced all the same, so no restart loop forms.
        /// </summary>
        [Test]
        [Timeout(15000)]
        public async Task ByDefaultARequestDuringTheRestartedGenerationsInitializationIsCoalescedAndNotBudgeted()
        {
            _options.MaxRestartsPerWindow = 1;
            _journal.RequestAt.Add(1);
            await using var host = Host();
            await Within(host.StartAsync(), host, "the startup");

            var result = await Within(host.RestartAsync("bundles-updated"), host, "the restart");

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed), _log.Dump());
            Assert.That(host.Generation, Is.EqualTo(1));
            Assert.That(host.RestartCount, Is.EqualTo(1));
            Assert.That(_journal.Requests, Has.Count.EqualTo(1));
            var joined = await Within(_journal.Requests[0], host, "the request of generation 1");
            Assert.That(joined, Is.SameAs(result), "the request joined the restart it was made in, within budget");
            Assert.That(_observer.Requests, Is.EqualTo(new[] { "bundles-updated:accepted:0", "again-1:coalesced:1" }));
            Assert.That(_log.Has(LogLevel.Information,
                "[RuntimeFlow] restart 'again-1' coalesced with the restart in progress"), Is.True, _log.Dump());
            Assert.That(host.IsRestarting, Is.False);
        }

        /// <summary>
        /// "Change server" or "Retry" while the restarted generation waits forever (a login nobody answers,
        /// a request without a timeout): coalescing it would hang every awaiter for good, so a request with
        /// another reason from outside that generation is a restart of its own.
        /// </summary>
        [TestCase(true)]
        [TestCase(false)]
        [Timeout(15000)]
        public async Task ByDefaultADifferentReasonWhileTheRestartedGenerationInitializesRestartsAgain(bool userGated)
        {
            _options.MaxRestartsPerWindow = 2;
            _journal.StuckGeneration = 1;
            await using var host = StuckHost(userGated);
            await Within(host.StartAsync(), host, "the startup");

            var first = host.RestartAsync("bundles-updated");
            await Within(_journal.StuckEntered.Task, host, "the stuck generation");
            Assert.That(host.IsRestarting, Is.True);
            Assert.That(host.Generation, Is.EqualTo(1));

            var second = host.RestartAsync("change-server");
            var result = await Within(second, host, "the second restart");
            var firstResult = await Within(first, host, "the first restart");

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed), _log.Dump());
            Assert.That(firstResult, Is.SameAs(result), "the awaiter of the first restart sees the final outcome");
            Assert.That(host.Generation, Is.EqualTo(2));
            Assert.That(host.RestartCount, Is.EqualTo(2), "the accepted request is budgeted");
            Assert.That(_journal.Log, Is.EqualTo(new[] { "stuck-cancelled:1" }), "the stuck generation was torn down");
            Assert.That(_observer.Requests, Is.EqualTo(new[] { "bundles-updated:accepted:0", "change-server:accepted:1" }));
            Assert.That(host.IsRestarting, Is.False);
        }

        [Test]
        [Timeout(15000)]
        public async Task ByDefaultTheSameReasonWhileTheRestartedGenerationInitializesIsCoalescedAndNotBudgeted()
        {
            _options.MaxRestartsPerWindow = 1;
            _journal.StuckGeneration = 1;
            await using var host = StuckHost(userGated: true);
            await Within(host.StartAsync(), host, "the startup");

            var first = host.RestartAsync("bundles-updated");
            await Within(_journal.StuckEntered.Task, host, "the stuck generation");
            var repeat = host.RestartAsync("bundles-updated");

            Assert.That(repeat.IsCompleted, Is.False, "the repeat waits for the restart it joined");
            Assert.That(_observer.Requests, Is.EqualTo(new[] { "bundles-updated:accepted:0", "bundles-updated:coalesced:1" }));
            _journal.Answer!.TrySetResult(true);

            var result = await Within(first, host, "the restart");
            var joined = await Within(repeat, host, "the repeated request");

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed), _log.Dump());
            Assert.That(joined, Is.SameAs(result));
            Assert.That(host.Generation, Is.EqualTo(1));
            Assert.That(host.RestartCount, Is.EqualTo(1));
            Assert.That(_journal.Log, Is.Empty, "nothing was torn down");
            Assert.That(host.IsRestarting, Is.False);
        }

        [Test]
        [Timeout(15000)]
        public async Task UntilSessionBuiltOpensANewGenerationForSuchARequest()
        {
            _options.RestartCoalescing = RestartCoalescing.UntilSessionBuilt;
            _journal.RequestAt.Add(1);
            await using var host = Host();
            await Within(host.StartAsync(), host, "the startup");

            var result = await Within(host.RestartAsync("bundles-updated"), host, "the restart");

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed), _log.Dump());
            Assert.That(host.Generation, Is.EqualTo(2));
            Assert.That(host.RestartCount, Is.EqualTo(2));
            Assert.That(_observer.Requests, Is.EqualTo(new[] { "bundles-updated:accepted:0", "again-1:accepted:1" }));
            Assert.That(_journal.Log, Is.EqualTo(new[]
            {
                "init:0:restarting=False", "init:1:restarting=True", "init:2:restarting=True"
            }));
            Assert.That(host.IsRestarting, Is.False);
        }

        [Test]
        [Timeout(15000)]
        public async Task ARequestFromTheFirstGenerationIsAcceptedByDefault()
        {
            _journal.RequestAt.Add(0);
            await using var host = Host();

            var result = await Within(host.StartAsync(), host, "the startup");

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed), _log.Dump());
            Assert.That(host.Generation, Is.EqualTo(1), "a plain startup is not a restart in progress");
            Assert.That(_observer.Requests, Is.EqualTo(new[] { "again-0:accepted:0" }));
            Assert.That(host.IsRestarting, Is.False);
        }
    }
}
