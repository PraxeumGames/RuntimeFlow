# RuntimeFlow

Asynchronous initialization graph for Unity, built on stock VContainer. A service implements
`IAsyncInitializable`, is registered with VContainer in whatever way you like, and the framework
derives the startup order from the container itself: constructor parameters are edges, the graph is
validated before anything runs, and services start as soon as their own dependencies are done.

Repository-level layout and development commands: [`../README.md`](../README.md).
Design rationale, algorithms and the complete message catalogue: [`../docs/DESIGN.md`](../docs/DESIGN.md).

## Install

`Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.praxeum.runtimeflow": "https://github.com/PraxeumGames/RuntimeFlow.git?path=com.praxeum.runtimeflow#1.0.0"
  },
  "testables": [
    "com.praxeum.runtimeflow"
  ]
}
```

VContainer is a declared dependency and resolves automatically (upstream `hadashiA/VContainer`, tag
1.15.3, pinned by SHA). The `testables` entry defines `UNITY_INCLUDE_TESTS` for the package, which is
what compiles the `RuntimeFlow.Testing` assembly; omit it if you do not want the harness.

Requires Unity 2021.3+ (validated on 2022.3.62f2).

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

**Services.** A service is any VContainer registration that exposes `IAsyncInitializable` and is
`Lifetime.Singleton`. Discovery is scope-local: each scope initializes only its own registrations;
services inherited from parent scopes are treated as already initialized externals. A non-singleton
registration is a graph error, because a scoped or transient service would be re-created uninitialized
in a child scope.

**Edges.** Three sources, all rendered by `Describe()` with their origin:

