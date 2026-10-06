using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RuntimeFlow.Testing;
using VContainer;

namespace RuntimeFlow.Tests.Lifecycle.Chaos
{
    /// <summary>Outcome of one chaos case: the violations found plus everything needed to understand them.</summary>
    public sealed class ChaosReport
    {
        public ChaosScenario Scenario = null!;
        public ChaosWorld World = null!;
        public CapturingLogger Log = null!;
        public TimeSpan Elapsed;
        public List<string> Violations => World.Violations;
        public bool Failed => Violations.Count > 0;

        public string Summary()
        {
            var start = World.Awaiters.FirstOrDefault(a => a.Label == "start");
            return $"{(Scenario.Label.Length > 0 ? Scenario.Label : "seed " + Scenario.Seed.ToString(CultureInfo.InvariantCulture))}: " +
                   $"{Elapsed.TotalMilliseconds:0}ms, start {start?.Outcome() ?? "?"}, builds {World.BuildGenerations.Count}, " +
                   $"max gen {World.MaxSessionGenerationStarted}, instances {World.Instances.Count}, started {World.Instances.Count(i => i.InitCalls > 0)}, " +
                   $"awaiters {World.Awaiters.Count}, violations {Violations.Count}";
        }

        public string Render(int events = 300, int logLines = 150)
        {
            var text = new StringBuilder();
            text.AppendLine($"=== chaos {(Scenario.Label.Length > 0 ? Scenario.Label : "seed " + Scenario.Seed.ToString(CultureInfo.InvariantCulture))}: " +
                            $"{Violations.Count.ToString(CultureInfo.InvariantCulture)} violation(s) in {Elapsed.TotalMilliseconds:0}ms");
            foreach (var violation in Violations.Distinct()) text.Append(" * ").AppendLine(violation);
            text.Append(Scenario.Describe());
            text.AppendLine("--- events");
            text.Append(World.Dump(events));
            text.AppendLine("--- RuntimeFlow log");
            var lines = Log.Lines;
            var from = Math.Max(0, lines.Count - logLines);
            if (from > 0) text.AppendLine($"... {from} earlier lines omitted");
            for (var i = from; i < lines.Count; i++) text.AppendLine(lines[i]);
            return text.ToString();
        }
    }

    /// <summary>
    /// Drives a <see cref="RuntimeFlowHost"/> through one <see cref="ChaosScenario"/> and checks the
    /// lifecycle invariants: every task settles, nothing starts after disposal, every constructed
    /// service is disposed exactly once, generations are monotonic, dependencies finish before their
    /// dependents start, no service initializes twice, no unobserved exception or unexpected error log,
    /// and the awaiters of a chain agree with each other and with the host state.
    /// </summary>
    public static class ChaosRunner
    {
        private static readonly Regex DisposingService = new Regex(@"^\[RuntimeFlow\] (\S+): disposing (\S+) threw ");
        private const int SettleBoundMs = 5000;
        private const int QuiescenceBoundMs = 4000;

        public static async Task<ChaosReport> Run(ChaosScenario scenario)
        {
            var clock = Stopwatch.StartNew();
            scenario.DefineTypes();

            FlushFinalizers();
            var unobserved = new List<Exception>();
            EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, args) =>
            {
                lock (unobserved) unobserved.Add(args.Exception);
            };
            TaskScheduler.UnobservedTaskException += onUnobserved;

            var log = new CapturingLogger();
            var options = new RuntimeFlowOptions
            {
                Logger = log,
                TimeoutMultiplier = 1,
                StallWarningAfter = TimeSpan.Zero,
                CancellationGrace = TimeSpan.FromMilliseconds(scenario.CancellationGraceMs),
                Phases = new[] { "early", "late" },
                MaxRestartsPerWindow = scenario.MaxRestarts,
                RestartWindow = TimeSpan.FromSeconds(60)
            };

