# RuntimeFlow 1.0 — design

This document explains why the framework looks the way it does, what the scheduler guarantees, and the
exact text it produces. It is the reference the test suite asserts against; changing a message here
means changing a test.

## 1. One graph

**The container is the graph.** Startup order is not declared anywhere: it is derived from the
VContainer registrations of the scope being initialized. A service is a class implementing
`IAsyncInitializable` registered as `Lifetime.Singleton`; its dependencies are the parameters of the
constructor VContainer would inject.

Three things were deliberately *not* built:

- **No central flow file.** A file listing steps duplicates the dependency information already present
  in the constructors, and drifts from it. Deleting a service must not require editing a second place.
- **No stage-marker interfaces.** The 0.x design had 22 lifecycle markers on three orthogonal axes, so
  two independent systems (marker stage and topological layer) decided order without knowing about each
  other. One graph has one answer.
- **No custom container.** 0.x reimplemented lifetime, scoping, ownership and disposal on top of
  VContainer (5.8k LOC) and required a fork. 1.0 uses stock VContainer and never wraps
  `IContainerBuilder`, so factories, decorators, entry points, build callbacks and `CreateScope` all
  keep working exactly as documented upstream.

### Discovery

`GraphBuilder` asks the scope for its own registration of the initializable collection:

```csharp
scope.TryGetRegistration(typeof(IReadOnlyList<IAsyncInitializable>), out var registration);
var local = (IEnumerable<Registration>)registration.Provider;     // the scope's own registrations only
foreach (var r in local) instance = scope.Resolve(r);
```

This matters: `Resolve<IReadOnlyList<IAsyncInitializable>>()` on a child scope *merges* the parents',
which would make a child re-run its parents' services (and create child-owned copies of any scoped
registration). Enumerating the local provider instead keeps discovery scope-local and hands us
`Registration.Lifetime` and `Registration.ImplementationType` for validation. If the provider is not
enumerable — a VContainer version we do not know — the build fails with
`Unsupported VContainer version: cannot enumerate the initializable registrations of scope 'session'.`

Every service is constructed during the build, before the run starts, so a resolution problem surfaces
as a named graph error rather than as one container exception for the whole scope. A construction
failure is attached to its own node and replayed as that node's failure when the run begins.

Services of parent scopes become **external** nodes: they take part in edges and in `Describe()`, count
as already completed, and are never scheduled. That is what makes a session graph able to depend on
global services without re-initializing them.

Validation performed at build time:

| Problem | Message |
|---|---|
| Non-singleton | `Foo is registered with Lifetime.Transient in scope 'session'; initializable services must be Lifetime.Singleton (a Scoped/Transient service would be re-created uninitialized in child scopes).` |
| Two registrations of the same type | Warning: `Foo is registered as IAsyncInitializable 2 times; each registration will be initialized.` |
| Construction failed | `Could not construct Foo in scope 'session': <message>. Register the missing type in 'session' or a parent scope.` |
| `DefaultPhase` not in `Phases` | `RuntimeFlowOptions.DefaultPhase is 'x', but RuntimeFlowOptions.Phases is [platform, content].` |
| Phase used, none declared | `Foo declares phase 'content', but RuntimeFlowOptions.Phases is empty. Declare the ordered phase list in RuntimeFlowOptions.Phases.` |
| Unknown phase | `Foo declares phase 'x', but RuntimeFlowOptions.Phases is [platform, content].` |
| `UserGated` + `TimeoutSeconds` | `Foo is user-gated and declares TimeoutSeconds = 30; user-gated services never time out. Remove one of them.` |

### Edge rules

`ConstructorEdges` picks the constructor the way VContainer does — among
`DeclaredOnly | Instance | Public | NonPublic`, the single one marked `[Inject]`, otherwise the one with
the most parameters (first wins on a tie) — and caches `Type → ParameterInfo[]`. Both the runtime type
of the resolved instance and `Registration.ImplementationType` are inspected, which is how a decorator
registered as `Register<I>(resolver => new Decorator(inner))` still contributes its edges.