| Source | Rule |
|---|---|
| Constructor parameter | The constructor VContainer would inject (single `[Inject]`, otherwise the one with most parameters). A parameter typed as another service is an edge; a parameter typed `IEnumerable<T>`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>`, `IList<T>`, `ICollection<T>`, `List<T>` or `T[]` produces edges to every service assignable to `T` — the barrier idiom. An interface with several implementations produces an edge to each. |
| `[DependsOn(typeof(T))]` | An ordering edge to every service assignable to `T`, in this scope or a parent. A target that matches nothing is a graph error listing the known services per scope. |
| Phases | `RuntimeFlowOptions.Phases` is an ordered name list, empty by default. Each phase gets a synthetic barrier: every service of phase *i* must finish before any service of phase *i+1* starts. |

`Func<T>`, `Lazy<T>` and `ILazy<T>` parameters are **not** edges — that is the documented way to break
a cycle, and `Describe()` lists them as `lazy:` rows. `IObjectResolver`, `IScopedObjectResolver`,
`IContainerBuilder`, `RuntimeFlowHost`, `ScopeRun`, `RuntimeFlowOptions`, `InitContext`, `ILogger`,
`object`, `string`, primitives and enums are ignored as parameters.

A service with no `[Init(Phase = …)]` lands in the **last** phase (or in
`RuntimeFlowOptions.DefaultPhase` when you set one). A forgotten label therefore makes a service late,
never early. Declaring a phase that is not in the list — or any phase at all while the list is empty —
is a graph error.

**Policies** come from `[Init]` on the service class (read with inheritance):

| Member | Effect |
|---|---|
| `Optional` | A failure degrades instead of failing the run: warning, state `Degraded`, dependents still start and see the name in `InitContext.DegradedServices`. |
| `UserGated` | The service is waiting for the player: reported as `AwaitingPlayer`, exempt from timeouts and from the stall warning (a run with only gated services in flight logs an informational line instead). Combining it with `TimeoutSeconds` is a graph error. |
| `TimeoutSeconds` | Deadline in seconds, multiplied by `RuntimeFlowOptions.TimeoutMultiplier` (`1.0` by default, `0` disables every timeout). There is no default timeout: a service without the attribute is never killed. |
| `Weight` | Relative share of `RuntimeFlowStatus.Percent`; `1.0` by default, `0` removes the service from the progress bar. |

**Scopes.** `RuntimeFlowHost` owns two: a Global container built once and kept warm, and a Session
child scope rebuilt from scratch by every `RestartAsync`. Any other scope is yours —
`host.Session.CreateScope(installer)` followed by `host.InitializeScopeAsync(scope, "lobby")` runs its
graph and ties its lifetime to the session. `ScopeRun.Create(resolver, name, options)` does the same
for a resolver the host knows nothing about, so a standalone `LifetimeScope` or a headless test
container works without a host. `RuntimeFlowHost.From(existingGlobal, sessionInstaller, options)` wraps
a container somebody else built and never disposes it.

**VContainer stays yours.** The framework never wraps `IContainerBuilder`: installers receive the real
builder and the whole VContainer API — factories, `RegisterInstance`, decorators through
`Register<I>(resolver => …)`, entry points, build callbacks, `CreateScope`. The host adds exactly three
registrations of its own to each scope it composes: itself, an entry-point exception handler
(registered first, so yours overrides it) and the entry-point dispatcher. Consequences worth knowing:

- `IInitializable` and `IPostInitializable` entry points run **inside** `Build()`, before the first
  `InitializeAsync` of the scope. Their order is the registration order. An exception thrown by one of
  them fails the run with a message naming the entry point.
- `IStartable` and `ITickable` are frame-driven: they are unordered with respect to asynchronous
  initialization, and in EditMode they never tick at all.
- VContainer disposes `IDisposable` registrations when the scope is disposed, in reverse creation
  order. RuntimeFlow additionally calls `IAsyncDisposable.DisposeAsync` on the services it started.

## Failure semantics

Every message is prefixed `[RuntimeFlow] {scope}: `. Levels: Debug for service start/finish, Info for
run and phase boundaries, halts and restarts, Warning for stalls and degradation, Error for failures
and teardown problems.

| Situation | Outcome | Message shape |
|---|---|---|
| Required service throws | Run fails. Token cancelled for everyone; in-flight services are awaited up to `RuntimeFlowOptions.CancellationGrace`; **all** failures collected, then one `RuntimeFlowException` (`Scope`, `Service`, `Phase`, `Elapsed`, `Completed`, `Unfinished`, `Failures`; `InnerException` is the original, or an `AggregateException` when two or more failed). | `Initialization of scope 'session' failed: RemoteCatalog threw InvalidOperationException after 3.2s in phase 'content'. Completed (7): Config, Auth, …; unfinished (4): QuestsWarmup (running 3.1s), StartSession (blocked on RemoteCatalog), …. See InnerException.` |
| Optional service throws | State `Degraded`, run continues, dependents start. | `Telemetry is optional and failed after 1.2s (HttpRequestException: host unreachable); continuing degraded. Dependents: Analytics.` |
| Timeout elapses | The service's token is cancelled and it fails with a `TimeoutException` — required ones bring the run down, optional ones degrade. | `RemoteCatalog did not complete within 30.0s (limit 30s from [Init(TimeoutSeconds = 30)], multiplier 1.0).` |
| User-gated service waits | Nothing happens: no timeout, no stall warning, `AwaitingPlayer` in the status. | `awaiting player: GdprConsent (12.4s).` (informational) |
| No progress for `StallWarningAfter` (10 s) | Warning only, never a kill. Any `ReportProgress` call or service completion resets the timer. | `no progress for 10.0s. Running: RemoteCatalog (12.4s). Awaiting player: GdprConsent (12.4s). Blocked: StartSession (waits for RemoteCatalog) and 9 more.` |
| A service calls `InitContext.Halt` | First call wins. Run state `Halted`, nothing throws; unstarted services are skipped, in-flight ones cancelled; `StartupResult.Outcome` is `StartupOutcome.Halted` with `HaltReason` and `HaltedBy`. The scope stays alive until restart or disposal. | `halted by UserUpdateCheck — 'user-update.required' after 4.2s (7/12 services completed; cancelled: RemoteCatalog).` |
| Caller's token cancelled | `RunAsync` completes cancelled (`OperationCanceledException`). Services that ignore their token are reported and abandoned. | `2 services still running 5.0s after cancellation: A (5.0s), B (5.0s). Continuing teardown; they must observe their CancellationToken.` |
| `RestartAsync` | Session torn down and rebuilt; see below. | `restart requested: 'bundles-updated' (session run in progress: cancelling)` |
| More than `MaxRestartsPerWindow` (5) restarts in `RestartWindow` (60 s) | `RestartAsync` returns a faulted task with a `RuntimeFlowException`. | `Restart budget exceeded: 6 restarts within 60s (limit 5). Reasons: bundles-updated, auth-lost, …` |
| `IInitializable` entry point throws | Run fails before any `InitializeAsync`. | `Initialization of scope 'session' failed: SaveMigration threw IOException while VContainer was running the IInitializable entry points of the scope: disk full. See InnerException.` |

Graph problems are detected before the first `InitializeAsync` and raise `InitGraphException`: cycles
(with the path and every edge origin), unknown `[DependsOn]` targets, non-singleton registrations,
unknown or undeclared phases, `UserGated` combined with `TimeoutSeconds`, and construction failures of
a single service — the last one names that service instead of surfacing one container exception for the
whole scope.

## Progress and status

`GetStatus()` returns an immutable `RuntimeFlowStatus`, cheap enough to poll from a UI every frame:
`State`, `Scope`, current `Phase`, `Services` and `Running` (each a `ServiceStatus` with `State`,
`Elapsed`, `Progress`, `Weight`, `Dependencies`, `WaitingOn`, `Error`), `CompletedCount`/`TotalCount`,
weighted `Percent`, `Elapsed`, `RestartCount`, `HaltReason`, `Error`. On the host, the snapshot spans
both scopes and the percentage is weighted across all of them. A service refines its own share with
`InitContext.ReportProgress(0f..1f)`.

Push notifications come from `IRuntimeFlowObserver` (every method is a default interface member, so
implement only what you need): `OnRunStarted`/`OnRunCompleted`/`OnRunHalted`/`OnRunFailed`,
`OnPhaseStarted`/`OnPhaseCompleted`,
`OnServiceStarted`/`OnServiceAwaitingPlayer`/`OnServiceCompleted`/`OnServiceFailed`. Add them to
`RuntimeFlowOptions.Observers`. An observer that throws is logged and skipped.

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
external (initialized in parent scopes): GlobalConfig [global]
```