            var world = new ChaosWorld(scenario);
            var report = new ChaosReport { Scenario = scenario, World = world, Log = log };
            var host = new RuntimeFlowHost(
                builder =>
                {
                    world.OnGlobalBuild();
                    builder.RegisterInstance(world);
                    foreach (var slot in scenario.Global) Register(builder, slot);
                },
                builder =>
                {
                    world.OnSessionBuild();
                    foreach (var slot in scenario.Session) Register(builder, slot);
                },
                options);
            world.Host = host;

            var startCts = scenario.StartWithToken ? new CancellationTokenSource() : null;
            try
            {
                world.Track("start", host.StartAsync(startCts?.Token ?? default), chain: true);

                var childIndex = 0;
                foreach (var action in scenario.Actions)
                {
                    if (action.WaitFor != null) await Until(() => action.WaitFor(world), QuiescenceBoundMs);
                    for (var i = 0; i < action.WaitYields; i++) await Task.Yield();
                    if (action.WaitMs > 0) await Task.Delay(action.WaitMs);
                    Perform(world, host, action, startCts, ref childIndex);
                }

                var settled = await Until(() => ChainSettled(world), QuiescenceBoundMs);
                if (!settled && (!world.MayHang || world.DisposeCalledSeq >= 0))
                {
                    world.Violation($"the chain did not settle within {QuiescenceBoundMs}ms before the final dispose: " +
                                    $"host state {host.State}, generation {host.Generation}; pending: " +
                                    string.Join(", ", world.Awaiters.Where(a => a.Chain && !a.Task.IsCompleted).Select(a => a.Label)));
                }
                if (settled && world.DisposeCalledSeq < 0) CheckQuiescentState(world, host);

                world.Log("final dispose");
                Dispose(world, host, "final dispose");

                if (!await Until(() => world.Awaiters.All(a => a.Task.IsCompleted), SettleBoundMs))
                {
                    world.Violation($"tasks still pending {SettleBoundMs}ms after the final DisposeAsync: " +
                                    string.Join(", ", world.Awaiters.Where(a => !a.Task.IsCompleted).Select(a => a.Label)));
                }

                // Services the run abandoned may still be finishing their (bounded) hang: give them the time
                // to prove that nothing new starts once they return.
                await Until(() => world.Instances.All(i => i.Outcome != ChaosOutcome.Running), 400);
                CheckAfterDisposal(world, host, log);
                CheckChainConsistency(world);
            }
            catch (Exception exception)
            {
                world.Violation($"the harness threw {exception}");
            }
            finally
            {
                startCts?.Dispose();
                if (world.DisposeCalledSeq < 0)
                {
                    try { await host.DisposeAsync(); }
                    catch (Exception) { }
                }

                FlushFinalizers();
                TaskScheduler.UnobservedTaskException -= onUnobserved;
                lock (unobserved)
                {
                    foreach (var exception in unobserved)
                    {
                        var text = exception.ToString();
                        if (text.Contains("RuntimeFlow") || text.Contains("chaos"))
                            world.Violation($"unobserved task exception: {Flatten(exception)}");
                    }
                }
                report.Elapsed = clock.Elapsed;
                UnityEngine.Debug.Log($"[chaos] {report.Summary()}");
            }

            return report;
        }

        private static void Register(IContainerBuilder builder, ChaosSlot slot)
            => builder.Register(slot.Type, Lifetime.Singleton).As(typeof(IAsyncInitializable)).AsSelf();

