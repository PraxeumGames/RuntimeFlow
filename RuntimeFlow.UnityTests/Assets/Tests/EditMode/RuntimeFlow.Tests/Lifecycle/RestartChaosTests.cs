using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Tests.Lifecycle.Chaos;

namespace RuntimeFlow.Tests.Lifecycle
{
    /// <summary>
    /// Seeded, replayable chaos: random global/session graphs (constructor and [DependsOn] edges,
    /// optional, user-gated, timed and phased services, every disposal shape) whose services follow a
    /// random script per generation — complete, throw, bail out, ignore their token, park, halt, request
    /// restarts with every kind of token — while the "game" restarts from the UI, cancels, quits,
    /// disposes and opens child scopes at random moments. <see cref="ChaosRunner"/> checks the lifecycle
    /// invariants after every case; a failure names the seed, so <see cref="Replay"/> reproduces it.
    /// </summary>
    [TestFixture]
    public sealed class RestartChaosTests
    {
        private const int BatchSize = 25;

        /// <summary>Batches run by every regular EditMode run: seeds 0..74. Fast enough to keep in the suite.</summary>
        public static IEnumerable<int> FastBatches => Enumerable.Range(0, 3);

        /// <summary>
        /// Batches of the full sweep, <c>RUNTIMEFLOW_CHAOS_BATCHES=first,count</c> (batch n runs seeds
        /// n*25 .. n*25+24; <c>0,30</c> sweeps seeds 0..749). Only an unset variable yields the no-sweep
        /// sentinel (-1), so a regular run stays fast; the fast batches above always run.
        /// </summary>
        public static IEnumerable<int> SweepBatches => ParseSweepBatches(
            System.Environment.GetEnvironmentVariable("RUNTIMEFLOW_CHAOS_BATCHES"));

        internal static IEnumerable<int> ParseSweepBatches(string? configuredRange)
        {
            if (configuredRange == null) return new[] { -1 };

            var parts = configuredRange.Split(',');
            if (parts.Length != 2
                || !TryParseUnsignedInt(parts[0], out var first)
                || !TryParseUnsignedInt(parts[1], out var count)
                || count == 0)
            {
                throw InvalidSweepRange(configuredRange);
            }

            var lastBatch = (long)first + count - 1;
            if (lastBatch > MaxBatchForInt32Seeds)
                throw InvalidSweepRange(configuredRange);

            return Enumerable.Range(first, count);
        }

        private const int MaxBatchForInt32Seeds = (int.MaxValue - (BatchSize - 1)) / BatchSize;

        private static bool TryParseUnsignedInt(string value, out int parsed)
        {
            if (value.Length == 0 || value.Any(character => character < '0' || character > '9'))
            {
                parsed = 0;
                return false;
            }

            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed);
        }

        private static FormatException InvalidSweepRange(string configuredRange) => new FormatException(
            $"Invalid RUNTIMEFLOW_CHAOS_BATCHES value '{configuredRange}'. Use 'first,count' with " +
            $"0 <= first, count > 0, and last batch <= {MaxBatchForInt32Seeds}; each batch has {BatchSize} seeds.");

        [TestCaseSource(nameof(FastBatches))]
        [Timeout(300000)]
        public Task RandomScenariosHoldEveryInvariant(int batch) => RunBatch(batch);

        /// <summary>The full sweep; see <see cref="SweepBatches"/>.</summary>
        [Category("Chaos")]
        [TestCaseSource(nameof(SweepBatches))]
        [Timeout(600000)]
        public async Task ChaosSweep(int batch)
        {
            // -1: no sweep requested. It passes trivially; an ignored or empty case would trip Unity's runner.
            if (batch < 0) return;
            await RunBatch(batch);
        }

        private static async Task RunBatch(int batch)
        {
            var failures = new List<ChaosReport>();
            var firstSeed = batch * BatchSize;
            for (var offset = 0; offset < BatchSize; offset++)
            {
                var seed = firstSeed + offset;
                var report = await ChaosRunner.Run(ChaosScenario.Generate(seed));
                if (report.Failed) failures.Add(report);
            }

            Assert.That(failures, Is.Empty, Summary(failures));
        }

        /// <summary>Replays single seeds; add a failing seed here to debug it in isolation.</summary>
        [TestCase(0)]
        [Timeout(60000)]
        public async Task Replay(int seed)
        {
            var report = await ChaosRunner.Run(ChaosScenario.Generate(seed));
            Assert.That(report.Failed, Is.False, report.Render(1000, 400));
        }