| Parameter | Meaning |
|---|---|
| Another service's type or an interface it exposes | Edge. An interface with several implementations produces an edge to each of them. |
| `IEnumerable<T>`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>`, `IList<T>`, `ICollection<T>`, `List<T>`, `T[]` | Edges to every service assignable to `T`, excluding the node itself — the barrier idiom for "after all background tasks". |
| `Func<…>`, `Lazy<T>`, `ILazy<T>` | **Not** an edge. The documented way to break a cycle; listed in `Describe()` as a `lazy:` row. |
| `IObjectResolver`, `IScopedObjectResolver`, `IContainerBuilder`, `RuntimeFlowHost`, `ScopeRun`, `RuntimeFlowOptions`, `InitContext`, `ILogger`, `object`, `string`, primitives, enums, `decimal` | Ignored. |
| Anything resolving to a parent registration | Edge to the external node. |

Every edge remembers where it came from, and the origin is printed verbatim in cycle errors and in
`Describe()`: `ctor: ICdnMirrorSelector mirrors`, `DependsOn`, `phase barrier`.

`[DependsOn(typeof(T))]` targets every node — local or external — assignable to `T`, excluding the
declaring node. Zero matches is an error, because a silently dropped ordering constraint is exactly how
0.x produced a production restart loop:

```text
RemoteCatalog declares [DependsOn(typeof(IMirrorSelector))], but no initializable service assignable to
IMirrorSelector is registered in scope 'session' or its parents. A target must implement
IAsyncInitializable and live in the same scope or a parent scope; a parent scope can never depend on a
child scope. Known services — session: Auth, Config; global: Analytics, Crash.
```

The direction rule in that message is how a Global-depends-on-Session mistake presents itself: the
target is invisible from the parent scope, so it is reported as unknown rather than as a cycle.

### Phase barriers

Phases are optional and off by default (`RuntimeFlowOptions.Phases` is empty). When declared, each
phase gets a synthetic barrier node: barrier *i* depends on every service of phase *i* and on barrier
*i-1*; every service of phase *i* depends on barrier *i-1*. Empty phases chain harmlessly.

Phases belong to the scopes that use them. `RuntimeFlowOptions.Phases` is shared by every scope, but a
scope where no service carries `[Init(Phase = …)]` gets no barriers at all and its services report
`Phase == null` — a Global container of unlabelled services is not swept into the last phase of a list
the session declares. From the first labelled service of a scope on, that scope has barriers and a
service without `[Init(Phase = …)]` lands in the last phase, or in `RuntimeFlowOptions.DefaultPhase`
when one is set. This is deliberate: a forgotten label makes a service *late*, which is at worst slow,
rather than *early*, which is a race.

Because barriers are ordinary edges, a phase inversion is reported as a cycle — with an annotation that
says what actually happened:

```text
Initialization graph of scope 'session' has a cycle: Catalog -> Group -> Runner -> Catalog. Edges:
Catalog -> Group (ctor: IGroupService groups), Group -> Runner (DependsOn), Runner -> Catalog (ctor:
ICatalog catalog). (Catalog in phase 'content' depends on Runner in phase 'session'; a service cannot
depend on a later phase). Remove one dependency or take it lazily (Func<T>/ILazy<T> parameters are not
edges).
```

Cycle detection is a topological sweep; whatever does not settle is walked depth-first to recover an
actual path, and each edge on it is printed with its origin. All of this happens in `ScopeRun.Create`,
before the first `InitializeAsync` of the scope.

## 2. The scheduler

`Scheduler` is dynamic, not layered: each node carries a count of unfinished dependencies, everything
at zero is started in stable index order, and finishing a node decrements its dependents and starts
those that just became ready. A service therefore waits for *its own* dependencies, not for a wave.
Independent services overlap through interleaved awaits.

It is **single-threaded by construction**. Everything runs on the `SynchronizationContext` captured by
the caller of `RunAsync` — `UnityEngine`'s in play mode, the editor's in EditMode, which is why the
EditMode suite is authoritative and the PlayMode suite only covers frame-driven behaviour. There is no
`ConfigureAwait(false)` anywhere in the package (the layout guard enforces this), no locks, no thread
affinity checks, no scheduler abstraction. A null context is a one-line warning
(`SynchronizationContext.Current is null; continuations run inline on the calling thread.`) rather than
a failure.

One periodic loop per run (`StallWatch`, at most 1 Hz, faster when a configured threshold demands it)
drives both timeout enforcement and the stall warning. Each service gets a `CancellationToken` linked
to the run's; it stays valid after `InitializeAsync` returns, so background work started during
initialization is cancelled by the next restart.

### Failure semantics

| Event | Behaviour |
|---|---|
| Required service throws | Node `Failed`; the run token is cancelled; in-flight services are awaited up to `CancellationGrace` (5 s) so their failures are collected too; then one `RuntimeFlowException`. |
| Optional service throws | Node `Degraded`, warning logged, dependents start anyway and see the name in `InitContext.DegradedServices`. The name also travels down: a parent-scope node that degraded stays `Degraded` as an external node of every child scope, so the session's services and its `Describe()` see the global degradation. `StartupResult.Degraded` stays per scope for `ScopeRun.RunAsync`; `RuntimeFlowHost.StartAsync`/`RestartAsync` return the union, global names first. |
| Required service fails behind a degraded one | Same failure handling, plus an annotation: every direct dependency of the failing service that is `Degraded` — own scope or parent — is named in the message as ` (after upstream X degraded)`. |
| Timeout | Only from `[Init(TimeoutSeconds = n)]`, scaled by `TimeoutMultiplier` (`0` disables all timeouts). There is no default deadline — 0.x had a self-learning 5-second watchdog that tore down bundle downloads and produced restart storms. User-gated services are exempt. |
| Stall | `StallWarningAfter` (10 s) of no service start, completion or `ReportProgress` call produces a warning naming what is running, what is awaiting the player and what is blocked. It never cancels anything. |
| `Halt` | First call wins; run state `Halted`; unstarted nodes skipped, in-flight ones cancelled; the result is `StartupOutcome.Halted` and nothing throws. |
| Caller cancellation | The run's task completes cancelled. |
| Grace exceeded | Services still running after `CancellationGrace` are marked `Cancelled` and abandoned with an error line; teardown continues, and their `InitContext` throws `ObjectDisposedException` afterwards. |

Message formats, verbatim:

```text
Initialization of scope 'session' failed: RemoteCatalog threw InvalidOperationException after 3.2s in
phase 'content'. Completed (7): Config, Auth, …; unfinished (4): QuestsWarmup (running 3.1s),
StartSession (blocked on RemoteCatalog), …. See InnerException.