        private static void Perform(ChaosWorld world, RuntimeFlowHost host, ChaosDriverAction action,
            CancellationTokenSource? startCts, ref int childIndex)
        {
            world.Log($"driver: {action.Kind}");
            switch (action.Kind)
            {
                case ChaosDriverKind.UiRestart:
                    world.FireRestart(null, ChaosToken.None, default, "ui", out _);
                    break;
                case ChaosDriverKind.UiRestartAlreadyCancelled:
                    world.FireRestart(null, ChaosToken.AlreadyCancelled, default, "ui-cancelled", out _);
                    break;
                case ChaosDriverKind.UiRestartCancelledLater:
                {
                    var cts = new CancellationTokenSource();
                    world.FireRestart(null, ChaosToken.Own, cts.Token, "ui-cancel-later", out _);
                    _ = CancelLater(world, cts, action.CancelAfterMs);
                    break;
                }
                case ChaosDriverKind.CancelStartToken:
                    if (startCts != null)
                    {
                        world.Log("driver: cancelling the StartAsync token");
                        startCts.Cancel();
                    }
                    break;
                case ChaosDriverKind.Dispose:
                    Dispose(world, host, "dispose (driver)");
                    break;
                case ChaosDriverKind.Quit:
                    host.OnQuitting();
                    break;
                case ChaosDriverKind.ChildUnderSession:
                case ChaosDriverKind.ChildUnderGlobal:
                    InitializeChild(world, host, action.Kind == ChaosDriverKind.ChildUnderSession, "child" + (childIndex++).ToString(CultureInfo.InvariantCulture));
                    break;
                case ChaosDriverKind.StartAgain:
                    // Only while a startup or restart is in flight: after a failed global phase a second
                    // StartAsync legitimately retries from scratch, which is not "the same chain".
                    if (host.State != RunState.Running || world.DisposeCalledSeq >= 0) break;
                    world.Track("start again", host.StartAsync(), chain: true);
                    break;
            }
        }

        private static async Task CancelLater(ChaosWorld world, CancellationTokenSource cts, int ms)
        {
            try
            {
                await Task.Delay(ms);
                world.Log("driver: cancelling the UI restart token");
                cts.Cancel();
            }
            catch (Exception exception)
            {
                world.Log($"driver: cancelling the UI restart token threw {exception.GetType().Name}");
            }
        }