## Restart

`await host.RestartAsync("reason")` rebuilds the session scope only; the global container and its
services stay warm and are never initialized twice.

- **Deferred.** The work starts after a yield, so a service may request a restart from inside its own
  `InitializeAsync` without re-entrancy.
- **Coalesced.** Requests arriving while a restart is already pending join it; ten clicks produce one
  rebuild. `StartAsync` and `RestartAsync` both return the result of the *last* run in the chain, so an
  awaiter started before a restart still sees the final outcome.
- **Budgeted.** `MaxRestartsPerWindow` within `RestartWindow`; exceeding it fails with the list of
  reasons instead of looping forever.
- **Visible.** Services see `InitContext.IsRestart` and `InitContext.Generation` (0 for the first run,
  +1 per restart); observers see `OnRunStarted(scope, isRestart: true)`, which is where a UI cover
  belongs.
- **Ordered teardown.** The current run is cancelled, every service token with it — including tokens
  the service kept for background work after `InitializeAsync` returned. Then child runs are disposed
  in reverse creation order, then the session run: `IAsyncDisposable.DisposeAsync` on every service in
  reverse completion order (failures logged, never thrown), then the scope itself, where VContainer
  disposes `IDisposable` registrations in reverse creation order.
- **Quit-aware.** After `Application.quitting` the host cancels the current run and refuses further
  restarts instead of rebuilding into a dying player.

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

- `Override<T>(instance)` and `Override<T, TImpl>()` **remove** every registration that exposes `T`
  from the installers before registering the replacement, so the real service is never constructed and
  never appears in the collection the graph is discovered from. An override that matches nothing fails
  the test naming the type. `Configure(options => …)` reaches the rest of `RuntimeFlowOptions`; the
  defaults are timeouts off, a two-second stall threshold and a capturing logger exposed as `Log`.
- `WithStartupTimeout` (30 s by default) turns a hang into a `TimeoutException` whose message contains
  the status: what was running, for how long, and what each blocked service was waiting for.
- `LifecycleFake.Of<TService>(stub, cfg => …)` builds a `DispatchProxy` fake that takes part in the
  lifecycle: `FailInitializeAttempts(n)`, `FailDisposeAttempts(n)`, `DelayInitialize(span)`, `Hang()`.
  `OfHandle` also returns a `FakeInvocationLog` recording every call (`initialize#1`, `disposeAsync#1`).
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
JSON or as `Describe()` text.

## Demo

`RuntimeFlow.UnityTests/Assets/Demo` is a runnable startup flow with a fake backend and chaos toggles —
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

## Package contents

| Path | Contents |
|---|---|
| `Runtime/` | Public API: `IAsyncInitializable`, `InitContext`, `InitAttribute`, `DependsOnAttribute`, `RuntimeFlowHost`, `RuntimeFlowOptions`, `ScopeRun`, `StartupResult`/`StartupOutcome`, `RuntimeFlowException`, `InitGraphException`, `IRuntimeFlowObserver`, `RuntimeFlowStatus`/`ServiceStatus`/`ServiceState`/`RunState`, `UnityConsoleLogger`, `RuntimeFlowRegistrationExtensions`. |
| `Runtime/Internal/` | `GraphBuilder`, `ConstructorEdges`, `GraphDescriber`, `ServiceNode`, `Scheduler`, `StallWatch`, `ObserverList`, `FlowRegistry`. |
| `Runtime/Testing/` | `TestFlow`, `LifecycleFake`, `CollectingObserver` (assembly `RuntimeFlow.Testing`, constrained to `UNITY_INCLUDE_TESTS`). |
| `Runtime/Plugins/` | Microsoft.Extensions.Logging.Abstractions. |
| `Editor/` | `RuntimeFlowDashboardWindow` and its stylesheet (assembly `RuntimeFlow.Editor`). |

MIT licensed; see LICENSE.md and Third Party Notices.md in the package root.