        public static IEnumerable<TestCaseData> SfsChains()
        {
            foreach (var then in new[] { ChaosAct.Park, ChaosAct.Complete, ChaosAct.ThrowCancelled })
            foreach (var token in new[] { ChaosToken.None, ChaosToken.Own })
            foreach (var ui in new[] { false, true })
                yield return new TestCaseData(then, token, ui);
        }

        /// <summary>
        /// The sfs-client chain: generation 0's AddressablesUpdater and generation 1's AbTestsPrecheck each
        /// request a restart; generation 2 completes. Variants cover what the requester does afterwards,
        /// the token it passes, and a UI restart that lands in the middle of the chain.
        /// </summary>
        [TestCaseSource(nameof(SfsChains))]
        [Timeout(60000)]
        public async Task TheSfsClientRestartChainReachesItsFinalGeneration(ChaosAct then, ChaosToken token, bool uiMidChain)
        {
            var scenario = Sfs(then, token, uiMidChain);
            var report = await ChaosRunner.Run(scenario);
            var world = report.World;

            Assert.That(report.Failed, Is.False, report.Render(1000, 400));
            var start = world.Awaiters.First(a => a.Label == "start");
            Assert.That(start.Task.IsFaulted || start.Task.IsCanceled, Is.False, report.Render(1000, 400));
            var result = ((Task<StartupResult>)start.Task).Result;
            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed), report.Render(1000, 400));

            var gameplay = world.Instances.Where(i => i.Slot.Name == "Gameplay" && i.InitCalls > 0).ToList();
            Assert.That(gameplay.Count, Is.EqualTo(1), "Gameplay starts only in the final generation\n" + report.Render(1000, 400));
            var final = gameplay[0].Generation;
            Assert.That(final, uiMidChain ? Is.InRange(2, 3) : Is.EqualTo(2), report.Render(1000, 400));
            Assert.That(world.BuildGenerations.Last(), Is.EqualTo(final), report.Render(1000, 400));
        }

        private static ChaosScenario Sfs(ChaosAct then, ChaosToken token, bool uiMidChain)
        {
            var scenario = new ChaosScenario { Label = $"sfs {then}/{token}{(uiMidChain ? "/ui" : "")}" };
            scenario.Global.Add(new ChaosSlot { Scope = ChaosScope.Global, Index = 0, Name = "Bootstrap" });

            var addressables = new ChaosSlot { Scope = ChaosScope.Session, Index = 0, Name = "AddressablesUpdater", Disposal = ChaosDisposal.Async };
            addressables.ByGeneration[0] = new ChaosBehaviour { Yields = 2, Restart = true, Token = token, Then = then };

            var abTests = new ChaosSlot { Scope = ChaosScope.Session, Index = 1, Name = "AbTestsPrecheck", Disposal = ChaosDisposal.Both };
            abTests.Deps.Add(0);
            abTests.DepViaAttribute.Add(false);
            abTests.ByGeneration[1] = new ChaosBehaviour { Yields = 2, Restart = true, Token = token, Then = then };

            var gameplay = new ChaosSlot { Scope = ChaosScope.Session, Index = 2, Name = "Gameplay", Disposal = ChaosDisposal.Sync };
            gameplay.Deps.Add(1);
            gameplay.DepViaAttribute.Add(true);
            gameplay.ParentDeps.Add(0);

            var hud = new ChaosSlot { Scope = ChaosScope.Session, Index = 3, Name = "Hud", Default = new ChaosBehaviour { DelayMs = 30 } };

            scenario.Session.AddRange(new[] { addressables, abTests, gameplay, hud });

            if (uiMidChain)
            {
                scenario.Actions.Add(new ChaosDriverAction
                {
                    Kind = ChaosDriverKind.UiRestart,
                    WaitFor = w => w.Instances.Any(i => i.Slot.Name == "AbTestsPrecheck" && i.Generation == 1)
                });
            }
            return scenario;
        }

        private static string Summary(List<ChaosReport> failures)
        {
            if (failures.Count == 0) return string.Empty;
            var text = new StringBuilder();
            text.Append("failing seeds: ").AppendLine(string.Join(", ", failures.Select(f => f.Scenario.Seed.ToString(CultureInfo.InvariantCulture))));
            foreach (var failure in failures)
            {
                text.Append("seed ").Append(failure.Scenario.Seed).Append(": ")
                    .AppendLine(string.Join(" | ", failure.Violations.Distinct().Take(4)));
            }
            text.AppendLine();
            text.Append(failures[0].Render());
            return text.ToString();
        }
    }
}
