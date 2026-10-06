# RuntimeFlow 1.0 — design

This document explains why the framework looks the way it does, what the scheduler guarantees, and the
exact text it produces. It is the reference the test suite asserts against; changing a message here
means changing a test.

## 1. One graph

**The container is the graph.** Startup order is not declared anywhere: it is derived from the
VContainer registrations of the scope being initialized. A service is a class implementing
`IAsyncInitializable` registered as `Lifetime.Singleton`; its dependencies are whatever VContainer
injects into it — constructor parameters and `[Inject]` members — resolved the way VContainer resolves
them.

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
failure is attached to its own node. When the run begins, every *required* node that failed to
construct fails at once — all of them are reported, before a single service starts. An *optional* one
degrades in graph order instead, once its own dependencies are done, so a broken optional service in
the middle of a chain does not let the rest of the chain overtake its upstream.

Whatever can be validated without constructing a service is validated first: the options, the
lifetimes, the `[Init]` values of every concretely registered type, and the parent-scope scene
components a service would re-inject (below). Every owned build failure releases already-created graph
services, including eager builder resolutions before an early validation error, an entry-point
failure or a container build that throws before returning its resolver. Recovery reads successful
local catalog cache entries without resolving untouched registrations. During a graph failure,
graph-owned async cleanup and locally tracked synchronous cleanup follow the same dependency order,
then the scope releases remaining container resources — but only when the run was going to own the scope
(`ownsScope: true`). A caller-owned container (`RuntimeFlowHost.From`, `ScopeRun.Create` without
`ownsScope`) keeps its singletons untouched: they are not the framework's to dispose.

`ScopeRun.CreateAsync` awaits that owned rollback before rethrowing the original error. The synchronous
`Create` starts the same ordered rollback and can throw while it is pending; ownership has transferred,
so the caller must retire the failed resolver. Async service cleanup is awaited sequentially, once per
instance, with known dependency edges keeping upstream services alive, then the resolver is disposed.
Owned construction records linked parent graph instances and already-created ancestor cache/tracker
instances, refreshing that evidence after failure and around cleanup callbacks. Child tracker aliases of those borrowed instances
are detached before any rollback cleanup; those instances receive neither async nor synchronous
child cleanup. Inspection never creates an instance; a cached null factory result is not an owned
instance. Thus a rejected factory that returned a live parent object cannot cause rollback or the
final child resolver drain to dispose that parent.
Within an invalid dependency cycle no internal order can satisfy every edge; dependencies outside the
cycle are retained until that group is released. The host reserves construction before running user
code and joins pending rollback before disposing ancestors, independently of the initializer grace.

Services of parent scopes become **external** nodes: they take part in edges and in `Describe()` and
are never scheduled. That is what makes a session graph able to depend on global services without
re-initializing them. Through linked runs (the host passes its runs as `parents`) an external node
carries the state of the parent's node, read again when the child run starts: a degraded global service
stays `Degraded` in the session; one the parent run never initialized — skipped by a halt, cancelled,
failed, not run yet — is not satisfied, and a service of the child depending on it fails at the start of
the run, like a construction failure (`X depends on Y of scope 'global', which is Skipped there: that run
never initialized it, so X cannot start. Run a child scope only once every parent run completed.`). The
host never gets there — it refuses a child scope whose parents have not all completed — so this guards a
standalone `ScopeRun.Create` with parents. A standalone `ScopeRun.Create` on a child resolver without
parents only sees the parent containers, so its externals always count as initialized (and are named
after the instance the parent holds, so a factory registration is known by its concrete type, not by its
contract).

Because discovery goes through the registration's *exposed* types, a class that implements
`IAsyncInitializable` but is registered without exposing it — `Register<Catalog>(Lifetime.Singleton)`,
or `.As<ICatalog>()` and nothing else — is invisible to the graph. Nothing throws: the service is
constructed on demand, `InitializeAsync` is never called, and the run reports success over a half-built
object. This is the one failure mode of the design that is silent by construction, so after the
consumer installer has run the host inspects the composed builder's registrations and warns:

```text
[RuntimeFlow] session: Catalog implements IAsyncInitializable but is registered without exposing it; it
will never be initialized. Use RegisterInitializable<T>() or .As<IAsyncInitializable>().
```

A `[DependsOn(typeof(T))]` whose only candidate is such a registration is an error rather than a
warning: an ordering constraint that cannot be honoured must not be dropped silently — that is exactly
how 0.x produced a production restart loop.

Validation performed at build time:

| Problem | Message |
|---|---|
| Non-singleton | `Foo is registered with Lifetime.Transient in scope 'session'; initializable services must be Lifetime.Singleton (a Scoped/Transient service would be re-created uninitialized in child scopes).` A MonoBehaviour gets the extra hint ` For a MonoBehaviour use RegisterComponent(instance), RegisterComponentOnNewGameObject<T>(Lifetime.Singleton) or RegisterComponentInHierarchy<T>(), which is accepted as Scoped as long as no child scope resolves it.` — upstream `RegisterComponentInHierarchy` is always Scoped, yet in the scope that registers it every resolution returns the one component in the scene, so it is accepted there. |
| A child service injects a parent's Scoped scene component | Checked before the child constructs anything, directly or through plain registrations: `Foo in scope 'session' depends on HudRoot (ctor: HudRoot hud), which scope 'global' registers with RegisterComponentInHierarchy (Lifetime.Scoped). VContainer creates a Scoped registration anew in every scope that resolves it, so 'session' would inject that scene component again with its own dependencies and dispose it with itself. Register it in 'global' as a single instance instead: RegisterComponent(instance), for example RegisterComponent(Object.FindObjectOfType<T>(true)).` Resolved through a parent singleton it is fine (the parent resolves it), and so is anything on the 1.19 fork, which registers hierarchy components as singletons. Resolving it ad hoc from a child (`childScope.Resolve<HudRoot>()`) re-injects it too; the graph cannot see that. |
| Two registrations of the same type | Warning: `Foo is registered as IAsyncInitializable 2 times; each registration will be initialized.` |
| Construction failed: missing registration | `Could not construct Foo in scope 'session': <VContainer message>. Register the missing type in 'session' or a parent scope.` |
| Construction failed: the constructor threw | `Could not construct Foo in scope 'session': its construction threw InvalidOperationException: <message>. See InnerException.` (`TargetInvocationException` is unwrapped; the inner exception is the original.) |
| Equal-arity constructors, none `[Inject]` | Warning: `Foo has 2 constructors with 1 parameter and none is marked [Inject]; VContainer injects the first one reflection returns (Foo(Bar bar)), which is not guaranteed to be the same in every build. Mark the intended constructor with [Inject].` |
| Negative `CancellationGrace` | `RuntimeFlowOptions.CancellationGrace is -2.0s; it must be >= 0 or Timeout.InfiniteTimeSpan (wait forever).` |
| Duplicate or empty phase name | `RuntimeFlowOptions.Phases [boot, ui, boot] lists phase 'boot' twice; phase names must be unique.` |
| `DefaultPhase` not in `Phases` | `RuntimeFlowOptions.DefaultPhase is 'x', but RuntimeFlowOptions.Phases is [platform, content].` |
| Phase used, none declared | `Foo declares phase 'content', but RuntimeFlowOptions.Phases is empty. Declare the ordered phase list in RuntimeFlowOptions.Phases.` |
| Unknown phase | `Foo declares phase 'x', but RuntimeFlowOptions.Phases is [platform, content].` |
| `UserGated` + `TimeoutSeconds` | `Foo is user-gated and declares TimeoutSeconds = 30; user-gated services never time out. Remove one of them.` |
| Negative or non-finite `Weight` | `Foo declares Weight = -1; weight must be a finite number >= 0 (0 removes the service from the progress bar).` |
| Negative or non-finite `TimeoutSeconds` | `Foo declares TimeoutSeconds = -1; it must be a finite number >= 0 (0 means no timeout).` |
| Negative or non-finite `TimeoutMultiplier` | `RuntimeFlowOptions.TimeoutMultiplier is NaN; it must be a finite number >= 0 (0 disables all timeouts).` |

### Edge rules

Edges follow what VContainer injects. `ConstructorEdges` mirrors VContainer's `TypeAnalyzer` exactly:
among `DeclaredOnly | Instance | Public | NonPublic` constructors, the single one marked `[Inject]`
wins wherever it is declared (two or more make VContainer throw, so there are no constructor edges);
otherwise the **first constructor reflection returns** with strictly the most parameters. Using the
same reflection order in the same process is what keeps the edges identical to the constructor
VContainer really calls. A tie is still a portability hazard — reflection order is not guaranteed to be
the same in every build — so it is reported as a warning asking for `[Inject]` rather than broken by
some other rule that could disagree with VContainer (the reason 0.x once shipped a silently wrong
edge). On top of the constructor, `[Inject]` methods, fields and properties are read up the base-type
chain the way VContainer injects them, so a `MonoBehaviour` registered with `RegisterComponent` gets
the edges of its `[Inject] Construct(...)` method — for the registrations VContainer constructs or
injects by reflection only. An instance (`RegisterInstance`) or a factory product
(`Register(resolver => …)`) is never member-injected by VContainer, so it contributes its constructor
parameters and nothing else (an `[Inject]` field there would only invent edges, and false cycles). Both
the runtime type of an instance/factory product and `Registration.ImplementationType` are inspected, which is
how a decorator registered as `Register<I>(resolver => new Decorator(inner))` still contributes its
constructor edges. Reflected providers follow one bound injector: the registered implementation for
ordinary and existing-component providers, the found runtime subtype for hierarchy providers.
Components contribute member injection only. Reading a hierarchy subtype uses its already-created
VContainer cache entry, never a resolution; before first injection preflight knows only the declared
type, and graph assembly checks the actual cached subtype. A reached custom nonsealed hierarchy
registration with no created cache entry is refused when a parent's initialized identity could be
recreated or re-injected from that child; without such an unsafe parent identity it remains allowed.
An exact sealed type is inspectable before creation. Assembly also rejects a parent Scoped scene
identity when a child cache entry proves injection was attempted. Existing-component and hierarchy
providers can mutate the parent before throwing, so even a faulted entry counts without reading its
value. An attempted hierarchy injection whose unknown subtype never produced a cached instance is
also refused when child-cache evidence shows unsafe parent resolution. Providers that create a new
object require a successfully created entry. Constructor
metadata alone does not imply reinjection: an instance or closure factory can safely retain the
initialized parent's object. Late inherited Singleton checks compare the created instance with the
initialized parent's identity: a factory returning the same object is allowed unless the child's
actual tracker also acquired its synchronous disposal. Existing-component injection attempts remain
unsafe even for the same object. Reflected preflight still rejects prospective unsafe resolution
before construction.
That last defense cannot undo user code already run by opaque factories
or eager VContainer build callbacks. Hidden base members omitted by the injector stay
omitted rather than being revived by a second base-type scan.

Each injected type is then resolved the way VContainer resolves it:

| Injected type | Meaning |
|---|---|
| A single type `T` | The registration VContainer picks — the nearest scope that registers `T`, and the **last** registration of `T` there. An edge to that service only; a class that merely happens to implement `T` but is not registered as `T` is not a dependency. |
| `[Key(x)] T` (VContainer 1.19 fork) | The registration of `T` keyed `x` (`.Keyed(x)`), looked up through the keyed `TryGetRegistration(type, out registration, key)`. The attribute is read by name, since upstream 1.15.3 has neither it nor keyed lookups. |
| `IEnumerable<T>`, `IReadOnlyList<T>` | Edges to every registration of `T` in the scope and its parents, excluding the node itself — the barrier idiom for "after all background tasks". Other collection shapes (`List<T>`, `T[]`, `IReadOnlyCollection<T>`…) are not resolvable by VContainer: no edge, the service fails construction. |
| A registration that is not itself a service | Walked through: `Profile(IProfileApi)` → `ProfileApi(Auth)` → `Auth` gives `Profile` an edge to `Auth`, with origin `ctor: IProfileApi api via ProfileApi` (deeper chains read `via Repository > ProfileApi`). Dependencies follow the scope VContainer actually uses to construct the registration. For an inherited singleton, a child registration of the same implementation type can keep construction in the child even when it exposes a different interface. Each node reached is reported once, with the first chain found, and walks are memoised per registration and effective construction scope. Factory (`Register(resolver => …)`), instance and `RegisterFromResolve` registrations end the walk — what they resolve is invisible to reflection; add `[DependsOn]` when it matters (see below). |
| A lookup that throws | For example `IRepo<int>` against an open generic `Repo<T> where T : class` in a parent scope: no edge (Debug log). Resolving the service throws the same way, so the construction error — not a raw exception out of `ScopeRun.Create` — explains it. |
| `ContainerLocal<T>` | Follows VContainer's local resolution. A wrapped collection excludes parent singleton registrations; parent scoped and transient elements still resolve in the child. An explicitly registered parent collection is not imported. |
| A parameter supplied by the registration's `WithParameter(...)` | Not resolved, so not an edge. |
| `Func<…>`, `Lazy<T>`, `ILazy<T>`, `LazyDependency<T>` | **Not** an edge, directly or on a transitive walk. The documented way to break a cycle; listed in `Describe()` as a `lazy:` row. |
| `IObjectResolver`, `IScopedObjectResolver`, `IContainerBuilder`, `RuntimeFlowHost`, `ScopeRun`, `RuntimeFlowOptions`, `InitContext`, `ILogger`, `object`, `string`, primitives, enums, `decimal` | Ignored. |
| Anything resolving to a parent service instance | Edge to the external node. If VContainer would recreate that initializable registration in the child, the graph rejects it rather than counting the parent's initialized instance as its replacement. |

Every edge remembers where it came from, and the origin is printed verbatim in cycle errors and in
`Describe()`: `ctor: ICdnMirrorSelector mirrors`, `[Inject] Construct(Auth auth)`, `[Inject] Auth auth`,
`ctor: IProfileApi api via ProfileApi`, `DependsOn`, `phase barrier`.
Distinct injected overloads retain their own parameter identities, including keyed dependencies.

**Where reflection stops.** The walk derives edges from what VContainer constructs; it cannot see what
user code resolves. A migrating project must add `[DependsOn(typeof(T))]` wherever one of these hides a
dependency:

- a factory: `Register<IFoo>(resolver => new Foo(resolver.Resolve<Auth>()), Lifetime.Singleton)` — the
  product's constructor parameters are inspected, but only against the types they declare, not against
  what the lambda passes;
- an instance: `RegisterInstance(foo)`;
- the 1.19 fork's `RegisterFromResolve<IFoo>(resolver => resolver.Resolve<Config>().Foo)`;
- a delegate parameter: `WithParameter<IFoo>(resolver => resolver.Resolve<Auth>().Foo)` — a parameter
  supplied by `WithParameter` is no edge at all;
- anything resolved from an injected `IObjectResolver` inside a constructor or method.

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
affinity checks, no scheduler abstraction. Starting a run without a context is refused with
`InvalidOperationException` (`The 'session' run must be started on a thread with a SynchronizationContext
(the Unity main thread); SynchronizationContext.Current is null, so its continuations would run
concurrently on the thread pool.`): without one, every continuation — `Task.Yield`, `Task.Delay`, the
watch timer — resumes on the thread pool and the lock-free bookkeeping would race.

"Single-threaded" describes where the framework's own code runs, not where its inputs come from. A
caller's `CancellationToken` may be cancelled from any thread — a network callback, a `Task.Run`
worker, Unity's background loader; the registration reacts on that thread, and the framework marshals
the reaction back through the captured `SynchronizationContext` before it touches any run state. The
same holds for `InitContext.Halt` and `InitContext.ReportProgress`: called from another thread, they are
posted to the run's context (a progress report that lands after the service finished is ignored).
`InitContext.DegradedServices` hands out an immutable snapshot, replaced — never mutated — on every
degradation, so a service may enumerate it across an `await` or from a worker thread; each read of the
property returns the latest snapshot. Every
observer callback, log line and status mutation therefore still happens on the run's thread, and a
service that cancels from a worker thread sees no difference from one that cancels on the main thread.

One periodic loop per run (`StallWatch`, at most 1 Hz, faster when a configured threshold demands it)
drives both timeout enforcement and the stall warning. Each service gets a `CancellationToken` linked
to the run's; it stays valid after `InitializeAsync` returns, so background work started during
initialization is cancelled by the next restart. A caller cancellation that lands after the run already
finished (posted from another thread just before completion) is ignored: a finished run keeps its
outcome and its service tokens.

### Failure semantics