Initialization of scope 'session' failed: Profile threw NullReferenceException after 0.1s in phase
'content' (after upstream Auth degraded). Completed (7): …; unfinished (4): …. See InnerException.

Initialization of scope 'session' failed: 2 services failed — RemoteCatalog (InvalidOperationException
after 3.2s), Profile (NullReferenceException after 0.1s, after upstream Auth degraded). Completed (7):
…; unfinished (4): …. InnerException is an AggregateException with the original exceptions.

[RuntimeFlow] session: Telemetry is optional and failed after 1.2s (HttpRequestException: host
unreachable); continuing degraded. Dependents: Analytics.

RemoteCatalog did not complete within 30.0s (limit 30s from [Init(TimeoutSeconds = 30)], multiplier 1.0).

[RuntimeFlow] session: no progress for 10.0s. Running: RemoteCatalog (12.4s). Awaiting player:
GdprConsent (12.4s). Blocked: StartSession (waits for RemoteCatalog) and 9 more.

[RuntimeFlow] session: awaiting player: GdprConsent (12.4s).

[RuntimeFlow] session: halted by UserUpdateCheck — 'user-update.required' after 4.2s (7/12 services
completed; cancelled: RemoteCatalog).

[RuntimeFlow] session: 2 services still running 5.0s after cancellation: A (5.0s), B (5.0s).
Continuing teardown; they must observe their CancellationToken.

