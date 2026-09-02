using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Content;
using RuntimeFlow.Contexts;
using RuntimeFlow.Initialization.Planning;
using RuntimeFlow.Testing;

namespace RuntimeFlow.Tests.GoldenPath
{
    /// <summary>
    /// Phase 2 verification: weighted progress flows through the composite notifier with
    /// fractional weights, and a user gate blocks exactly one node while progress events
    /// report opened/closed transitions.
    /// </summary>
    public sealed class WeightedProgressAndGateTests
    {
        private sealed class WeightSnapshot { public string Env { get; set; } = "prod"; }

        // ---------- weighted progress ----------

        private sealed class RecordingWeightedNotifier :
            IInitializationProgressNotifier,
            IWeightedInitializationProgressNotifier,
            IUserGateProgressNotifier
        {
            public List<string> Events { get; } = new();

            public void OnScopeStarted(GameContextType scope, int totalServices) { }
            public void OnServiceStarted(GameContextType scope, Type serviceType, int completedServices, int totalServices) { }
            public void OnServiceCompleted(GameContextType scope, Type serviceType, int completedServices, int totalServices) { }
            public void OnScopeCompleted(GameContextType scope, int totalServices) { }
            public void OnServiceProgress(GameContextType scope, Type serviceType, float progress, string? message, int completedServices, int totalServices) { }
            public Task OnGlobalContextReadyForSessionInitializationAsync(CancellationToken ct) => Task.CompletedTask;
            public Task OnSessionRestartTeardownCompletedAsync(CancellationToken ct) => Task.CompletedTask;

            public void OnScopeStarted(GameContextType scope, double totalWeight, int totalServices)
                => Events.Add($"scope:{scope}:{totalWeight:F1}");

            public void OnServiceStarted(GameContextType scope, Type serviceType, double completedWeight, double totalWeight)
                => Events.Add($"start:{serviceType.Name}:{completedWeight:F1}/{totalWeight:F1}");

            public void OnServiceProgress(GameContextType scope, Type serviceType, float nodeProgress, string? message, double completedWeight, double nodeWeight, double totalWeight)
                => Events.Add($"progress:{serviceType.Name}:{nodeProgress:F2}");

            public void OnServiceCompleted(GameContextType scope, Type serviceType, double completedWeight, double totalWeight)
                => Events.Add($"done:{serviceType.Name}:{completedWeight:F1}/{totalWeight:F1}");

            public void OnGateOpened(GameContextType scope, Type serviceType, string prompt)
                => Events.Add($"gate-open:{prompt}");

            public void OnGateClosed(GameContextType scope, Type serviceType)
                => Events.Add($"gate-closed:{serviceType.Name}");
        }

        [LoadWeight(5)]
        private sealed class HeavyConfigSource : ContentSource<WeightSnapshot>, IGlobalInitializableService
        {
            public override string SourceName => "heavy-config";
            protected override Task<WeightSnapshot> LoadAsync(CancellationToken ct) => Task.FromResult(new WeightSnapshot());
        }

        private sealed class LightService : ISessionInitializableService
        {
            public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        }

        [Test]
        public async Task WeightedProgress_HeavyNodeConsumesItsShare()
        {
            var recorder = new RecordingWeightedNotifier();
            await using var pipeline = await TestPipeline.Create(b =>
                {
                    b.Global().Content<HeavyConfigSource, WeightSnapshot>();
                    b.Session().Register<LightService>(DiLifetime.Singleton);
                })
                .ObserveProgress(recorder)
                .StartAsync();

            var done = recorder.Events.Where(e => e.StartsWith("done:")).ToList();
            Assert.AreEqual(2, done.Count, string.Join("|", recorder.Events));

            // heavy = weight 5 of 6 total: completes at 5/6; light completes at 6/6.
            StringAssert.Contains("5.0/6.0", done.First(e => e.Contains(nameof(HeavyConfigSource))), string.Join("|", done));
            StringAssert.Contains("6.0/6.0", done.First(e => e.Contains(nameof(LightService))), string.Join("|", done));
        }

        // ---------- user gate ----------

        [Test]
        public async Task UserGate_BlocksNodeUntilCompleted_AndReportsTransitions()
        {
            var completer = new UserGateCompleter();
            var recorder = new RecordingWeightedNotifier();
            GatedConsentService.OnStarted = () => { };

            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            GatedConsentService.StartedSignal = started;

            var pipeline = TestPipeline.Create(b =>
                {
                    b.Global().RegisterInstance(completer);
                    b.Global().Register<GatedConsentService>(DiLifetime.Singleton);
                })
                .ObserveProgress(recorder);

            var startTask = pipeline.StartAsync();

            // The gated node waits inside its InitializeAsync until the player answers;
            // the startup timeout (60 s) bounds the wait in case of a bug.
            await WaitUntilAsync(() => started.Task.IsCompleted);
            Assert.IsFalse(startTask.IsCompleted, "Boot must be blocked while the gate is open.");

            StringAssert.Contains("gate-open:gdpr-consent", string.Join("|", recorder.Events));

            completer.Complete("gdpr-consent");
            await startTask;

            StringAssert.Contains($"gate-closed:{nameof(GatedConsentService)}", string.Join("|", recorder.Events));
            Assert.IsTrue(GatedConsentService.Finished);
        }

        private static async Task WaitUntilAsync(Func<bool> predicate)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!predicate())
            {
                cts.Token.ThrowIfCancellationRequested();
                await Task.Delay(10, cts.Token).ConfigureAwait(false);
            }
        }

        private sealed class GatedConsentService : IGlobalInitializableService, IUserInteractionGatedInitializableService
        {
            public static bool Finished { get; private set; }
            public static Action? OnStarted { get; set; }
            public static TaskCompletionSource<bool>? StartedSignal;

            private readonly IUserGate _gate;
            public GatedConsentService(UserGateCompleter completer) => _gate = completer.Create("gdpr-consent");

            public async Task InitializeAsync(CancellationToken ct)
            {
                Finished = false;
                this.NotifyGateOpened(GameContextType.Global, _gate.Prompt);
                OnStarted?.Invoke();
                StartedSignal?.TrySetResult(true);
                try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
                finally
                {
                    this.NotifyGateClosed(GameContextType.Global);
                    Finished = true;
                }
            }
        }
    }
}