| Event | Behaviour |
|---|---|
| Required service throws | Node `Failed`; the run token is cancelled; in-flight services are awaited up to `CancellationGrace` (5 s) so their failures are collected too; then one `RuntimeFlowException`. The wait covers the observers' bookkeeping, not just the raw service tasks, so the exception never misses a failure that already happened. |
| Optional service throws | Node `Degraded`, warning logged, dependents start anyway and see the name in `InitContext.DegradedServices`. The name also travels down: a parent-scope node that degraded stays `Degraded` as an external node of every child scope, so the session's services and its `Describe()` see the global degradation. `StartupResult.Degraded` stays per scope for `ScopeRun.RunAsync`; `RuntimeFlowHost.StartAsync`/`RestartAsync` return the union, global names first. |
| Required service fails behind a degraded one | Same failure handling, plus an annotation: every direct dependency of the failing service that is `Degraded` — own scope or parent — is named in the message as ` (after upstream X degraded)`. |
| Timeout | Only from `[Init(TimeoutSeconds = n)]`, scaled by `TimeoutMultiplier` (`0` disables all timeouts). There is no default deadline — 0.x had a self-learning 5-second watchdog that tore down bundle downloads and produced restart storms. User-gated services are exempt. Every service expired in one watch tick is reported with its own timeout: all of them are marked timed out first, the run is marked failing (when one is required) before observers hear of it, dependents are released, and their tokens are cancelled last — so a service that catches its cancellation and returns (inline, inside `Cancel()`) cannot complete a node that already timed out, and a second required expiry is not misreported as cancelled. A watch tick that throws (a framework bug; a throwing logger cannot, see below) is logged as `[RuntimeFlow] session: the watch tick threw InvalidOperationException; timeouts and stall warnings keep running.` and the loop continues. |
| Stall | `StallWarningAfter` (10 s) of no service start, completion or `ReportProgress` call produces a warning naming what is running, what is awaiting the player and what is blocked. It never cancels anything. |
| `Halt` | First call wins; run state `Halted`; unstarted nodes skipped, in-flight ones cancelled; the result is `StartupOutcome.Halted` and nothing throws. An empty reason throws `ArgumentException` at the call site. A halt in the **global** scope ends the startup there: no session is built (see §3). |
| Failure while stopping | A service that fails while a halt or a cancellation settles does not change the outcome, but it is never dropped silently: `[RuntimeFlow] session: Foo threw InvalidOperationException (cleanup exploded) while the run was halting; the outcome stays unchanged.` (`… while the run was being cancelled; …`). |
| Throwing cancellation callback | `CancellationTokenSource.Cancel()` runs user callbacks (`ct.Register(() => request.Abort())`) and rethrows their exceptions. Every cancellation the framework performs goes through one helper that logs `[RuntimeFlow] session: a cancellation callback threw InvalidOperationException (abort failed) while cancelling the run; the other callbacks ran, continuing.` and carries on: a halt or failure still settles, a timed-out service's dependents are still released, and disposal still reaches every `DisposeAsync` and the scope. |
| Throwing logger | Every log call is contained: a logger that throws is never able to wedge a run (the run's task is completed in a `finally`, whatever logging or observers do). The first failure of each logger is reported once through `Debug.LogWarning`. |
| Observers during a failure | The run's own state changes first — `Failed` for a required failure or timeout — and only then are observers notified, so an observer that calls `CancelAsync()` from `OnServiceFailed` cannot turn a failure into a cancellation and lose the `RuntimeFlowException`. |
| Caller cancellation | The run's task completes cancelled; `OnRunCancelled` fires and an Info line reports it. |
| Grace | A stopping run waits for the services still *in flight* — not for ones it already gave up on (timed out), which would stall every later stop for the whole grace. `ScopeRun.DisposeAsync` then waits for those too, again bounded by the grace, before it disposes them: teardown never disposes a service while it is still inside its `InitializeAsync`, within the grace. `Timeout.InfiniteTimeSpan` (or a value beyond what a timer can express) waits forever; zero does not wait; a negative value is rejected. `ScopeRun.CancelAsync`/`DisposeAsync` wait for a run that is already stopping (halt, failure) too. **With an infinite grace, a service that ignores its token makes `CancelAsync`, `DisposeAsync`, `RuntimeFlowHost.DisposeAsync` and every restart wait forever** — and a service that awaits its own `RestartAsync` deadlocks permanently instead of for one grace. |
| Grace exceeded | Services still running after `CancellationGrace` are marked `Cancelled` and abandoned with an error line; teardown continues, and their `InitContext` throws `ObjectDisposedException` afterwards. A timed-out service still running when its run is disposed is reported the same way (`… still running 5.0s after cancellation: Catalog (timed out after 30.0s). …`) and disposed anyway. |

A delayed watch tick settles already-completed initializer tasks rather than timing out successful
work whose observation was queued behind it. Outcome publication is deferred through the whole expiry
batch, including observer reentry, so timeout tokens are cancelled before a terminal run event. Raw
unexpected cancellation that preceded the batch remains a failure. `ReportProgress(NaN)` is ignored
without resetting the stall clock; finite weights are normalized before summation to avoid overflow.

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

[RuntimeFlow] session: cancelled after 1.2s (3/12 services completed).

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

`RuntimeFlowHost` composes each scope as: register itself → register the host's entry-point exception
collector → the consumer installer → inspect what the installer left behind → ensure the entry-point
dispatcher is registered (without which `IInitializable` never runs in a plain `ContainerBuilder`).
VContainer runs `IInitializable` inside `Build()`, before the asynchronous graph, in registration order;
without a handler upstream VContainer only logs an exception with `Debug.LogException` and carries on
building — hence the collector, which turns that throw into a failed run:

```text
Initialization of scope 'session' failed: SaveMigration threw IOException while VContainer was running
the IInitializable entry points of the scope: disk full. See InnerException.
```

The collector is registered in **every** scope the host composes and **before** the installer. The
order is what makes it work on the VContainer 1.19 fork as well: its `EnsureDispatcherRegistered` (called
by `UseEntryPoints`, `RegisterEntryPoint`, a `LifetimeScope` build and the host itself) registers
`Debug.LogException` as the handler of any scope that has none yet — registered after the installer, the
collector would find that default already in place and mistake it for a consumer's handler, and a
session relying on the parent's handler would get the default instead. VContainer resolves the handler
through the parent chain and takes the last registration of a scope, and it allows one handler per
scope (1.15.3 refuses a second singleton one: `Conflict implementation type`). So when the installer
registers its own handler, the collector's entry is replaced by an inert registration and the
consumer's handler **supersedes** it: an `IInitializable` that throws is routed to the consumer's handler
instead of failing the run. That is a legitimate choice (a game may want to swallow a non-fatal
migration error), so it is allowed rather than blocked, but it changes a documented semantic, hence one
informational line: `a custom EntryPointExceptionHandler is registered; IInitializable exceptions are
delivered to it instead of failing the run.` A consumer handler of a parent scope — one registered in
the global installer, or in a container passed to `RuntimeFlowHost.From` — stays in charge of the session:
the session's collector forwards every exception to it. A parent handler that is exactly
`UnityEngine.Debug.LogException` is the fork's default, not a consumer decision, and is ignored.

The collector collects only while the host is building its scope. VContainer routes *every* entry-point
exception of the scope to it for the rest of the scope's life — `IStartable`, `ITickable`, the post and
fixed/late variants — and those are logged at Error (`[RuntimeFlow] Foo threw InvalidOperationException
in a VContainer entry point after its scope was built: <message>`), never swallowed and never carried
into the next build. The name of the failing entry point is the outermost frame of the exception's stack
that belongs to an entry-point type, so a helper deeper down (`FileStream`, say) is not blamed.

The inspection pass is also what finds a service registered without exposing `IAsyncInitializable`
(above); it inspects registration builders through the public `Build()`, registers nothing and never
aborts composition — a registration that cannot be inspected this early (a component builder needing a
`LifetimeScope`) is skipped.

`IStartable` and `ITickable` are frame-driven and unordered with respect to asynchronous
initialization; that is a documented contract, not an oversight.

## 3. Restart and disposal

State flow of `RuntimeFlowHost.RestartAsync`:

```text
request ─▶ quitting?                 ─ yes ▶ refuse (warning + InvalidOperationException)
        ─▶ started?                  ─ no  ▶ InvalidOperationException ("start the host first")
        ─▶ token owned by the run it tears down? ─ yes ▶ warning; the token is ignored
        ─▶ token already cancelled?  ─ yes ▶ cancelled task; nothing is torn down or budgeted
        ─▶ global scope halted?      ─ yes ▶ InvalidOperationException (the global scope is never rebuilt)
        ─▶ restart pending?          ─ yes ▶ coalesce: join the chain already in flight
        ─▶ budget exceeded?          ─ yes ▶ RuntimeFlowException with the list of reasons (not recorded)
        ─▶ no session yet (a startup or restart is building one)?
                                     ─ yes ▶ that chain builds its session as this restart
        ─▶ freeze the session run (nothing new starts) ─▶ after a yield: cancel it
        ─▶ dispose its child runs (reverse creation order) ─▶ dispose the session run
        ─▶ Global.CreateScope(composed session installer) ─▶ ScopeRun.Create
        ─▶ RunAsync(isRestart: true, generation + 1)
```

**No request ever waits for a session run to finish on its own.** Whenever a session exists, an
accepted restart cancels it; when none exists yet — a request during the global phase of the startup,
or from an entry point or constructor while a session is being built — the chain in flight builds its
session *as* the restart (budgeted once, `Generation` and `RestartCount` incremented,
`InitContext.IsRestart` set) instead of building one generation only to throw it away. A startup whose
global phase fails drops such a request (and its budget entry) and its awaiters observe the startup
failure, never a misleading restart. This is what makes the common live-ops chain work: an addressables
service finds the catalog outdated, downloads and requests a restart; in the next generation a configs
service finds the configs outdated and requests another; generation 2 completes — whether the first
service lives in the session or in global, and whether the services return, park on their token or
throw a cancellation after asking.

**Freeze now, cancel later.** Accepting a restart for a running session freezes its scheduler (and its
children's) synchronously: no further service of the doomed generation starts, so a dependent of the
requesting service never runs on stale data. The tokens are cancelled only after a `Task.Yield`, so a
service can request a restart from inside its own `InitializeAsync` — as `_ = host.RestartAsync("reason")`,
never awaited — without re-entrancy. A service that throws `OperationCanceledException` from a frozen
run is recorded as cancelled, not failed. Awaiting the request there deadlocks by construction until
the grace expires: the returned task completes only once the current run is torn down, and teardown
waits up to `CancellationGrace` for the in-flight services, one of which is the awaiting service itself.
`Generation` is stamped as started *before* the first `InitializeAsync` of the new run, so a synchronous
restart request from within it opens a new generation instead of joining the one it is running in. A
frozen run never finishes on its own — not even when every one of its services finished — so a doomed
generation (or a session doomed before it started, which the host freezes before its first service)
never reports `Completed` or fires `OnRunCompleted`; the cancellation that replaces it ends it
`Cancelled`. For the same reason no run ever reports `Completed` while one of its services is
`Cancelled`.
Both `StartAsync` and `RestartAsync` follow the chain and return the result of the **last** run, which
is why an awaiter that started before a restart still observes the final outcome.

**Tokens.** A `CancellationToken` passed to `RestartAsync` cancels the new session run. A token of the
run the restart tears down — the requesting service's own token, a session run token, a token of a
session child run — would be cancelled by that very teardown and cancel the new generation at birth,
so it is ignored with a warning:

```text
[RuntimeFlow] restart 'configs-updated': the CancellationToken passed to RestartAsync belongs to the run
this restart tears down; it is ignored, otherwise the new generation would be cancelled at birth. Pass
CancellationToken.None or a token that outlives the restart.
```

Tokens *derived* from it (a linked source) cannot be recognised; do not pass them. A token that is
already cancelled when `RestartAsync` is called yields a cancelled task and tears nothing down
(`restart 'x' not started: its CancellationToken is already cancelled.`).

**A halt in the global scope ends the startup.** Its skipped services never initialized, and a session
built on top of them would run on half an application, so no session is built: `StartAsync` returns the
global run's `Halted` result (`Scope == "global"`, `HaltReason`, `HaltedBy`, the global degradations),
`State` and `GetStatus()` report `Halted` with the reason and the halting service, `Session` throws
`InvalidOperationException` (`No session scope exists: the global scope was halted by X ('reason'), so no
session is built. The global scope is never rebuilt; dispose this host and create a new one.`), a restart
requested during the global phase is dropped (unbudgeted; its awaiters get the halt), and every later
`RestartAsync` is refused with `InvalidOperationException` — global services are never rebuilt, so there
is nothing a restart could build a session on. Starting over means a new host.

**Global services are never rebuilt.** A global service may request a restart, but it must not wait on
its own token afterwards: nothing cancels it, and the startup would never finish. A request while the
global scope is initializing, or with a token of the global run, logs:

```text
[RuntimeFlow] restart 'addressables-updated' requested while the global scope is initializing: global
services are not rebuilt by RestartAsync, only the session is. Do not wait on your token after
requesting a restart from the global scope: nothing cancels it, and the startup would never finish.
```

**Failed builds stay recoverable.** When the session cannot even be built during a restart (an installer
throws, an `IInitializable` entry point throws, the graph is invalid), nothing half-built stays
published: the scope is disposed, `State` is `Failed`, `GetStatus().Error` carries the exception,
`Session` throws `InvalidOperationException` (`The session scope failed to build (…); call
RuntimeFlowHost.RestartAsync to build it again.`, the exception as `InnerException`), and the next
`RestartAsync` builds a fresh session — a "Retry" button recovers. The same holds for a first session
that fails to build: `StartAsync` keeps returning that failure, `RestartAsync` rebuilds. A failure of the
*global* phase instead resets the host, so a later `StartAsync` retries from scratch — except for a
global supplied through `RuntimeFlowHost.From`, whose half-initialized singletons cannot be initialized
twice (`StartAsync` then fails with `InvalidOperationException`).

`State` is `Running` for the whole of a startup or restart, including the teardown of the generation it
replaces (never `Disposed` mid-restart).

**Budget.** `MaxRestartsPerWindow` counts *accepted* restarts within the sliding `RestartWindow`; a
refused request is not recorded, so a steady retry is refused only while the window is really full. A
request folded into a chain that then fails before honouring it — the global phase fails (even while its
teardown is still running), or the session build fails after a constructor asked for it — gives its
budget entry back.
With `RestartWindow` zero or less the window never slides and the limit counts every restart over the
host's lifetime (`Restart budget exceeded: 2 restarts over the host's lifetime (limit 1). Reasons: …`).

**Disposal and quitting during a chain.** Every await of a startup or restart chain is followed by a
check: once the host is disposed or the application is quitting, the chain stops without building
anything (`ObjectDisposedException` or `OperationCanceledException` for its awaiters). `DisposeAsync`
freezes every run up front — every child run, the session and the global run — so no service of any
scope starts once it was called, not even in a child run that is disposed only after another one; it
cancels them all after a `Task.Yield` (a service may call `DisposeAsync` from its own
`InitializeAsync` without having its token cancelled inside that call), then waits for the chain to
unwind — bounded by twice `CancellationGrace` plus a second, unbounded when the grace is infinite — so no
session is ever built on a disposed global. Afterwards `Global` and `Session` throw
`ObjectDisposedException`. A quit cancels the child runs too — a child frozen by an accepted restart
would otherwise never be cancelled when the quit stops that restart before disposing it — and refuses
new child scopes. A quit that tears the session down without a replacement leaves `State` `Cancelled`
and a `Session` getter that says the application is quitting. Every iteration over the child runs works
on a snapshot: a cancellation callback may dispose another child, which removes it from the list.
Construction checks stop state after user constructors as well. Its reservation lasts until a run is
published or failed/aborted cleanup finishes, not through initialization. Restart joins pending
session constructions; host disposal joins all constructions before freeing their dependencies.
These cleanup waits are not bounded by `CancellationGrace`, which bounds initializer drain only.

Disposal order, top to bottom:

1. Freeze the run (nothing new starts), yield, cancel it; wait out `CancellationGrace` (also when the
   run is already stopping); then wait, again up to the grace, for timed-out services still inside
   their `InitializeAsync`.
2. Clean constructed instances **dependents before their dependencies**, including failed, cancelled
   and unstarted services and dependency paths through phase barriers. Independent services keep
   reverse completion order, then reverse registration order for services that never completed.
   An owned scope combines graph-owned async services with already-created plain synchronous
   registrations. At each physical instance, await its framework-owned async cleanup, then perform
   its VContainer-owned synchronous cleanup. Remove matching tracker entries before calling it so
   final resolver disposal cannot repeat it, even if it throws. Ownership comes from the actual local
   tracker; registered instances and untracked transients do not acquire synchronous ownership.
   Multiple registrations returning one instance share cleanup; distinct equal instances stay
   distinct. Cleanup exceptions are logged (`disposing Foo threw InvalidOperationException;
   continuing teardown.` for async cleanup, or `disposing Foo synchronously threw
   InvalidOperationException; continuing teardown.` for synchronous cleanup), and other instances
   still run. The global services supplied through
   `RuntimeFlowHost.From` are left alone — that container is its owner's.
3. Dispose the service tokens and the run's cancellation sources; mark the generation abandoned, after
   which its `InitContext` throws `ObjectDisposedException`.
4. Drain remaining locally tracked synchronous resources one entry at a time in VContainer's scope
   order, preserving the same physical cleanup ledger. During failed construction, refresh ancestor
   ownership before each callback so late aliases cannot release a parent. Once the trackers are
   empty, `scope.Dispose()` clears the resolver's remaining state. Callback errors are logged and
   other owned resources still run.

The owned root includes its Singleton tracker and root Scoped tracker; an owned child touches only
its own caches and tracker. Inspection never resolves services or creates cache entries. A service
owned asynchronously as a graph service and synchronously by the local tracker receives both calls
and must tolerate them. Plain registrations retain synchronous ownership only. Concurrent and
re-entrant `DisposeAsync` calls (one from a disposal callback, say) share one teardown and all complete
when it does. Every step runs even when an earlier cleanup failed. A scope whose
`Dispose()` throws is disposed again until it disposes cleanly (VContainer resumes with the next
disposable), up to 64 attempts. Distinct disposables may throw the same exception object, so exception
identity does not stop retries. Identical failures are logged once plus a count; exhausting the bound
logs that remaining disposables could not be released.

A caller-owned standalone scope retains synchronous ownership. Read-only validation before
initialization rejects a local synchronous dependent that would need cleanup before a framework-owned
async dependency. Use an owned scope, or expose the dependent as an `IAsyncInitializable` graph service
and move its cleanup from `IDisposable` to `IAsyncDisposable`. An opaque
factory or later ad hoc resolution can introduce such an edge after validation; the disposal recheck
then faults with `InitGraphException` before releasing services and still releases run tokens. The
caller completes the required cleanup order. This ownership violation differs from a user cleanup
exception, which remains contained. `RuntimeFlowHost.From` leaves its borrowed global services intact
and bypasses that validation.

On the host, child runs go before the session and the session before the global run; a global container
supplied through `RuntimeFlowHost.From` is never disposed. Child scopes (`InitializeScopeAsync`) follow
their lineage: one below the session is disposed with it, by every restart; one below global is
independent of the session, survives restarts and is disposed with the host. A child run disposed by
its user remains tracked until teardown finishes, so a restart or host disposal awaits it before
disposing its parent. Direct child-parent disposal freezes its managed descendants synchronously,
then cancels and joins their runs and pending construction cleanup before releasing parent resources;
unmanaged intermediate scopes do not break the ancestry walk. Afterwards the host releases the run and retains only weak scope identity to
reject another initialization of the same resolver or a child below that disposed scope.
`InitializeScopeAsync` refuses (with `InvalidOperationException`) to
start a child scope while a startup or restart is in flight, while any parent run has not `Completed` —
a child created from a service of its parent scope, while that scope is still initializing, would
otherwise run on uninitialized parents — and once the application is quitting. Because constructing the
child's services runs user code, the checks are repeated after `ScopeRun.Create`: a child whose
constructor requested a restart (dooming the session it would run on) or disposed the host is disposed
and refused (`Scope 'lobby' was refused: a restart was requested while its services were being
constructed. …`) before anything of it starts. It rejects (with `InitGraphException`) the host's own
scopes, a scope already initialized, and a scope below a session a restart has replaced.
A grandchild scope sees every ancestor run: `[DependsOn]` on a service of its parent child scope
works, and degradation travels down the whole lineage.

## 4. Diagnostics

- **`Describe()`** renders the graph as an aligned table: index, phase, name, flags
  (`required|optional`, `user-gated`, `timeout Ns`, `weight N`), one indented row per edge with its
  origin (and `[global, initialized]`, `[global, degraded]` — or, for a parent service its run has not
  initialized, its state: `[global, pending]`, `[global, skipped]` — for external targets), the `lazy:`
  parameters, and a final line listing the services inherited from parent scopes. It answers "why did this run in that order" without
  a debugger, and it is snapshot-tested.
- **`GetStatus()`** returns immutable `RuntimeFlowStatus`/`ServiceStatus` snapshots: state, phase,
  elapsed (ticking while running), reported sub-progress, weight, dependencies, `WaitingOn`, error,
  weighted `Percent`, `RestartCount`, `HaltReason`, `HaltedBy`. Safe to poll every frame; the dashboard
  polls at 4 Hz. Polling allocates little: a node's dependency names are computed once (edges never
  change after the build), an empty `WaitingOn` is a shared empty list, and the snapshot of a service
  in a terminal state is built once and reused.
  A graph where every service has `weight 0` reports `0%` while running and `100%` once all services
  are satisfied.
- **`IRuntimeFlowObserver`** is the push side, with default interface members so implementations stay
  small. Every run ends in exactly one of `OnRunCompleted`, `OnRunHalted`, `OnRunFailed` or
  `OnRunCancelled`. Observers are wrapped: one that throws is logged and skipped. The observer set is
  snapshotted when the run starts (`RunAsync`, not `ScopeRun.Create`): an observer added between the two
  receives the whole run, one added mid-run does not affect the run in flight. Reentrant notifications
  are queued until the current observer fan finishes; a terminal run event follows all pending service
  notifications.
- **`FlowRegistry`** is the only static in the package holding runtime objects, it is
  diagnostics-only, it holds **weak** references, prunes them on read, and is cleared on
  `RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)`. Runtime logic never
  reads it; it exists so the editor dashboard can find live hosts without the runtime depending on an
  ambient singleton.
- **JSON dump.** The dashboard serialises the current snapshot (scopes, services, states, timings,
  errors) with escaping, so a bug report can carry the whole run as text. Per-run/node identities keep
  selections and actual dependency links distinct across matching scope/service names. Host build
  errors are flattened and retained even without a session service row; saved snapshots normalize
  missing identity fields when loaded.
- **Dashboard selection and history.** The selected host is held weakly by identity, so removing an
  earlier registry entry cannot redirect a restart to another host. Last run prefers the stored final
  snapshot; it shows an explicitly labelled live preview only when no stored snapshot exists.

## 5. Testing philosophy

- **EditMode is authoritative.** The editor's synchronization context behaves like the player's, so the
  entire scheduler, graph, failure and restart surface is testable without entering play mode. PlayMode
  covers only what needs frames: `IStartable` on the first tick, continuations on the main thread,
  restart from a coroutine, timeouts under real frame pacing, disposal on exiting play mode.
- **Gates, not delays.** Tests drive services with `TaskCompletionSource` gates (`await service.Started`,
  then `service.Release()`), so ordering assertions are deterministic. Real time appears only in
  timeout and stall tests, with 50–200 ms thresholds.
- **Awaiting assertions.** NUnit's `Assert.ThrowsAsync` blocks the Unity main thread and deadlocks any
  posted continuation; the suite uses awaiting equivalents instead. Unity's runner also reports an
  async test whose task ends Canceled (an escaping `OperationCanceledException`) as *passed*: it
  rethrows only faulted tasks. Both test assemblies therefore declare the assembly-level
  FailOnEscapedCancellation attribute (Tests/Shared), which reroutes every `Task`-returning test
  through a wrapper that turns a cancelled task into a failure; an expected cancellation is asserted
  explicitly with the awaiting assertions.
- **Overrides filter registrations.** `TestFlow.Override<T>` cannot rely on "register last, wins":
  VContainer resolves a collection by merging every registration, so the real service would still be
  discovered through `IReadOnlyList<IAsyncInitializable>` and would still be constructed and
  initialized. `OverridingContainerBuilder` therefore wraps the builder handed to the production
  installer, buffers the `RegistrationBuilder` objects, inspects each through the public `Build()` to
  read its `InterfaceTypes` and `ImplementationType`, drops the ones exposing the overridden type,
  forwards the rest untouched (lifetimes and parameters survive) and finally registers the replacement.
  No reflection, no internals. An override matching nothing fails the test by name.
  Builder inspections are repeated after installer changes so later interface exposure is not hidden
  by an earlier `Exists` query. `TestFlow` starts ordered cleanup immediately on failure and waits only
  until the original startup deadline; cleanup continues afterwards and explicit disposal joins it.
- **Exact messages.** Tests assert on the strings in this document, because a diagnostic that is not
  pinned degrades silently.
- **Chaos, fast by default.** `RestartChaosTests` generates random global/session/child graphs whose
  services follow a random script per generation (complete, throw, bail out, ignore the token, park,
  halt — global services included — request restarts with every kind of token) while the "game"
  restarts, cancels, quits, disposes and opens child scopes at random moments, and checks the lifecycle
  invariants after every case (nothing starts after `DisposeAsync` was called, in any scope; every task
  settles; every constructed service is disposed exactly once; generations are monotonic; …). A failing
  case names its seed; `Replay(seed)` reproduces it. Every EditMode run executes seeds 0–74 (three
  batches of 25) plus the fixed sfs-client restart chains; the full sweep is the `[Category("Chaos")]`
  `ChaosSweep`, enabled by `RUNTIMEFLOW_CHAOS_BATCHES=first,count` (batch *n* is seeds 25*n*…25*n*+24;
  `0,30` sweeps seeds 0–749, about a second per seed). Without the variable `ChaosSweep(-1)` passes
  trivially. An explicitly supplied range must have a nonnegative first batch, a positive count and
  seeds within the integer range; an invalid value fails instead of selecting that sentinel. The
  batch runner also rejects missing results and runs that execute no tests. Every product bug the chaos
  found is reduced to a deterministic test in `ChaosFindingsTests`.
- **Two VContainers.** `RUNTIMEFLOW_VCONTAINER=fork scripts/run_unity_editmode_tests.sh [playmode]` runs
  the suites against the Bezarius 1.19.0 fork sfs-client ships, by temporarily pointing the test
  project's manifest at it (the lock entry is dropped; both files are restored on every exit path).
  `RUNTIMEFLOW_VCONTAINER_FORK` overrides the package URL, for example with a `file:` path to a local
  copy when offline. Tests that depend on fork-only API (keyed registrations) find it by reflection and
  are skipped on upstream.
- **`LifecycleFake` needs a JIT.** It builds its fakes with `DispatchProxy`, which emits IL at runtime,
  so it runs in the Editor and in Mono players and throws under IL2CPP. Nothing in the runtime package
  depends on it and it never reaches a player build (`RuntimeFlow.Testing` is constrained to
  `UNITY_INCLUDE_TESTS`); an IL2CPP test writes a hand-rolled stub instead.

### Layout and namespaces

`scripts/check_package_namespaces.sh` enforces "namespace follows folder" **for the package only**, and
the mapping is exactly four cases: `Runtime/*.cs` → `RuntimeFlow`, `Runtime/Internal/*.cs` (flat) →
`RuntimeFlow.Internal`, `Runtime/Testing/*.cs` (flat) → `RuntimeFlow.Testing`, and `Editor/**` (any
depth) → `RuntimeFlow.Editor`. `Runtime/Properties/` and `Runtime/Plugins/` are exempt, and no other
`Runtime/` subfolder is allowed.

The rule stops at the package boundary. The Unity test project's assemblies are organised by subject,
not by namespace: every file of `RuntimeFlow.Tests` lives in some `Assets/Tests/EditMode/…/<Area>/`
folder and declares `RuntimeFlow.Tests.<Area>`, which is the asmdef's root namespace plus its folder;
`RuntimeFlow.Demo` is flat — `Assets/Demo/Global/` and `Assets/Demo/Session/` both declare
`RuntimeFlow.Demo`, because they are one assembly with one root namespace and the folders only group
the two scopes for a reader. The guard never looks at them.

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