Restart budget exceeded: 6 restarts within 60s (limit 5). Reasons: bundles-updated, auth-lost, ….
```

Unfinished entries are annotated with the reason they never finished: `(running 3.1s)`,
`(cancelled after 1.2s)`, `(blocked on X, Y)` or `(not started)`. Lists longer than five items are
capped with `and N more`. Durations use one decimal and the invariant culture.

Log levels: Debug for `Foo started (phase content)` and `Foo completed in 1.23s`; Info for run and
phase boundaries, halts and restart bookkeeping; Warning for stalls, degradation and refused restarts;
Error for run failures and teardown problems. Every line is prefixed `[RuntimeFlow] ` — the console
logger adds it when a message does not already carry it.

### Entry points

`RuntimeFlowHost` composes each scope as: register itself → register an entry-point exception handler
(first, so a consumer registration overrides it) → the consumer installer → ensure the entry-point
dispatcher is registered (without which `IInitializable` never runs in a plain `ContainerBuilder`).
VContainer runs `IInitializable` inside `Build()`, before the asynchronous graph, in registration order,
and swallows exceptions unless a handler is installed — hence the handler, which turns a swallowed
throw into a failed run:

```text
Initialization of scope 'session' failed: SaveMigration threw IOException while VContainer was running
the IInitializable entry points of the scope: disk full. See InnerException.
```

`IStartable` and `ITickable` are frame-driven and unordered with respect to asynchronous
initialization; that is a documented contract, not an oversight.

## 3. Restart and disposal

State flow of `RuntimeFlowHost.RestartAsync`:

```text
request ─▶ quitting?            ─ yes ▶ refuse (warning + InvalidOperationException)
        ─▶ started?             ─ no  ▶ InvalidOperationException ("start the host first")
        ─▶ restart pending?     ─ yes ▶ coalesce: join the run already in flight
        ─▶ budget exceeded?     ─ yes ▶ RuntimeFlowException with the list of reasons
        ─▶ cancel current run ─▶ dispose child runs (reverse creation order)
        ─▶ dispose session run ─▶ Global.CreateScope(composed session installer)
        ─▶ ScopeRun.Create ─▶ RunAsync(isRestart: true, generation + 1)
