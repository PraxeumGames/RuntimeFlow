# RuntimeFlow

Asynchronous initialization graph for Unity, built on stock VContainer. A service implements
`IAsyncInitializable`, is registered with VContainer in whatever way you like, and the framework
derives the startup order from the container itself: constructor parameters are edges, the graph is
validated before anything runs, and services start as soon as their own dependencies are done.

Repository-level layout and development commands:
[`README.md`](https://github.com/PraxeumGames/RuntimeFlow/blob/main/README.md).
Design rationale, algorithms and the complete message catalogue:
[`docs/DESIGN.md`](https://github.com/PraxeumGames/RuntimeFlow/blob/main/docs/DESIGN.md).

## Install

`Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.praxeum.runtimeflow": "https://github.com/PraxeumGames/RuntimeFlow.git?path=com.praxeum.runtimeflow#1.0.0",
    "jp.hadashikick.vcontainer": "https://github.com/hadashiA/VContainer.git?path=VContainer/Assets/VContainer#f2afd2ac175a1e04ac59a8f69794df827b53b732"
  },
  "testables": [
    "com.praxeum.runtimeflow"
  ]
}
```

Both entries are needed. RuntimeFlow lists `jp.hadashikick.vcontainer` in its own `package.json`, but
UPM never resolves a git-URL dependency declared inside a package — only the project manifest fetches
git packages — so the declaration documents the requirement and is satisfied only when your manifest
names the same package. Skip the second line and the project stops compiling on unresolved
`VContainer` references. The pin is upstream `hadashiA/VContainer`, tag 1.15.3, by commit SHA; if your
project already ships VContainer (a fork, a registry copy, an embedded one), keep yours and omit the
line, as long as its **API** is a superset of 1.15.3's. That says nothing about behaviour: forks change
defaults. The suite runs against upstream 1.15.3 and against the Bezarius 1.19.0 fork
(`RUNTIMEFLOW_VCONTAINER=fork scripts/run_unity_editmode_tests.sh`), whose differences RuntimeFlow
accounts for — its `EnsureDispatcherRegistered` registers `Debug.LogException` as the entry-point
exception handler of every scope that has none, `RegisterComponentInHierarchy` registers a singleton
instead of a Scoped registration, and `[Key]` selects keyed registrations. With any other fork, run the
suite against it before relying on it.

If your project already contains `Microsoft.Extensions.Logging.Abstractions.dll` (NuGetForUnity, for
example), add the scripting define `RUNTIMEFLOW_EXTERNAL_MEL` before installing — see
[Logging](#logging) — or Unity reports two precompiled assemblies with the same name.

The `testables` entry defines `UNITY_INCLUDE_TESTS` for the package, which is what compiles the
`RuntimeFlow.Testing` assembly; omit it if you do not want the harness.

Requires Unity 2022.3+ (validated on 2022.3.62f2). 2022.2 is the floor for the UI Toolkit API the
dashboard uses.

## Quick start

```csharp
public sealed class RemoteConfig : IAsyncInitializable
{
    public string CatalogUrl { get; private set; } = "builtin://catalog";
    public async Task InitializeAsync(InitContext ctx, CancellationToken ct)
        => CatalogUrl = await Backend.FetchCatalogUrlAsync(ct);
}

[Init(Phase = "content", Weight = 3, TimeoutSeconds = 30)]
public sealed class Catalog : IAsyncInitializable
{
    private readonly RemoteConfig _config;
    public Catalog(RemoteConfig config) => _config = config;   // edge: after RemoteConfig
    public Task InitializeAsync(InitContext ctx, CancellationToken ct)
        => Backend.LoadCatalogAsync(_config.CatalogUrl, ct);
}

var host = new RuntimeFlowHost(
    global: b => b.RegisterInitializable<RemoteConfig>(),
    session: b => b.RegisterInitializable<Catalog>(),
    new RuntimeFlowOptions { Phases = new[] { "platform", "content" } });

var result = await host.StartAsync();
```

## Concepts

**Services.** A service is any VContainer registration that **exposes** `IAsyncInitializable` and is
`Lifetime.Singleton`. Discovery is scope-local: each scope initializes only its own registrations;
services inherited from parent scopes are treated as already initialized externals (one that degraded
there stays degraded here). A non-singleton registration is a graph error, because a scoped or
transient service would be re-created uninitialized in a child scope. The one exception is
`RegisterComponentInHierarchy<T>()`, which VContainer 1.15.3 registers as Scoped although it always finds
the one component in the scene: it is accepted in the scope that registers it, but a service of a child
scope that injects it (directly or through plain registrations) is a graph error, because VContainer
would create the Scoped registration anew in that child — inject the same scene component again with the
child's dependencies and dispose it with the child. Register such a component with
`RegisterComponent(instance)` when child scopes use it. (The 1.19 fork registers hierarchy components as
singletons; there is nothing to refuse.)

*The trap.* Implementing `IAsyncInitializable` is not enough — the registration has to expose it.
`builder.Register<Catalog>(Lifetime.Singleton)` (or `.As<ICatalog>()` alone) registers a class the
graph never sees, so its `InitializeAsync` is simply never called and nothing else goes wrong: the run
succeeds with a half-built service. The host detects the case and warns:

```text
[RuntimeFlow] session: Catalog implements IAsyncInitializable but is registered without exposing it;
it will never be initialized. Use RegisterInitializable<T>() or .As<IAsyncInitializable>().
```

Use `builder.RegisterInitializable<Catalog>()`, or add `.As<IAsyncInitializable>()` /
`.AsImplementedInterfaces()` to your own registration. A `[DependsOn(typeof(T))]` pointing at such a
type is an error rather than a warning, because the ordering it asks for cannot be honoured at all.
The check reads registrations, not instances, so a factory registration (`Register<ICatalog>(resolver => …)`)
whose product implements `IAsyncInitializable` without exposing it cannot be detected — expose it on
the factory registration.

**Edges.** Three sources, all rendered by `Describe()` with their origin:

| Source | Rule |
|---|---|
| Injected dependency | Whatever VContainer injects: the constructor it would pick (single `[Inject]`, otherwise the first with the most parameters — equal-arity constructors without `[Inject]` log a warning asking for one) plus `[Inject]` methods, fields and properties — the latter only for registrations VContainer constructs or injects itself; an instance (`RegisterInstance`) or a factory product (`Register(resolver => …)`) contributes its constructor parameters only, since VContainer never injects its members. Each is resolved the way VContainer resolves it: a single type to the registration VContainer picks (nearest scope, last registration; with the 1.19 fork's `[Key(x)]`, the registration keyed `x`) — not to every class that happens to be assignable; `IEnumerable<T>` / `IReadOnlyList<T>` to every registration of `T` here and in the parents — the barrier idiom (other collection shapes are not resolvable by VContainer). A dependency that is a plain, non-initializable registration is walked through, so `Profile(IProfileApi)` → `ProfileApi(Auth)` makes `Profile` wait for `Auth` (`ctor: IProfileApi api via ProfileApi`). A parameter supplied by `WithParameter(...)` is no edge; `ContainerLocal<T>` follows local resolution, so a wrapped collection excludes parent singleton elements. A lookup that throws while the edges are derived (an open generic that cannot be closed for the requested arguments, say) is no edge — resolving the service fails the same way and reports it as a construction error. |
| `[DependsOn(typeof(T))]` | An ordering edge to every service assignable to `T`, in this scope or a parent. A target that matches nothing is a graph error listing the known services per scope. |
| Phases | `RuntimeFlowOptions.Phases` is an ordered name list, empty by default. Each phase gets a synthetic barrier: every service of phase *i* must finish before any service of phase *i+1* starts. |

The walk uses the scope VContainer actually constructs each registration in: a child registration
of the same implementation type can make an inherited singleton use the child's dependencies.
If that would recreate a parent initializable service, the graph refuses it rather than treating the
parent's initialized instance as the replacement. Distinct injected overloads keep distinct keyed edges.
Component providers contribute injected members, not constructor parameters. Hierarchy components use
the found object's runtime injector, including dependencies introduced by a subtype; other component
providers use their registered implementation injector. Hidden base members that VContainer does not
inject do not become graph edges. Inspection reads already-created hierarchy instances without resolving them.
An attempted injection into an existing parent component is rejected even if it throws and a factory
catches the error: the attempt may already have changed the parent object's dependencies.
A factory returning the same initialized parent identity is allowed when the child does not also
acquire its synchronous disposal ownership.
A custom, uncreated hierarchy registration with a nonsealed declared type is refused when resolving
from the child could recreate or re-inject a parent service: its unknown subtype cannot be checked
safely. Use parent single-instance registrations without child implementation guards, or an exact
sealed hierarchy type. Factories and VContainer build callbacks can already execute user code before
graph validation; a later graph error cannot undo that code's effects.

**Where the walk stops — add `[DependsOn]`.** Reflection sees what VContainer constructs, not what your
code resolves. These patterns end the transitive walk, so a service behind them gets no edge to what they
resolve; declare the ordering with `[DependsOn(typeof(T))]` on the dependent service:

- a factory: `Register<IFoo>(resolver => new Foo(resolver.Resolve<Auth>()), Lifetime.Singleton)`;
- an instance: `RegisterInstance(foo)` (built by your code, dependencies included);
- the 1.19 fork's `RegisterFromResolve<IFoo>(resolver => resolver.Resolve<Config>().Foo)`;
- a parameter supplied through a delegate: `WithParameter<IFoo>(resolver => resolver.Resolve<Auth>().Foo)`
  (the value is not resolved by VContainer, so the parameter is no edge at all);
- anything resolved inside a constructor or method from an injected `IObjectResolver`.

`Func<T>`, `Lazy<T>`, `ILazy<T>` and VContainer's `LazyDependency<T>` parameters are **not** edges —
that is the documented way to break a cycle, and `Describe()` lists them as `lazy:` rows.
`IObjectResolver`, `IScopedObjectResolver`, `IContainerBuilder`, `RuntimeFlowHost`, `ScopeRun`,
`RuntimeFlowOptions`, `InitContext`, `ILogger`, `object`, `string`, primitives, enums and `decimal` are
ignored as parameters.

Phases apply per scope, to the scopes that use them: as soon as **one** service of a scope carries
`[Init(Phase = …)]`, that scope gets the barriers and every unmarked service of it lands in the **last**
phase (or in `RuntimeFlowOptions.DefaultPhase` when you set one) — a forgotten label makes a service
late, never early. A scope where **no** service declares a phase runs without barriers and its services
report `Phase == null`, so a Global container of unlabelled services is not swept into the last phase of
a list the session declares. Declaring a phase that is not in the list — or any phase at all while the
list is empty — is a graph error.

**Policies** come from `[Init]` on the service class (read with inheritance):

| Member | Effect |
|---|---|
| `Optional` | A failure degrades instead of failing the run: warning, state `Degraded`, dependents still start and see the name in `InitContext.DegradedServices` — including dependents in child scopes, since a parent-scope service that degraded stays `Degraded` there. |
| `UserGated` | The service is waiting for the player: reported as `AwaitingPlayer`, exempt from timeouts and from the stall warning (a run with only gated services in flight logs an informational line instead). Combining it with `TimeoutSeconds` is a graph error. |
| `TimeoutSeconds` | Deadline in seconds, multiplied by `RuntimeFlowOptions.TimeoutMultiplier` (`1.0` by default, `0` disables every timeout). Must be finite and `>= 0` (`0` means none). There is no default timeout: a service without the attribute is never killed. |
| `Weight` | Relative share of `RuntimeFlowStatus.Percent`; `1.0` by default, `0` removes the service from the progress bar. Must be finite and `>= 0`. |

**Scopes.** `RuntimeFlowHost` owns two: a Global container built once and kept warm, and a Session
child scope rebuilt from scratch by every `RestartAsync`. Any other scope is yours —
`host.Session.CreateScope(installer)` followed by `host.InitializeScopeAsync(scope, "lobby")` runs its
graph and ties its lifetime to its parent: a scope below the session is disposed by every restart, one
below `host.Global` survives restarts and goes with the host. Initialize a child scope once its parents
completed — the host refuses one while a startup or restart is in flight, or from a service of a parent
scope that is still initializing. A grandchild scope sees every ancestor, including its parent child
scope. A child being disposed stays tracked until its teardown completes, so its parent is released
after it. Directly disposing a managed child run also freezes and joins its managed descendants before
releasing that child's services. The same child resolver cannot be initialized again, during or after disposal, and a
descendant of that disposed child is refused. `ScopeRun.Create(resolver, name, options)` does the same for a resolver the host knows nothing
about, so a standalone `LifetimeScope` or a headless test container works without a host.
`RuntimeFlowHost.From(existingGlobal, sessionInstaller, options)` wraps a container somebody else built
and never disposes it or its services; if its global phase fails, that host cannot retry it (the
singletons are half-initialized) — build a new container and host.

**VContainer stays yours.** The framework never wraps `IContainerBuilder`: installers receive the real
builder and the whole VContainer API — factories, `RegisterInstance`, decorators through
`Register<I>(resolver => …)`, entry points, build callbacks, `CreateScope`. The host adds at most three
registrations of its own to each scope it composes: itself, an entry-point exception handler and the
entry-point dispatcher. The handler is registered in every composed scope, **before** your installer
runs — so no default handler (the 1.19 fork's `EnsureDispatcherRegistered` registers `Debug.LogException`
wherever none exists yet) can take its place — and yours still always wins: a handler your installer
registers replaces the host's in that scope, and one registered in the global installer handles the
session's entry points too (the session's handler forwards to it). Consequences worth knowing:

- `IInitializable` and `IPostInitializable` entry points run **inside** `Build()`, before the first
  `InitializeAsync` of the scope. Their order is the registration order. An exception thrown by one of
  them fails the run with a message naming the entry point.
- `IStartable` and `ITickable` are frame-driven: they are unordered with respect to asynchronous
  initialization, and in EditMode they never tick at all. When one of them throws later on the player
  loop, the host's handler logs it at Error with the exception — it never swallows it, and it never
  fails a later session build.
- In an owned scope, RuntimeFlow orders async service cleanup together with VContainer-owned
  synchronous cleanup, including already-created plain dependencies. Dependents finish before their
  dependencies, including failed, cancelled and unstarted services. Each physical instance is cleaned
  once per owned interface. Async ownership covers graph services; a locally tracked graph service
  implementing both gets its async call followed by its synchronous call. Plain registrations retain
  their existing synchronous cleanup. Synchronous ownership comes from VContainer's tracker: registered instances and untracked
  transient objects do not acquire it. Remaining container resources are released through scope
  disposal, retried up to 64 attempts when it throws, with repeated failures logged once plus a count.
- A standalone run over a caller-owned scope still leaves synchronous cleanup to that caller. A
  synchronous dependent requiring earlier cleanup than an async service is a graph error before
  initialization; use an owned scope, or expose that dependent as an `IAsyncInitializable` graph
  service and move its cleanup from `IDisposable` to `IAsyncDisposable`.
  Introducing an opaque dependent later is an ownership violation: disposal reports
  `InitGraphException` before releasing services, while leaving caller resources intact.
- Registering your own entry-point exception handler (`builder.RegisterEntryPointExceptionHandler(…)`)
  **supersedes** the host's collector: VContainer allows one handler per scope, so the host's entry is
  replaced by an inert one. From then on an `IInitializable` that throws is routed to your handler and no
  longer fails the run — the exception is yours to rethrow or to swallow. The host logs one informational
  line when it notices, so the change of semantics is never silent: `a custom EntryPointExceptionHandler
  is registered; IInitializable exceptions are delivered to it instead of failing the run.` A handler
  that is exactly `UnityEngine.Debug.LogException` in a container the host did not compose (a root
  `LifetimeScope` built by the 1.19 fork, passed to `RuntimeFlowHost.From`) is VContainer's default, not
  a decision of yours: the session's `IInitializable` failures still fail the session build.

## Failure semantics

Every message is prefixed `[RuntimeFlow] {scope}: `. Levels: Debug for service start/finish, Info for
run and phase boundaries, halts and restarts, Warning for stalls and degradation, Error for failures
and teardown problems.

| Situation | Outcome | Message shape |
|---|---|---|
| Required service throws | Run fails. Token cancelled for everyone; in-flight services are awaited up to `RuntimeFlowOptions.CancellationGrace`; **all** failures collected, then one `RuntimeFlowException` (`Scope`, `Service`, `Phase`, `Elapsed`, `Completed`, `Unfinished`, `Failures`; `InnerException` is the original, or an `AggregateException` when two or more failed). | `Initialization of scope 'session' failed: RemoteCatalog threw InvalidOperationException after 3.2s in phase 'content'. Completed (7): Config, Auth, …; unfinished (4): QuestsWarmup (running 3.1s), StartSession (blocked on RemoteCatalog), …. See InnerException.` |
| Optional service throws | State `Degraded`, run continues, dependents start. The name reaches `InitContext.DegradedServices` of every later service, this scope's and its children's, and `StartupResult.Degraded` — per scope from `ScopeRun.RunAsync`, both scopes (global first) from `RuntimeFlowHost.StartAsync`/`RestartAsync`. | `Telemetry is optional and failed after 1.2s (HttpRequestException: host unreachable); continuing degraded. Dependents: Analytics.` |
| Required service fails behind a degraded one | Same as above, plus the likely cause in the message: every direct dependency that degraded, own scope or parent, is named. | `Initialization of scope 'session' failed: Profile threw NullReferenceException after 0.1s in phase 'content' (after upstream Auth degraded). …` |
| Timeout elapses | The service's token is cancelled and it fails with a `TimeoutException` — required ones bring the run down, optional ones degrade. If it ignores the token, disposal waits for its `InitializeAsync` (up to `CancellationGrace`) before calling its `DisposeAsync`. | `RemoteCatalog did not complete within 30.0s (limit 30s from [Init(TimeoutSeconds = 30)], multiplier 1.0).` |
| User-gated service waits | Nothing happens: no timeout, no stall warning, `AwaitingPlayer` in the status. | `awaiting player: GdprConsent (12.4s).` (informational) |
| No progress for `StallWarningAfter` (10 s) | Warning only, never a kill. Any `ReportProgress` call or service completion resets the timer. | `no progress for 10.0s. Running: RemoteCatalog (12.4s). Awaiting player: GdprConsent (12.4s). Blocked: StartSession (waits for RemoteCatalog) and 9 more.` |
| A service calls `InitContext.Halt` | First call wins. Run state `Halted`, nothing throws; unstarted services are skipped, in-flight ones cancelled; `StartupResult.Outcome` is `StartupOutcome.Halted` with `HaltReason` and `HaltedBy` (also on `GetStatus()`). A service that fails while the halt settles is logged as a warning and does not change the outcome. The scope stays alive until restart or disposal. | `halted by UserUpdateCheck — 'user-update.required' after 4.2s (7/12 services completed; cancelled: RemoteCatalog).` |
| A **global** service halts | No session is built: `StartAsync` returns the global run's `Halted` result (`Scope == "global"`), `State` is `Halted`, `Session` throws `InvalidOperationException` naming the halt, and `RestartAsync` is refused — the global scope is never rebuilt, so dispose the host and create a new one. | `the global scope was halted by UserUpdateCheck ('user-update.required'); no session is built and the host cannot be restarted.` |
| Caller's token cancelled | `RunAsync` completes cancelled (`OperationCanceledException`), `OnRunCancelled` fires. Services that ignore their token are reported and abandoned after `CancellationGrace` (`Timeout.InfiniteTimeSpan` waits forever — then `CancelAsync`, `DisposeAsync` and every restart wait forever too). | `cancelled after 1.2s (3/12 services completed).` / `2 services still running 5.0s after cancellation: A (5.0s), B (5.0s). Continuing teardown; they must observe their CancellationToken.` |
| A cancellation callback throws (`ct.Register(() => request.Abort())`) | Logged at Error; the other callbacks still run and the run still settles, releases its dependents and disposes. | `a cancellation callback threw InvalidOperationException (abort failed) while cancelling the run; the other callbacks ran, continuing.` |
| The logger itself throws | Swallowed: a run never wedges on its logger. The first failure of each logger is reported once through `Debug.LogWarning`. | — |
| `RestartAsync` | Session torn down and rebuilt; see below. | `restart requested: 'bundles-updated' (session run in progress: cancelling)` |
| More than `MaxRestartsPerWindow` (5) accepted restarts in `RestartWindow` (60 s; zero or less means over the host's lifetime) | `RestartAsync` returns a faulted task with a `RuntimeFlowException`; the refused request is not counted. | `Restart budget exceeded: 6 restarts within 60s (limit 5). Reasons: bundles-updated, auth-lost, …` |
| `IInitializable` entry point throws | Run fails before any `InitializeAsync`. | `Initialization of scope 'session' failed: SaveMigration threw IOException while VContainer was running the IInitializable entry points of the scope: disk full. See InnerException.` |

Graph problems are detected before the first `InitializeAsync` and raise `InitGraphException`: cycles
(with the path and every edge origin), unknown `[DependsOn]` targets, non-singleton registrations,
unknown or undeclared phases, duplicate phase names, `UserGated` combined with `TimeoutSeconds`,
negative or non-finite `Weight` / `TimeoutSeconds` / `TimeoutMultiplier`, a negative
`CancellationGrace`, and construction failures of a single service — the last one names that service
(and the exception its constructor threw) instead of surfacing one container exception for the whole
scope. When the run starts, every required service that failed to construct is reported at once,
before anything starts; an optional one degrades in its turn. Lifetimes, `[Init]` values and the
scene-component rule are checked before anything is constructed by the graph. Any owned build failure
releases already-created graph services, including those resolved by builder callbacks before an
early validation or entry-point error — `DisposeAsync` on `IAsyncDisposable` services, and the scope
itself — only when the run was going to own the scope
(`ownsScope: true`, the host's session and child scopes); a caller-owned container
(`RuntimeFlowHost.From`, `ScopeRun.Create` without `ownsScope`) keeps its singletons untouched. A child
scope whose service depends on a parent service that never initialized (a halt skipped it, it failed or
was cancelled) fails that service at the start of the run instead of starting it on half a parent.

`ScopeRun.CreateAsync` has the same arguments as `Create` and awaits owned failure cleanup before
throwing the original graph error. Cleanup awaits services in dependent-first order where their edges
are known, releases each instance once, and disposes the resolver last. Synchronous `Create` starts the
same cleanup but may throw before it finishes. `ownsScope: true` transfers ownership immediately: after
a failed build use a fresh resolver. The host uses the awaited path and keeps ancestor resources alive
through pending construction cleanup, including during restart or disposal.
Failure cleanup preserves known parent instances even if a factory cached or tracked the same object
in the child: those child ownership entries are removed without releasing the parent's object.
Ownership is checked around cleanup callbacks, including resources and aliases first created during
cleanup. Planned and residual synchronous cleanup share one record of already released instances.
Cached factory results that are `null` do not represent owned instances and do not interrupt cleanup.

## Progress and status

`GetStatus()` returns an immutable `RuntimeFlowStatus`, cheap enough to poll from a UI every frame (the
snapshot of a finished service and every dependency-name list are built once and shared):
`State`, `Scope`, current `Phase`, `Services` and `Running` (each a `ServiceStatus` with `State`,
`Elapsed`, `Progress`, `Weight`, `Dependencies`, `WaitingOn`, `Error`), `CompletedCount`/`TotalCount`,
weighted `Percent`, `Elapsed`, `RestartCount`, `HaltReason`, `HaltedBy`, `Error`. On the host, the
snapshot spans both scopes and the percentage is weighted across all of them. A service refines its own
share with `InitContext.ReportProgress(0f..1f)`; a report that arrives after the service finished is
ignored, as is NaN (it does not reset the stall timer). Weighted sums are normalized so even large
finite weights retain finite percentages. `InitContext.DegradedServices` returns an immutable snapshot on every read — safe to enumerate
across an `await` — so read it again to see later degradations.

Push notifications come from `IRuntimeFlowObserver` (every method is a default interface member, so
implement only what you need): `OnRunStarted`/`OnRunCompleted`/`OnRunHalted`/`OnRunFailed`/`OnRunCancelled`,
`OnPhaseStarted`/`OnPhaseCompleted`,
`OnServiceStarted`/`OnServiceAwaitingPlayer`/`OnServiceCompleted`/`OnServiceFailed`. Add them to
`RuntimeFlowOptions.Observers` before starting the host: the set is snapshotted when each run starts
(`RunAsync`, not `ScopeRun.Create`). An observer that throws is logged and skipped. By the time
`OnServiceFailed` fires for a required service the run is already failing, so an observer that cancels
the run there cannot turn the failure into a cancellation.

`Describe()` renders the graph itself — the fastest way to answer "why did this run in that order":

```text
scope 'session' — 3 services, phases: platform > content
 1 [platform] Mirrors        required, weight 1
 2 [content]  RemoteCatalog  required, user-gated, weight 2
      after Mirrors           (ctor: ICdnMirrorSelector mirrorSelector)
      after phase 'platform'  (phase barrier)
      lazy: Func<IAnalytics> analytics
 3 [content]  Telemetry      optional, timeout 10s, weight 1
      after phase 'platform'  (phase barrier)
external (from parent scopes): GlobalConfig [global, initialized]
```

## Restart

`await host.RestartAsync("reason")` rebuilds the session scope only; the global container and its
services stay warm and are never initialized twice.

- **Deferred.** The work starts after a yield, so a service may request a restart from inside its own
  `InitializeAsync` without re-entrancy. Request it as `_ = host.RestartAsync("reason")` and do **not**
  await it there: the returned task completes only after the current run has been cancelled and torn
  down, and teardown waits out `CancellationGrace` for the very service that is awaiting — the service
  would be waiting for itself (forever, with `Timeout.InfiniteTimeSpan`). Fire it and return; observers
  and `InitContext.IsRestart` report the new generation.
- **Coalesced.** Requests arriving while a restart is already pending join it; ten clicks produce one
  rebuild. `StartAsync` and `RestartAsync` both return the result of the *last* run in the chain, so an
  awaiter started before a restart still sees the final outcome. A request arriving before a session
  exists — during the global phase of the startup — makes the startup build its first session directly
  as the restart (generation 1); if the global phase fails, the request is dropped (and not budgeted)
  and its awaiters observe the startup failure.
- **Never waits on a doomed run.** Accepting a restart freezes the running session at once — no
  dependent of the requesting service starts on stale data — and cancels it right after the caller's
  stack unwinds. So a service may request a restart and then return, park on its token, or throw a
  cancellation: the next generation comes either way. A frozen generation never reports `Completed` —
  it ends `Cancelled`, even when all of its services happened to finish. Chains work: an addressables service that
  restarts, then a configs service of the new generation that restarts again, ends in generation 2.
- **Tokens.** A token passed to `RestartAsync` cancels the new session run. The requesting service's own
  token (or any token of the run being torn down) is ignored with a warning — it would cancel the new
  generation at birth. A token that is already cancelled yields a cancelled task and changes nothing.
- **Global is never rebuilt.** A global service may request a restart, but must not wait on its own
  token afterwards: nothing cancels it and the startup would never finish (a warning says so). After a
  halt in the global scope `RestartAsync` is refused with `InvalidOperationException`.
- **Recoverable.** If a restart cannot build the session (an installer or entry point throws, the graph
  is invalid), the host reports `State == Failed`, `GetStatus().Error` carries the exception, `Session`
  throws with it as `InnerException`, and the next `RestartAsync` builds a fresh session — a "Retry"
  button works. `State` is `Running` for the whole restart, teardown included.
- **Budgeted.** `MaxRestartsPerWindow` within `RestartWindow`; exceeding it fails with the list of
  reasons instead of looping forever.
- **Visible.** Services see `InitContext.IsRestart` and `InitContext.Generation` (0 for the first run,
  +1 per restart); observers see `OnRunStarted(scope, isRestart: true)`, which is where a UI cover
  belongs.
- **Ordered teardown.** The current run is cancelled, every service token with it — including tokens
  the service kept for background work after `InitializeAsync` returned. Then the child runs below the
  session are disposed in reverse creation order, then the session run. Graph-owned async cleanup and
  locally tracked synchronous cleanup follow dependency order, with dependent instances first;
  independent services retain reverse completion priority. Cleanup failures are logged, and remaining
  container resources are released through final scope disposal.
- **Quit-aware.** After `Application.quitting` the host cancels the current run and every child run,
  refuses further restarts and child scopes instead of rebuilding into a dying player; a restart already
  in flight stops before building anything. When that leaves no session, `State` is `Cancelled` and
  `Session` says the application is quitting. `DisposeAsync` freezes every run at once (no service of
  any scope starts after it was called), cancels them right after a yield (so a service may call it
  from its own `InitializeAsync`), waits for a chain in flight to unwind (bounded, unless the grace is
  infinite) and never lets it build on a disposed global; afterwards `Global` and `Session` throw
  `ObjectDisposedException`. Pending construction cleanup is joined before releasing ancestor resources;
  this cleanup wait, like service `DisposeAsync`, is not bounded by `CancellationGrace`.
- **Child scopes are refused when doomed.** `InitializeScopeAsync` refuses a child scope once the
  application is quitting, and one whose own constructors requested a restart (or disposed the host)
  while it was being built — its scope is disposed and nothing of it starts.

## Testing

`RuntimeFlow.Testing` (needs the `testables` entry) runs the production installers headlessly, without
a `MonoBehaviour` and without a scene:

```csharp
await using var app = await TestFlow.Create(GlobalInstaller.Register, SessionInstaller.Register)
    .Override<IProfileApi>(new FakeProfileApi())
    .ObserveWith(observer)
    .WithStartupTimeout(TimeSpan.FromSeconds(5))
    .StartAsync();

Assert.That(app.Result!.Outcome, Is.EqualTo(StartupOutcome.Completed));
Assert.That(app.Resolve<PlayerProfile>().Coins, Is.EqualTo(10));
```

- `Override<T>(instance)`, `Override<T>(factory)` and `Override<T, TImpl>()` **remove** every registration that exposes `T`
  from the installers before registering the replacement, so the real service is never constructed and
  never appears in the collection the graph is discovered from. An override that matches nothing fails
  the test naming the type. `Configure(options => …)` reaches the rest of `RuntimeFlowOptions`; the
  defaults are timeouts off, a two-second stall threshold and a capturing logger exposed as `Log`.
  The instance overload registers the *same* object on every build of its scope, so a session override
  is initialized once per restart generation (and disposed in between) with its state carried over;
  `Override<T>(() => new Fake())` creates a fresh one per build, which is what a restart test wants.
- A caller cancellation of `StartAsync` surfaces as `OperationCanceledException`, never as the
  startup timeout.
- `LifecycleFake` without a stub returns completed tasks from `Task`/`Task<T>` members (default
  results), so awaiting an unstubbed member never throws.
- `WithStartupTimeout` (30 s by default) turns a hang into a `TimeoutException` whose message contains
  the status: what was running, for how long, and what each blocked service was waiting for. It also
  bounds waiting for failed-startup cleanup; ordered cleanup continues afterwards and an explicit
  `DisposeAsync` joins it. Unsupported timer durations are rejected before allocating a host.
- `LifecycleFake.Of<TService>(stub, cfg => …)` builds a `DispatchProxy` fake that takes part in the
  lifecycle: `FailInitializeAttempts(n)`, `FailDisposeAttempts(n)`, `DelayInitialize(span)`, `Hang()`.
  `OfHandle` also returns a `FakeInvocationLog` recording every call (`initialize#1`, `disposeAsync#1`).
  `DispatchProxy` emits IL at runtime, so `LifecycleFake` works in the Editor and in Mono players only;
  under IL2CPP it throws. Nothing else in `RuntimeFlow.Testing` needs it, and no player build ships it —
  write a hand-rolled stub class for an IL2CPP test.
- `CollectingObserver` records callbacks as `"started:session:Catalog"`-style strings for order
  assertions.

Both EditMode and PlayMode behave identically: everything runs on the caller's
`SynchronizationContext`, which the editor provides as well.

## Dashboard

**Window → RuntimeFlow → Dashboard** (UI Toolkit, editor-only). Three tabs — Graph (services grouped by
scope and phase with live state, elapsed and weight, a detail card with dependencies and what a service
is waiting on, and a red card carrying the exception of a failure), Scopes (Global → Session → child
runs with counts, percentage, restart count and uptime) and Last run (the final snapshot plus
`Describe()` after exiting play mode). The toolbar restarts the session and copies the diagnostics as
JSON or as `Describe()` text. Selections and dependency links distinguish separate runs even when their
scope or service names match, and removing another host preserves the selected host. Last run keeps
the stored snapshot visible during a later run; before any snapshot is stored it shows a labelled
live preview. A failed session build displays and exports the host exception even when there is no
failed service row.

## Demo

[`RuntimeFlow.UnityTests/Assets/Demo`](https://github.com/PraxeumGames/RuntimeFlow/blob/main/RuntimeFlow.UnityTests/Assets/Demo)
is a runnable startup flow with a fake backend and chaos toggles —
throw in a profile service, hang a warmup, trip a catalog timeout, trigger a maintenance halt, skip the
consent gate — so every failure path in the table above can be watched in the dashboard.

## Logging

The default sink is `UnityConsoleLogger`, which maps Trace/Debug/Information to `Debug.Log`, Warning to
`Debug.LogWarning`, and Error/Critical to `Debug.LogError`, prefixing `[RuntimeFlow] `. Its `MinLevel`
defaults to `LogLevel.Debug`. `RuntimeFlowOptions.Logger` accepts any
Microsoft.Extensions.Logging `ILogger`; there is no `NullLogger` default, ever.

The package ships `Microsoft.Extensions.Logging.Abstractions.dll` under `Runtime/Plugins/`. Its importer
has reference validation disabled on purpose: the assembly carries a reference to
Microsoft.Extensions.DependencyInjection.Abstractions that nothing in RuntimeFlow uses, and Unity would
otherwise report it as a missing dependency.

**Projects that already ship Microsoft.Extensions.Logging.Abstractions** (NuGetForUnity, another
package) would otherwise fail with "Multiple precompiled assemblies with the same name". The plugin is
constrained to `!RUNTIMEFLOW_EXTERNAL_MEL`: add `RUNTIMEFLOW_EXTERNAL_MEL` to *Player Settings ▸
Scripting Define Symbols* (every platform you build) and the package's copy is excluded. RuntimeFlow's
assemblies reference the DLL by file name, so they then compile against your copy. The package ships
8.0.0; it only uses `ILogger`, `LogLevel` and `EventId`, which every version provides.

## Package contents

| Path | Contents |
|---|---|
| `Runtime/` | Public API: `IAsyncInitializable`, `InitContext`, `InitAttribute`, `DependsOnAttribute`, `RuntimeFlowHost`, `RuntimeFlowOptions`, `ScopeRun`, `StartupResult`/`StartupOutcome`, `RuntimeFlowException`, `InitGraphException`, `IRuntimeFlowObserver`, `RuntimeFlowStatus`/`ServiceStatus`/`ServiceState`/`RunState`, `UnityConsoleLogger`, `RuntimeFlowRegistrationExtensions`. |
| `Runtime/Internal/` | `GraphBuilder`, `ConstructorEdges`, `InjectionEdges`, `GraphDescriber`, `ServiceNode`, `Scheduler`, `StallWatch`, `ObserverList`, `Cancellation`, `ScopeDisposal`, `FlowRegistry`. |
| `Runtime/Testing/` | `TestFlow`, `LifecycleFake`, `CollectingObserver` (assembly `RuntimeFlow.Testing`, constrained to `UNITY_INCLUDE_TESTS`). |
| `Runtime/Properties/` | `AssemblyInfo.cs` — the `InternalsVisibleTo` grants for `RuntimeFlow.Editor`, `RuntimeFlow.Testing` and the two test assemblies. |
| `Runtime/Plugins/` | Microsoft.Extensions.Logging.Abstractions. |
| `Editor/` | `RuntimeFlowDashboardWindow` and its stylesheet (assembly `RuntimeFlow.Editor`). It is the assembly's only public type; the snapshot model, the JSON dump and the three views are internal, visible to the test assembly through `Editor/Properties/AssemblyInfo.cs`. |

MIT licensed; see LICENSE.md and Third Party Notices.md in the package root.
