using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Tests.Lifecycle.Chaos
{
    public enum ChaosOutcome { Constructed, Running, Completed, Threw }

    /// <summary>Everything the harness knows about one constructed service instance.</summary>
    public sealed class ChaosInstance
    {
        public int Id;
        public ChaosSlot Slot = null!;
        public int Build;
        public int BuildGeneration;
        public int ParentBuild = -1;
        public bool ChildUnderSession;
        public string ScopeName = "";
        public int InitCalls;
        public int Generation = -1;
        public ChaosOutcome Outcome = ChaosOutcome.Constructed;
        public Exception? Error;
        public int DisposeAsyncCalls;
        public int DisposeCalls;

        public string Label => $"{Slot.Name}#{Id}@{ScopeName}{Build}";
    }

    /// <summary>A task returned by the host that the harness follows.</summary>
    public sealed class ChaosAwaiter
    {
        public string Label = "";
        public Task Task = null!;
        public bool Chain;
        public bool Refused;
        public int CreatedSeq;
        public int CompletedSeq = -1;
        public List<ChaosAwaiter> PendingAtCreation = new List<ChaosAwaiter>();

        public string Outcome()
        {
            if (!Task.IsCompleted) return "pending";
            if (Task.IsCanceled) return "cancelled";
            if (Task.IsFaulted)
            {
                var error = Task.Exception!.InnerException!;
                return $"{error.GetType().Name}: {error.Message}";
            }
            return Task is Task<StartupResult> startup ? "result " + startup.Result : "done";
        }
    }

    /// <summary>
    /// The shared state of a chaos case: the plan, the event log, the instance records and the invariant
    /// violations found while it ran. Registered in the global scope, so every generation sees one world.
    /// </summary>
    public sealed class ChaosWorld
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Dictionary<Type, ChaosSlot> _slots = new Dictionary<Type, ChaosSlot>();
        private int _seq;

        public ChaosWorld(ChaosScenario scenario)
        {
            Scenario = scenario;
            foreach (var slot in scenario.AllSlots) _slots[slot.Type] = slot;
            AlreadyCancelled = new CancellationToken(true);
        }

        public ChaosScenario Scenario { get; }
        public RuntimeFlowHost Host { get; set; } = null!;
        public CancellationToken AlreadyCancelled { get; }

        public List<string> Events { get; } = new List<string>();
        public List<string> Violations { get; } = new List<string>();
        public List<ChaosInstance> Instances { get; } = new List<ChaosInstance>();
        public List<ChaosAwaiter> Awaiters { get; } = new List<ChaosAwaiter>();

        /// <summary>Index of the latest session installer call; -1 before the first.</summary>
        public int LatestSessionBuild { get; private set; } = -1;
        public List<int> BuildGenerations { get; } = new List<int>();

        /// <summary>Session builds up to this index are doomed by an accepted restart; none of their services may start.</summary>
        public int DoomedUpTo { get; private set; } = -1;

        public int ChildBuilds { get; private set; }
        public int MaxSessionGenerationStarted { get; private set; } = -1;
        public int DisposeCalledSeq { get; private set; } = -1;
        public int DisposeSettledSeq { get; set; } = -1;

        /// <summary>True once something legitimately waits forever before dispose (a gated park, a refused restart then park).</summary>
        public bool MayHang { get; private set; }
        public string? MayHangReason { get; private set; }

        // set while constructing a scope
        private int _childBuild = -1;
        private int _childParentBuild = -1;
        private bool _childUnderSession;
        private string _childName = "";

        public int Seq() => ++_seq;

        public void Log(string text)
            => Events.Add($"{Seq().ToString(CultureInfo.InvariantCulture),4} {_clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),5}ms {text}");

        public void Violation(string text)
        {
            Violations.Add(text);
            Log("VIOLATION " + text);
        }

        public void Hang(string reason)
        {
            if (!MayHang) MayHangReason = reason;
            MayHang = true;
            Log("may hang: " + reason);
        }

        // ------------------------------------------------------------------ scope construction

        public void OnGlobalBuild()
        {
            Log("global installer");
        }

        public void OnSessionBuild()
        {
            if (DisposeCalledSeq >= 0) Violation($"a session scope is being built after DisposeAsync was called (build #{LatestSessionBuild + 1})");
            if (Host.IsQuitting) Violation($"a session scope is being built after OnQuitting (build #{LatestSessionBuild + 1})");
            LatestSessionBuild++;
            BuildGenerations.Add(Host.Generation);
            Log($"session installer: build #{LatestSessionBuild} as generation {Host.Generation}");
            if (BuildGenerations.Count > 1 && BuildGenerations[BuildGenerations.Count - 1] <= BuildGenerations[BuildGenerations.Count - 2])
            {
                Violation($"session build #{LatestSessionBuild} has generation {Host.Generation}, " +
                          $"not above the previous build's {BuildGenerations[BuildGenerations.Count - 2]}");
            }
            if (LatestSessionBuild == Scenario.SessionBuildThrowsAt)
            {
                Log("session installer throws (planned)");
                throw new InvalidOperationException($"chaos: session installer #{LatestSessionBuild} exploded");
            }
        }

        public void BeginChild(bool underSession, string name)
        {
            _childBuild = ChildBuilds++;
            _childParentBuild = underSession ? LatestSessionBuild : -1;
            _childUnderSession = underSession;
            _childName = name;
        }

        public ChaosInstance Constructed(ChaosService service)
        {
            var slot = _slots[service.GetType()];
            var instance = new ChaosInstance { Id = Instances.Count, Slot = slot };
            switch (slot.Scope)
            {
                case ChaosScope.Global:
                    instance.Build = 0;
                    instance.ScopeName = "global";
                    break;
                case ChaosScope.Session:
                    instance.Build = LatestSessionBuild;
                    instance.BuildGeneration = BuildGenerations[LatestSessionBuild];
                    instance.ScopeName = "session";
                    break;
                default:
                    instance.Build = _childBuild;
                    instance.ParentBuild = _childParentBuild;
                    instance.ChildUnderSession = _childUnderSession;
                    instance.ScopeName = _childName;
                    break;
            }
            Instances.Add(instance);
            Log($"constructed {instance.Label}");

            if (slot.Scope == ChaosScope.Session && slot.CtorRestartGeneration == instance.BuildGeneration)
                FireRestart(instance, ChaosToken.None, default, "ctor", out _);
            return instance;
        }

        // ------------------------------------------------------------------ awaiters

        public ChaosAwaiter Track(string label, Task task, bool chain, bool refused = false)
        {
            var awaiter = new ChaosAwaiter { Label = label, Task = task, Chain = chain, Refused = refused, CreatedSeq = Seq() };
            if (chain && !refused)
                awaiter.PendingAtCreation.AddRange(Awaiters.Where(a => a.Chain && !a.Refused && !a.Task.IsCompleted));
            Awaiters.Add(awaiter);
            Log($"awaiter {label}{(refused ? " (refused: " + awaiter.Outcome() + ")" : "")}");
            task.ContinueWith(t =>
            {
                _ = t.Exception;
                awaiter.CompletedSeq = Seq();
                Log($"awaiter {label} settled: {awaiter.Outcome()}");
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return awaiter;
        }

        /// <summary>Fires a restart the way a service or the UI does; returns false when the host refused it.</summary>
        public bool FireRestart(ChaosInstance? by, ChaosToken kind, CancellationToken own, string label, out ChaosAwaiter? awaiter)
        {
            awaiter = null;
            var token = kind switch
            {
                ChaosToken.Own => own,
                ChaosToken.AlreadyCancelled => AlreadyCancelled,
                _ => CancellationToken.None
            };
            var reason = $"chaos-{label}";
            Task<StartupResult> task;
            try
            {
                task = Host.RestartAsync(reason, token);
            }
            catch (Exception exception)
            {
                Log($"restart {reason} ({kind}) threw synchronously: {exception.GetType().Name}");
                return false;
            }

            var refused = task.IsCompleted;
            if (!refused)
            {
                DoomedUpTo = Math.Max(DoomedUpTo, LatestSessionBuild);
                Log($"restart {reason} ({kind}) accepted: session builds up to #{LatestSessionBuild} are doomed");
            }
            awaiter = Track($"restart {reason}", task, chain: true, refused: refused);
            return !refused;
        }

        // ------------------------------------------------------------------ services

        public async Task RunService(ChaosInstance instance, InitContext context, CancellationToken token)
        {
            instance.InitCalls++;
            instance.Generation = context.Generation;
            instance.Outcome = ChaosOutcome.Running;
            var behaviour = instance.Slot.Scope == ChaosScope.Session ? instance.Slot.For(context.Generation)
                : instance.Slot.Scope == ChaosScope.Global ? instance.Slot.For(0)
                : instance.Slot.Default;
            Log($"start {instance.Label} gen {context.Generation}{(context.IsRestart ? " restart" : "")}: {behaviour}");
            CheckStart(instance, context);

            try
            {
                await Execute(instance, behaviour, context, token);
                instance.Outcome = ChaosOutcome.Completed;
                Log($"completed {instance.Label}");
            }
            catch (Exception exception)
            {
                instance.Outcome = ChaosOutcome.Threw;
                instance.Error = exception;
                Log($"threw {instance.Label}: {exception.GetType().Name}");
                throw;
            }
        }

        private async Task Execute(ChaosInstance instance, ChaosBehaviour behaviour, InitContext context, CancellationToken token)
        {
            var accepted = true;
            var label = $"{instance.Slot.Name}-g{context.Generation}";
            if (behaviour.Restart && behaviour.RestartBeforeFirstAwait)
                accepted = FireRestart(instance, behaviour.Token, token, label, out _);

            for (var i = 0; i < behaviour.Yields; i++)
            {
                await Task.Yield();
                if (behaviour.Progress) context.ReportProgress((i + 1f) / behaviour.Yields);
            }
            if (behaviour.DelayMs > 0) await Task.Delay(behaviour.DelayMs, token);

            if (behaviour.Restart && !behaviour.RestartBeforeFirstAwait)
                accepted = FireRestart(instance, behaviour.Token, token, label, out _);

            switch (behaviour.Then)
            {
                case ChaosAct.Complete:
                    return;
                case ChaosAct.Throw:
                    throw new InvalidOperationException($"chaos: {instance.Label} failed");
                case ChaosAct.ThrowCancelled:
                    throw new OperationCanceledException($"chaos: {instance.Label} bailed out");
                case ChaosAct.HangIgnoringToken:
                    await Task.Delay(behaviour.HangMs);
                    return;
                case ChaosAct.Halt:
                    context.Halt("chaos-halt-" + instance.Slot.Name);
                    return;
                case ChaosAct.Park:
                    if (instance.Slot.Scope == ChaosScope.Child) { /* only child tasks wait on it */ }
                    else if (!behaviour.Restart && instance.Slot.TimeoutSeconds <= 0) Hang($"{instance.Label} parks (gated/unbounded)");
                    else if (behaviour.Restart && !accepted) Hang($"{instance.Label} parks after its restart request was refused");
                    await Task.Delay(Timeout.Infinite, token);
                    return;
            }
        }

        private void CheckStart(ChaosInstance instance, InitContext context)
        {
            if (instance.InitCalls > 1) Violation($"InitializeAsync called {instance.InitCalls} times on {instance.Label}");
            if (DisposeSettledSeq >= 0) Violation($"{instance.Label} started after DisposeAsync settled");
            else if (DisposeCalledSeq >= 0) Violation($"{instance.Label} started after DisposeAsync was called");

            switch (instance.Slot.Scope)
            {
                case ChaosScope.Global:
                    if (context.Scope != "global") Violation($"{instance.Label} saw scope '{context.Scope}'");
                    if (context.Generation != 0 || context.IsRestart)
                        Violation($"{instance.Label} (global) saw generation {context.Generation}, IsRestart {context.IsRestart}");
                    break;
                case ChaosScope.Session:
                    if (context.Scope != "session") Violation($"{instance.Label} saw scope '{context.Scope}'");
                    if (context.IsRestart != (context.Generation > 0))
                        Violation($"{instance.Label} saw IsRestart {context.IsRestart} with generation {context.Generation}");
                    if (context.Generation != instance.BuildGeneration)
                        Violation($"{instance.Label} saw generation {context.Generation} but its build #{instance.Build} was generation {instance.BuildGeneration}");
                    if (context.Generation < MaxSessionGenerationStarted)
                        Violation($"{instance.Label} of generation {context.Generation} started after a service of generation {MaxSessionGenerationStarted} started");
                    MaxSessionGenerationStarted = Math.Max(MaxSessionGenerationStarted, context.Generation);
                    if (instance.Build <= DoomedUpTo)
                        Violation($"{instance.Label} started although an accepted restart doomed session builds up to #{DoomedUpTo}");
                    if (Host.IsQuitting) Violation($"{instance.Label} started after OnQuitting");
                    break;
                default:
                    if (context.Scope != instance.ScopeName) Violation($"{instance.Label} saw scope '{context.Scope}'");
                    if (instance.ChildUnderSession && instance.ParentBuild <= DoomedUpTo)
                        Violation($"{instance.Label} (below session build #{instance.ParentBuild}) started although a restart doomed it");
                    break;
            }

            foreach (var dep in instance.Slot.Deps)
            {
                var slot = SlotsOf(instance.Slot.Scope)[dep];
                CheckDependency(instance, Find(slot, instance.Build));
            }
            foreach (var dep in instance.Slot.ParentDeps)
            {
                if (instance.Slot.Scope == ChaosScope.Session) CheckDependency(instance, Find(Scenario.Global[dep], 0));
                else if (instance.ChildUnderSession) CheckDependency(instance, Find(Scenario.Session[dep], instance.ParentBuild));
            }
        }

        private List<ChaosSlot> SlotsOf(ChaosScope scope)
            => scope == ChaosScope.Global ? Scenario.Global : scope == ChaosScope.Session ? Scenario.Session : Scenario.Child;

        private ChaosInstance? Find(ChaosSlot slot, int build)
            => Instances.LastOrDefault(i => i.Slot == slot && i.Build == build);

        private void CheckDependency(ChaosInstance dependent, ChaosInstance? dependency)
        {
            if (dependency == null)
            {
                Violation($"{dependent.Label} started but one of its dependencies was never constructed");
                return;
            }
            if (dependency.Outcome == ChaosOutcome.Completed) return;
            if (dependency.Slot.Optional)
            {
                if (dependency.Outcome == ChaosOutcome.Threw) return;
                if (dependency.Outcome == ChaosOutcome.Running && StatusOf(dependency) == ServiceState.Degraded) return;
            }
            Violation($"{dependent.Label} started before its dependency {dependency.Label} was done " +
                      $"(harness outcome {dependency.Outcome}, framework state {StatusOf(dependency)?.ToString() ?? "?"})");
        }

        private ServiceState? StatusOf(ChaosInstance instance)
        {
            var run = instance.Slot.Scope == ChaosScope.Global ? Host.GlobalRun
                : instance.Slot.Scope == ChaosScope.Session ? Host.SessionRun
                : Host.ChildRuns.FirstOrDefault(r => r.Name == instance.ScopeName);
            if (run == null) return null;
            foreach (var service in run.GetStatus().Services)
            {
                if (service.Name == instance.Slot.Name) return service.State;
            }
            return null;
        }

        public ValueTask OnDisposeAsync(ChaosInstance instance)
        {
            instance.DisposeAsyncCalls++;
            Log($"DisposeAsync {instance.Label} (#{instance.DisposeAsyncCalls})");
            if (instance.Slot.AsyncDisposeThrows)
                return new ValueTask(Task.FromException(new InvalidOperationException($"chaos: DisposeAsync of {instance.Label} exploded")));
            return default;
        }

        public void OnDispose(ChaosInstance instance)
        {
            instance.DisposeCalls++;
            Log($"Dispose {instance.Label} (#{instance.DisposeCalls})");
            if (instance.Slot.SyncDisposeThrows) throw new InvalidOperationException($"chaos: Dispose of {instance.Label} exploded");
        }

        public void MarkDisposeCalled()
        {
            if (DisposeCalledSeq < 0) DisposeCalledSeq = Seq();
        }

        public string Dump(int max = 400)
        {
            var text = new StringBuilder();
            var from = Math.Max(0, Events.Count - max);
            if (from > 0) text.AppendLine($"... {from} earlier events omitted");
            for (var i = from; i < Events.Count; i++) text.AppendLine(Events[i]);
            return text.ToString();
        }
    }

    // ---------------------------------------------------------------------- service bases

    public abstract class ChaosService : IAsyncInitializable
    {
        protected ChaosService(ChaosWorld world)
        {
            World = world;
            Instance = world.Constructed(this);
        }

        public ChaosWorld World { get; }
        public ChaosInstance Instance { get; }

        public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            => World.RunService(Instance, context, cancellationToken);
    }

    public abstract class ChaosAsyncDisposableService : ChaosService, IAsyncDisposable
    {
        protected ChaosAsyncDisposableService(ChaosWorld world) : base(world) { }

        public ValueTask DisposeAsync() => World.OnDisposeAsync(Instance);
    }

    public abstract class ChaosSyncDisposableService : ChaosService, IDisposable
    {
        protected ChaosSyncDisposableService(ChaosWorld world) : base(world) { }

        public void Dispose() => World.OnDispose(Instance);
    }

    public abstract class ChaosBothDisposableService : ChaosService, IAsyncDisposable, IDisposable
    {
        protected ChaosBothDisposableService(ChaosWorld world) : base(world) { }

        public ValueTask DisposeAsync() => World.OnDisposeAsync(Instance);

        public void Dispose() => World.OnDispose(Instance);
    }
}