```

The whole sequence starts after a `Task.Yield`, so a service can request a restart from inside its own
`InitializeAsync`. `Generation` is stamped as started *before* the first `InitializeAsync` of the new
run, so a synchronous restart request from within it opens a new generation instead of joining the one
it is running in. Both `StartAsync` and `RestartAsync` follow the chain and return the result of the
**last** run, which is why an awaiter that started before a restart still observes the final outcome.

Disposal order, top to bottom:

1. Cancel the run; wait out `CancellationGrace`.
2. `IAsyncDisposable.DisposeAsync()` on every service the run started, in **reverse completion order**;
   failures are logged (`disposing Foo threw InvalidOperationException; continuing teardown.`) and never
   rethrown.
3. Dispose the service tokens and the run's cancellation sources; mark the generation abandoned, after
   which its `InitContext` throws `ObjectDisposedException`.
4. `scope.Dispose()` when the run owns the scope. VContainer then disposes `IDisposable` registrations
   in reverse creation order.

The framework never calls `Dispose()` itself, so nothing is disposed twice; synchronous disposal is
VContainer's business and follows its dependency-correct order. On the host, child runs go before the
session and the session before the global run; a global container supplied through
`RuntimeFlowHost.From` is never disposed.

## 4. Diagnostics

- **`Describe()`** renders the graph as an aligned table: index, phase, name, flags
  (`required|optional`, `user-gated`, `timeout Ns`, `weight N`), one indented row per edge with its
  origin (and `[global, initialized]` or `[global, degraded]` for external targets), the `lazy:`
  parameters, and a final line listing the services inherited from parent scopes. It answers "why did this run in that order" without
  a debugger, and it is snapshot-tested.
- **`GetStatus()`** returns immutable `RuntimeFlowStatus`/`ServiceStatus` snapshots: state, phase,
  elapsed (ticking while running), reported sub-progress, weight, dependencies, `WaitingOn`, error,
  weighted `Percent`, `RestartCount`. Safe to poll every frame; the dashboard polls at 4 Hz.
- **`IRuntimeFlowObserver`** is the push side, with default interface members so implementations stay
  small. Observers are wrapped: one that throws is logged and skipped.
- **`FlowRegistry`** is the only static in the package holding runtime objects, it is
  diagnostics-only, it holds **weak** references, prunes them on read, and is cleared on
  `RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)`. Runtime logic never
  reads it; it exists so the editor dashboard can find live hosts without the runtime depending on an
  ambient singleton.
- **JSON dump.** The dashboard serialises the current snapshot (scopes, services, states, timings,
  errors) with escaping, so a bug report can carry the whole run as text.

## 5. Testing philosophy

- **EditMode is authoritative.** The editor's synchronization context behaves like the player's, so the
  entire scheduler, graph, failure and restart surface is testable without entering play mode. PlayMode
  covers only what needs frames: `IStartable` on the first tick, continuations on the main thread,
  restart from a coroutine, timeouts under real frame pacing, disposal on exiting play mode.
- **Gates, not delays.** Tests drive services with `TaskCompletionSource` gates (`await service.Started`,
  then `service.Release()`), so ordering assertions are deterministic. Real time appears only in
  timeout and stall tests, with 50–200 ms thresholds.
- **Awaiting assertions.** NUnit's `Assert.ThrowsAsync` blocks the Unity main thread and deadlocks any
  posted continuation; the suite uses awaiting equivalents instead.
- **Overrides filter registrations.** `TestFlow.Override<T>` cannot rely on "register last, wins":
  VContainer resolves a collection by merging every registration, so the real service would still be
  discovered through `IReadOnlyList<IAsyncInitializable>` and would still be constructed and
  initialized. `OverridingContainerBuilder` therefore wraps the builder handed to the production
  installer, buffers the `RegistrationBuilder` objects, inspects each through the public `Build()` to
  read its `InterfaceTypes` and `ImplementationType`, drops the ones exposing the overridden type,
  forwards the rest untouched (lifetimes and parameters survive) and finally registers the replacement.
  No reflection, no internals. An override matching nothing fails the test by name.
- **Exact messages.** Tests assert on the strings in this document, because a diagnostic that is not
  pinned degrades silently.

## 6. Requirements from a real free-to-play client

The design was validated against fourteen requirements taken from a shipped client that used the 0.x
framework, and against the roughly 2k lines of glue it had written to work around the gaps.

| # | Requirement | Mechanism |
|---|---|---|
| R1 | A step waits for the player (consent, sign-in) without ever timing out | `UserGated`, exempt from timeout and stall, reported as `AwaitingPlayer` |
| R2 | A step can stop startup with no restart and no exception | `InitContext.Halt` plus `Optional` for the degradable variants |
| R3 | Retry with its own UI inside a step | No default timeout; the popup loop lives inside `InitializeAsync` |
| R4 | Bounded wait that degrades instead of failing | The service catches its own `TimeoutException` and is marked `Optional` |
| R5 | Data dependencies config → catalog → login → profile | Constructor edges; data travels through the service's own properties; a cycle is a readable error with the path |
| R6 | Weighted progress with sub-progress | `Weight`, `InitContext.ReportProgress`, `RuntimeFlowStatus.Percent` weighted across scopes |
| R7 | Per-step timeouts with a stable key | `[Init(TimeoutSeconds)]` on the class — no keying by registered interface |
| R8 | Background work must not look like a hang | No kill by default; stalls are warnings naming what is running and what is blocked |
| R9 | Restart with ordered reset on the main thread, under a UI cover, without leaks | `RestartAsync`: deferred, coalesced, single-threaded, reverse-order `DisposeAsync`, `OnRunStarted(isRestart)` for the cover |
| R10 | A step must know it is a restart | `InitContext.IsRestart` and `InitContext.Generation` |
| R11 | Scenes are the service's business | No scene loader in 1.0; a service loads what it needs inside `InitializeAsync` |
| R12 | Test bootstrap without network and without a `MonoBehaviour` | `TestFlow`, or `ScopeRun` directly on a plain container |
| R13 | Parallel background tasks joined by a barrier | An `IReadOnlyList<IBackgroundTask>` constructor parameter produces edges to all of them |
| R14 | Restart cancels background work | Service tokens outlive `InitializeAsync` and are cancelled by the next restart or disposal |
