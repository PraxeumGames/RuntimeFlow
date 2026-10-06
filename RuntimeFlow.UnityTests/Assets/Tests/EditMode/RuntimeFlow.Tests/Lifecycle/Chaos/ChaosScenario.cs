using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace RuntimeFlow.Tests.Lifecycle.Chaos
{
    /// <summary>What a service does once its preamble (yields, delay, optional restart request) is over.</summary>
    public enum ChaosAct
    {
        /// <summary>Return normally.</summary>
        Complete,

        /// <summary>Throw <see cref="InvalidOperationException"/>.</summary>
        Throw,

        /// <summary>Throw a plain <see cref="OperationCanceledException"/> (not tied to the token).</summary>
        ThrowCancelled,

        /// <summary>Ignore the token and wait a bounded time, then return.</summary>
        HangIgnoringToken,

        /// <summary>Wait on the own token forever.</summary>
        Park,

        /// <summary>Call <see cref="InitContext.Halt"/>, then return.</summary>
        Halt
    }

    /// <summary>Which token a service passes to its own <see cref="RuntimeFlowHost.RestartAsync"/> call.</summary>
    public enum ChaosToken { None, Own, AlreadyCancelled }

    public enum ChaosDisposal { None, Async, Sync, Both }

    public enum ChaosScope { Global, Session, Child }

    /// <summary>Script of one InitializeAsync call.</summary>
    public sealed class ChaosBehaviour
    {
        public int Yields;
        public int DelayMs;
        public bool Progress;
        public bool Restart;
        public bool RestartBeforeFirstAwait;
        public ChaosToken Token;
        public ChaosAct Then = ChaosAct.Complete;
        public int HangMs;

        public static ChaosBehaviour Completing(int yields = 1) => new ChaosBehaviour { Yields = yields };

        public override string ToString()
        {
            var text = new StringBuilder();
            if (Restart && RestartBeforeFirstAwait) text.Append("restart(").Append(Token).Append(")>");
            if (Yields > 0) text.Append('y').Append(Yields.ToString(CultureInfo.InvariantCulture));
            if (DelayMs > 0) text.Append(" d").Append(DelayMs.ToString(CultureInfo.InvariantCulture));
            if (Progress) text.Append(" p");
            if (Restart && !RestartBeforeFirstAwait) text.Append(">restart(").Append(Token).Append(')');
            text.Append('>').Append(Then);
            if (Then == ChaosAct.HangIgnoringToken) text.Append(HangMs.ToString(CultureInfo.InvariantCulture)).Append("ms");
            return text.ToString();
        }
    }

    /// <summary>One service of a scenario: its flags, edges, disposal shape and per-generation script.</summary>
    public sealed class ChaosSlot
    {
        public ChaosScope Scope;
        public int Index;
        public string Name = "";
        public bool Optional;
        public bool UserGated;
        public double TimeoutSeconds;
        public string? Phase;
        public ChaosDisposal Disposal;
        public bool AsyncDisposeThrows;
        public bool SyncDisposeThrows;

        /// <summary>Same-scope dependencies (indices of earlier slots); some as constructor parameters, some as [DependsOn].</summary>
        public List<int> Deps = new List<int>();
        public List<bool> DepViaAttribute = new List<bool>();

        /// <summary>Dependencies on slots of the parent scope (global for session, session for a child).</summary>
        public List<int> ParentDeps = new List<int>();

        /// <summary>Generation whose session build constructs this service with a synchronous restart request, or -1.</summary>
        public int CtorRestartGeneration = -1;

        public Dictionary<int, ChaosBehaviour> ByGeneration = new Dictionary<int, ChaosBehaviour>();
        public ChaosBehaviour Default = ChaosBehaviour.Completing();

        public Type Type = null!;

        public ChaosBehaviour For(int generation)
            => ByGeneration.TryGetValue(generation, out var behaviour) ? behaviour : Default;

        public override string ToString()
        {
            var text = new StringBuilder();
            text.Append(Name);
            var flags = new List<string>();
            if (Optional) flags.Add("optional");
            if (UserGated) flags.Add("gated");
            if (TimeoutSeconds > 0) flags.Add("timeout=" + TimeoutSeconds.ToString(CultureInfo.InvariantCulture));
            if (Phase != null) flags.Add("phase=" + Phase);
            if (Disposal != ChaosDisposal.None)
                flags.Add("dispose=" + Disposal + (AsyncDisposeThrows ? "!async" : "") + (SyncDisposeThrows ? "!sync" : ""));
            if (CtorRestartGeneration >= 0) flags.Add("ctor-restart@g" + CtorRestartGeneration.ToString(CultureInfo.InvariantCulture));
            if (flags.Count > 0) text.Append(" [").Append(string.Join(", ", flags)).Append(']');
            if (Deps.Count > 0)
            {
                text.Append(" deps(");
                for (var i = 0; i < Deps.Count; i++)
                {
                    if (i > 0) text.Append(',');
                    text.Append(Deps[i].ToString(CultureInfo.InvariantCulture)).Append(DepViaAttribute[i] ? "a" : "c");
                }
                text.Append(')');
            }
            if (ParentDeps.Count > 0) text.Append(" parentDeps(").Append(string.Join(",", ParentDeps)).Append(')');
            foreach (var pair in ByGeneration.OrderBy(p => p.Key))
                text.Append(" g").Append(pair.Key.ToString(CultureInfo.InvariantCulture)).Append('=').Append(pair.Value);
            text.Append(" default=").Append(Default);
            return text.ToString();
        }
    }

    public enum ChaosDriverKind
    {
        UiRestart,
        UiRestartCancelledLater,
        UiRestartAlreadyCancelled,
        CancelStartToken,
        Dispose,
        Quit,
        ChildUnderSession,
        ChildUnderGlobal,
        StartAgain
    }

    /// <summary>An external action of the "game": fired after a wait measured in frames or milliseconds.</summary>
    public sealed class ChaosDriverAction
    {
        public ChaosDriverKind Kind;
        public int WaitYields;
        public int WaitMs;
        public int CancelAfterMs;

        /// <summary>Until this is true, the action waits; used by the fixed scenarios to act mid-chain.</summary>
        public Func<ChaosWorld, bool>? WaitFor;

        public override string ToString()
            => $"{Kind} after {(WaitFor != null ? "condition " : "")}{WaitYields}y/{WaitMs}ms" +
               (Kind == ChaosDriverKind.UiRestartCancelledLater ? $" cancel+{CancelAfterMs}ms" : "");
    }

    /// <summary>A complete, replayable scenario generated from a seed.</summary>
    public sealed class ChaosScenario
    {
        public int Seed;
        public string Label = "";
        public List<ChaosSlot> Global = new List<ChaosSlot>();
        public List<ChaosSlot> Session = new List<ChaosSlot>();
        public List<ChaosSlot> Child = new List<ChaosSlot>();
        public List<ChaosDriverAction> Actions = new List<ChaosDriverAction>();
        public int SessionBuildThrowsAt = -1;
        public int MaxRestarts = 50;
        public bool StartWithToken;
        public int CancellationGraceMs = 100;

        public IEnumerable<ChaosSlot> AllSlots => Global.Concat(Session).Concat(Child);

        public string Describe()
        {
            var text = new StringBuilder();
            text.Append("scenario ").Append(Label.Length > 0 ? Label : "seed " + Seed.ToString(CultureInfo.InvariantCulture))
                .Append(": budget ").Append(MaxRestarts.ToString(CultureInfo.InvariantCulture))
                .Append(", grace ").Append(CancellationGraceMs.ToString(CultureInfo.InvariantCulture)).Append("ms");
            if (SessionBuildThrowsAt >= 0) text.Append(", session build #").Append(SessionBuildThrowsAt).Append(" throws");
            if (StartWithToken) text.Append(", start token");
            text.AppendLine();
            foreach (var slot in AllSlots) text.Append("  ").Append(slot.Scope).Append(' ').AppendLine(slot.ToString());
            foreach (var action in Actions) text.Append("  action ").AppendLine(action.ToString());
            return text.ToString();
        }

        // ------------------------------------------------------------------ generation

        /// <summary>Generates the scenario of <paramref name="seed"/>; the same seed always yields the same plan.</summary>
        public static ChaosScenario Generate(int seed)
        {
            var rng = new Random(seed);
            var scenario = new ChaosScenario { Seed = seed };

            var globals = rng.Next(1, 5);
            var sessions = rng.Next(2, 7);
            var phasedGlobal = rng.NextDouble() < 0.2;
            var phasedSession = rng.NextDouble() < 0.3;

            for (var i = 0; i < globals; i++)
                scenario.Global.Add(NewSlot(rng, ChaosScope.Global, i, "G" + i, phasedGlobal, globals, scenario.Global, null));
            for (var i = 0; i < sessions; i++)
                scenario.Session.Add(NewSlot(rng, ChaosScope.Session, i, "S" + i, phasedSession, sessions, scenario.Session, scenario.Global));

            var children = rng.Next(1, 3);
            for (var i = 0; i < children; i++)
            {
                var slot = new ChaosSlot { Scope = ChaosScope.Child, Index = i, Name = "C" + i };
                slot.Disposal = (ChaosDisposal)rng.Next(0, 4);
                slot.AsyncDisposeThrows = rng.NextDouble() < 0.15;
                slot.SyncDisposeThrows = rng.NextDouble() < 0.15;
                if (i > 0 && rng.NextDouble() < 0.5)
                {
                    slot.Deps.Add(0);
                    slot.DepViaAttribute.Add(rng.NextDouble() < 0.5);
                }
                var roll = rng.Next(100);
                slot.Default = roll < 55 ? new ChaosBehaviour { Yields = rng.Next(0, 4) }
                    : roll < 70 ? new ChaosBehaviour { Yields = 1, Then = ChaosAct.Throw }
                    : roll < 85 ? new ChaosBehaviour { Yields = 1, Then = ChaosAct.Park }
                    : new ChaosBehaviour { Then = ChaosAct.HangIgnoringToken, HangMs = rng.Next(20, 250) };
                scenario.Child.Add(slot);
            }

            if (rng.NextDouble() < 0.07) scenario.SessionBuildThrowsAt = rng.Next(0, 3);
            if (rng.NextDouble() < 0.12) scenario.MaxRestarts = rng.Next(1, 3);
            scenario.StartWithToken = rng.NextDouble() < 0.4;

            var actions = rng.Next(0, 5);
            for (var i = 0; i < actions; i++)
            {
                var action = new ChaosDriverAction();
                var roll = rng.Next(100);
                action.Kind = roll < 22 ? ChaosDriverKind.UiRestart
                    : roll < 30 ? ChaosDriverKind.UiRestartCancelledLater
                    : roll < 36 ? ChaosDriverKind.UiRestartAlreadyCancelled
                    : roll < 44 ? ChaosDriverKind.CancelStartToken
                    : roll < 52 ? ChaosDriverKind.Dispose
                    : roll < 58 ? ChaosDriverKind.Quit
                    : roll < 76 ? ChaosDriverKind.ChildUnderSession
                    : roll < 90 ? ChaosDriverKind.ChildUnderGlobal
                    : ChaosDriverKind.StartAgain;
                if (rng.NextDouble() < 0.5) action.WaitYields = rng.Next(0, 12);
                else action.WaitMs = rng.Next(0, 250);
                action.CancelAfterMs = rng.Next(0, 150);
                scenario.Actions.Add(action);
            }

            return scenario;
        }

        private static ChaosSlot NewSlot(Random rng, ChaosScope scope, int index, string name, bool phased, int count,
            List<ChaosSlot> earlier, List<ChaosSlot>? parents)
        {
            var slot = new ChaosSlot { Scope = scope, Index = index, Name = name };
            slot.Optional = rng.NextDouble() < 0.25;
            slot.UserGated = rng.NextDouble() < 0.08;
            if (!slot.UserGated && rng.NextDouble() < 0.15) slot.TimeoutSeconds = 0.15;
            if (phased)
            {
                // Earlier slots are early, later ones late (or unlabelled, which lands in the last phase),
                // so a dependency never points from an early service to a late one.
                var cut = Math.Max(1, count / 2);
                slot.Phase = index < cut ? "early" : (rng.NextDouble() < 0.5 ? "late" : null);
            }

            var disposal = rng.Next(100);
            slot.Disposal = disposal < 40 ? ChaosDisposal.None
                : disposal < 65 ? ChaosDisposal.Async
                : disposal < 85 ? ChaosDisposal.Sync
                : ChaosDisposal.Both;
            slot.AsyncDisposeThrows = rng.NextDouble() < 0.2;
            slot.SyncDisposeThrows = rng.NextDouble() < 0.2;

            for (var j = 0; j < earlier.Count; j++)
            {
                if (rng.NextDouble() >= 0.35) continue;
                slot.Deps.Add(j);
                slot.DepViaAttribute.Add(rng.NextDouble() < 0.4);
            }
            if (parents != null)
            {
                for (var j = 0; j < parents.Count; j++)
                {
                    if (rng.NextDouble() < 0.15) slot.ParentDeps.Add(j);
                }
            }

            if (scope == ChaosScope.Session && rng.NextDouble() < 0.05) slot.CtorRestartGeneration = rng.Next(0, 2);

            var generations = scope == ChaosScope.Global ? 1 : 4;
            for (var generation = 0; generation < generations; generation++)
                slot.ByGeneration[generation] = NewBehaviour(rng, slot, generation);
            slot.Default = new ChaosBehaviour { Yields = rng.Next(0, 3) };
            return slot;
        }

        private static ChaosBehaviour NewBehaviour(Random rng, ChaosSlot slot, int generation)
        {
            var behaviour = new ChaosBehaviour();
            if (rng.NextDouble() < 0.6) behaviour.Yields = rng.Next(0, 4);
            else behaviour.DelayMs = rng.Next(0, 40);
            behaviour.Progress = rng.NextDouble() < 0.4;

            var global = slot.Scope == ChaosScope.Global;
            var restartWeight = global ? 10 : (generation <= 2 ? 22 : 0);
            var parkWeight = slot.UserGated || slot.TimeoutSeconds > 0 ? 12 : 0;

            var haltWeight = 3;
            // Global failures end the whole case before any session exists; keep them rarer there.
            var throwWeight = global ? 2 : 6;
            var bailWeight = global ? 1 : 5;
            var total = 45 + throwWeight + bailWeight + 6 + haltWeight + restartWeight + parkWeight;
            var roll = rng.Next(total);
            if ((roll -= 45) < 0) behaviour.Then = ChaosAct.Complete;
            else if ((roll -= throwWeight) < 0) behaviour.Then = ChaosAct.Throw;
            else if ((roll -= bailWeight) < 0) behaviour.Then = ChaosAct.ThrowCancelled;
            else if ((roll -= 6) < 0)
            {
                behaviour.Then = ChaosAct.HangIgnoringToken;
                behaviour.HangMs = rng.Next(20, 300);
            }
            else if ((roll -= haltWeight) < 0) behaviour.Then = ChaosAct.Halt;
            else if ((roll -= restartWeight) < 0)
            {
                behaviour.Restart = true;
                behaviour.RestartBeforeFirstAwait = rng.NextDouble() < 0.3;
                var token = rng.Next(100);
                behaviour.Token = token < 60 ? ChaosToken.None : token < 85 ? ChaosToken.Own : ChaosToken.AlreadyCancelled;
                var then = rng.Next(100);
                // A global service that parks on its token after requesting a restart is the documented
                // permitted hang (nothing ever cancels it); it is left out of the generator.
                behaviour.Then = then < 40 && !global ? ChaosAct.Park
                    : then < 70 ? ChaosAct.Complete
                    : then < 80 ? ChaosAct.Throw
                    : ChaosAct.ThrowCancelled;
            }
            else
            {
                behaviour.Then = ChaosAct.Park;
            }
            return behaviour;
        }

        // ------------------------------------------------------------------ types

        /// <summary>Emits one concrete service type per slot, with its constructor and attribute edges.</summary>
        public void DefineTypes()
        {
            var ns = "Chaos.Case" + ChaosTypes.NextCase().ToString(CultureInfo.InvariantCulture);
            foreach (var slot in Global) slot.Type = ChaosTypes.Define(ns, slot, Global, null);
            foreach (var slot in Session) slot.Type = ChaosTypes.Define(ns, slot, Session, Global);
            foreach (var slot in Child) slot.Type = ChaosTypes.Define(ns, slot, Child, Session);
        }
    }

    /// <summary>
    /// Emits the service types of a scenario at runtime: [DependsOn] and [Init] are static metadata, so a
    /// random graph needs types of its own. Every type derives from one of the <see cref="ChaosService"/>
    /// bases and forwards its <see cref="ChaosWorld"/> to it; extra constructor parameters are the
    /// constructor edges of the graph and are otherwise ignored.
    /// </summary>
    public static class ChaosTypes
    {
        private static readonly AssemblyName DynamicName = new AssemblyName("RuntimeFlow.Tests.ChaosDynamic");
        private static readonly AssemblyBuilder Assembly;
        private static readonly ModuleBuilder Module;
        private static int _cases;

        static ChaosTypes()
        {
            Assembly = AssemblyBuilder.DefineDynamicAssembly(DynamicName, AssemblyBuilderAccess.Run);
            Module = Assembly.DefineDynamicModule(DynamicName.Name);
            // Attribute blobs name their Type arguments; make sure those names resolve to this assembly.
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
                new AssemblyName(args.Name).Name == DynamicName.Name ? Assembly : null;
        }

        public static int NextCase() => ++_cases;

        public static Type Define(string ns, ChaosSlot slot, List<ChaosSlot> sameScope, List<ChaosSlot>? parentScope)
        {
            var baseType = slot.Disposal switch
            {
                ChaosDisposal.Async => typeof(ChaosAsyncDisposableService),
                ChaosDisposal.Sync => typeof(ChaosSyncDisposableService),
                ChaosDisposal.Both => typeof(ChaosBothDisposableService),
                _ => typeof(ChaosService)
            };

            var type = Module.DefineType(ns + "." + slot.Name, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, baseType);

            var parameters = new List<Type> { typeof(ChaosWorld) };
            for (var i = 0; i < slot.Deps.Count; i++)
            {
                var target = sameScope[slot.Deps[i]].Type;
                if (slot.DepViaAttribute[i])
                {
                    type.SetCustomAttribute(new CustomAttributeBuilder(
                        typeof(DependsOnAttribute).GetConstructor(new[] { typeof(Type) })!, new object[] { target }));
                }
                else
                {
                    parameters.Add(target);
                }
            }
            if (parentScope != null)
            {
                foreach (var dep in slot.ParentDeps) parameters.Add(parentScope[dep].Type);
            }

            var baseCtor = baseType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(ChaosWorld) }, null)!;
            var ctor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, parameters.ToArray());
            var il = ctor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, baseCtor);
            il.Emit(OpCodes.Ret);

            var fields = new List<FieldInfo>();
            var values = new List<object>();
            var init = typeof(InitAttribute);
            if (slot.Optional) { fields.Add(init.GetField(nameof(InitAttribute.Optional))!); values.Add(true); }
            if (slot.UserGated) { fields.Add(init.GetField(nameof(InitAttribute.UserGated))!); values.Add(true); }
            if (slot.TimeoutSeconds > 0) { fields.Add(init.GetField(nameof(InitAttribute.TimeoutSeconds))!); values.Add(slot.TimeoutSeconds); }
            if (slot.Phase != null) { fields.Add(init.GetField(nameof(InitAttribute.Phase))!); values.Add(slot.Phase); }
            if (fields.Count > 0)
            {
                type.SetCustomAttribute(new CustomAttributeBuilder(init.GetConstructor(Type.EmptyTypes)!,
                    Array.Empty<object>(), fields.ToArray(), values.ToArray()));
            }

            return type.CreateTypeInfo()!.AsType();
        }
    }
}