        private static void Dispose(ChaosWorld world, RuntimeFlowHost host, string label)
        {
            world.MarkDisposeCalled();
            Task task;
            try
            {
                task = host.DisposeAsync().AsTask();
            }
            catch (Exception exception)
            {
                world.Violation($"DisposeAsync threw synchronously: {exception.GetType().Name}: {exception.Message}");
                return;
            }
            task.ContinueWith(_ =>
            {
                if (world.DisposeSettledSeq < 0) world.DisposeSettledSeq = world.Seq();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            world.Track(label, task, chain: false);
        }

        private static void InitializeChild(ChaosWorld world, RuntimeFlowHost host, bool underSession, string name)
        {
            IScopedObjectResolver scope;
            try
            {
                var parent = underSession ? (IObjectResolver)host.Session : host.Global;
                scope = parent.CreateScope(builder =>
                {
                    foreach (var slot in world.Scenario.Child) Register(builder, slot);
                });
            }
            catch (Exception exception)
            {
                world.Log($"driver: no parent for {name}: {exception.GetType().Name}");
                return;
            }

            world.BeginChild(underSession, name);
            var build = world.ChildBuilds - 1;
            Task<ScopeRun> task;
            try
            {
                task = host.InitializeScopeAsync(scope, name);
            }
            catch (Exception exception)
            {
                world.Log($"driver: InitializeScopeAsync({name}) threw synchronously: {exception.GetType().Name}");
                scope.Dispose();
                return;
            }
            world.Track($"child {name} ({(underSession ? "session" : "global")})", task, chain: false);

            // A refused child never reached ScopeRun.Create, so nobody else owns its scope.
            task.ContinueWith(t =>
            {
                if (!t.IsFaulted) return;
                if (world.Instances.Any(i => i.Slot.Scope == ChaosScope.Child && i.Build == build)) return;
                world.Log($"driver: {name} refused ({t.Exception!.InnerException!.GetType().Name}); disposing its scope");
                scope.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static bool ChainSettled(ChaosWorld world)
            => world.Awaiters.Where(a => a.Chain && !a.Refused).All(a => a.Task.IsCompleted);

        private static async Task<bool> Until(Func<bool> condition, int boundMs)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.ElapsedMilliseconds > boundMs) return false;
                await Task.Delay(5);
            }
            return true;
        }

        // ------------------------------------------------------------------ invariants

        /// <summary>Invariant 7 and friends, while the host is still alive and its chain has settled.</summary>
        private static void CheckQuiescentState(ChaosWorld world, RuntimeFlowHost host)
        {
            var state = host.State;
            var status = host.GetStatus();
            world.Log($"quiescent: host {state}, status {status.State}, generation {host.Generation}, restarts {host.RestartCount}");

            if (state == RunState.Running) world.Violation("the chain settled but the host still reports Running");
            if (status.State != state) world.Violation($"GetStatus().State is {status.State} but host.State is {state}");
            if (host.RestartCount != host.Generation)
                world.Violation($"RestartCount {host.RestartCount} differs from Generation {host.Generation}");
            if (world.BuildGenerations.Count > 0 && world.BuildGenerations[world.BuildGenerations.Count - 1] != host.Generation)
                world.Violation($"host.Generation {host.Generation} differs from the latest session build's {world.BuildGenerations[world.BuildGenerations.Count - 1]}");

            foreach (var run in new[] { host.GlobalRun, host.SessionRun })
            {
                if (run == null || run.State == RunState.Running || run.State == RunState.NotStarted) continue;
                foreach (var service in run.GetStatus().Services)
                {
                    if (service.State == ServiceState.Running || service.State == ServiceState.Pending)
                        world.Violation($"{run.Name} run is {run.State} but {service.Name} is still {service.State}");
                }
            }

            var last = world.Awaiters.LastOrDefault(a => a.Chain && !a.Refused);
            if (last == null || host.IsQuitting) return;
            var task = last.Task;
            if (task.IsCanceled)
            {
                if (state != RunState.Cancelled && state != RunState.NotStarted)
                    world.Violation($"the chain ended cancelled but the host is {state}");
            }
            else if (task.IsFaulted)
            {
                var error = task.Exception!.InnerException!;
                if (error is RuntimeFlowException flow && flow.Scope == "global")
                {
                    if (state != RunState.NotStarted) world.Violation($"the global phase failed but the host is {state}");
                }
                else if (error is ObjectDisposedException) { }
                else
                {
                    if (state != RunState.Failed) world.Violation($"the chain failed with {error.GetType().Name} but the host is {state}");
                    else if (!ReferenceEquals(status.Error, error))
                        world.Violation($"GetStatus().Error ({status.Error?.GetType().Name ?? "null"}) is not the chain's {error.GetType().Name}");
                }
            }
            else if (task is Task<StartupResult> startup)
            {
                var result = startup.Result;
                var expected = result.Outcome == StartupOutcome.Completed ? RunState.Completed : RunState.Halted;
                if (state != expected) world.Violation($"the chain ended {result.Outcome} but the host is {state}");
                // A halt in the global scope ends the startup with the global result: no session is built.
                var globalHalt = result.Outcome == StartupOutcome.Halted && result.Scope == "global";
                if (globalHalt)
                {
                    if (host.SessionRun != null || world.BuildGenerations.Count > 0)
                        world.Violation("the global scope halted but a session scope was built");
                    if (status.HaltReason != result.HaltReason)
                        world.Violation($"GetStatus().HaltReason '{status.HaltReason}' is not the global halt's '{result.HaltReason}'");
                }
                else if (result.Scope != "session") world.Violation($"the chain's result describes scope '{result.Scope}', not the session");
                if (state == RunState.Completed)
                {
                    foreach (var service in status.Services)
                    {
                        if (service.State != ServiceState.Completed && service.State != ServiceState.Degraded)
                            world.Violation($"the host completed but {service.Scope}/{service.Name} is {service.State}");
                    }
                }
            }
        }

        /// <summary>Invariants 2, 5 and 6 once the host is disposed and everything settled.</summary>
        private static void CheckAfterDisposal(ChaosWorld world, RuntimeFlowHost host, CapturingLogger log)
        {
            if (host.State != RunState.Disposed) world.Violation($"after DisposeAsync the host reports {host.State}");

            foreach (var instance in world.Instances)
            {
                var disposal = instance.Slot.Disposal;
                if ((disposal == ChaosDisposal.Async || disposal == ChaosDisposal.Both) && instance.DisposeAsyncCalls != 1)
                    world.Violation($"{instance.Label} (IAsyncDisposable) had DisposeAsync called {instance.DisposeAsyncCalls} times");
                if ((disposal == ChaosDisposal.Sync || disposal == ChaosDisposal.Both) && instance.DisposeCalls != 1)
                    world.Violation($"{instance.Label} (IDisposable) had Dispose called {instance.DisposeCalls} times");
                if (instance.InitCalls > 1) world.Violation($"{instance.Label} was initialized {instance.InitCalls} times");
            }

            foreach (var entry in log.Entries)
            {
                if (entry.Level < LogLevel.Error) continue;
                if (ExpectedError(world, entry.Message)) continue;
                world.Violation($"unexpected error log: {entry.Message}");
            }
        }

        private static bool ExpectedError(ChaosWorld world, string message)
        {
            if (message.Contains(": Initialization of scope '")) return true;
            if (message.Contains(" services still running ") && message.Contains("after cancellation")) return true;
            if (message.Contains("disposing the scope threw InvalidOperationException"))
                return world.Scenario.AllSlots.Any(s => s.SyncDisposeThrows && s.Disposal != ChaosDisposal.None && s.Disposal != ChaosDisposal.Async);
            var match = DisposingService.Match(message);
            if (match.Success)
            {
                var name = match.Groups[2].Value;
                return world.Scenario.AllSlots.Any(s => s.Name == name && s.AsyncDisposeThrows
                                                        && (s.Disposal == ChaosDisposal.Async || s.Disposal == ChaosDisposal.Both));
            }
            return false;
        }

        /// <summary>
        /// An awaiter that was still pending when a later request joined or replaced its chain follows the
        /// chain to its last run, so both must observe the very same outcome.
        /// </summary>
        private static void CheckChainConsistency(ChaosWorld world)
        {
            foreach (var later in world.Awaiters.Where(a => a.Chain && !a.Refused))
            {
                if (!later.Task.IsCompleted) continue;
                foreach (var earlier in later.PendingAtCreation)
                {
                    if (!earlier.Task.IsCompleted || SameOutcome(earlier.Task, later.Task)) continue;
                    world.Violation($"awaiter '{earlier.Label}' ended '{earlier.Outcome()}' but '{later.Label}', " +
                                    $"created while it was pending, ended '{later.Outcome()}'");
                }
            }
        }

        private static bool SameOutcome(Task a, Task b)
        {
            if (a.IsCanceled || b.IsCanceled) return a.IsCanceled && b.IsCanceled;
            if (a.IsFaulted || b.IsFaulted)
                return a.IsFaulted && b.IsFaulted && ReferenceEquals(a.Exception!.InnerException, b.Exception!.InnerException);
            return a is Task<StartupResult> x && b is Task<StartupResult> y && ReferenceEquals(x.Result, y.Result);
        }

        private static void FlushFinalizers()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static string Flatten(Exception exception)
        {
            var inner = exception is AggregateException aggregate && aggregate.InnerExceptions.Count == 1
                ? aggregate.InnerExceptions[0]
                : exception;
            var text = inner.ToString();
            return text.Length > 1500 ? text.Substring(0, 1500) + "…" : text;
        }
    }
}
