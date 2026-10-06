# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-09-03

Complete rewrite. There is no migration path from 0.x by design: the public surface shrank from 234
types to about 25, and every 0.x concept below is gone.

### Changed

- **One graph, derived from the container.** Initialization order is no longer declared anywhere. After
  `Build()` the framework enumerates the scope's own VContainer registrations of
  `IAsyncInitializable`, builds a DAG from constructor parameters (plus validated `[DependsOn]` and
  optional named phase barriers), validates it synchronously, and executes it dynamically — a service
  starts as soon as *its* dependencies are done, independent services overlap. No central flow file, no
  stage markers, no wave scheduler, no `GameFlow` vocabulary.
- **Stock VContainer.** The custom container built on top of VContainer (5.8k LOC, required a fork) is
  gone; so is the fork. The dependency is upstream `hadashiA/VContainer`, tag 1.15.3, pinned by SHA.
  The framework never wraps `IContainerBuilder`: installers get the real builder and the whole
  VContainer API — factories, decorators, entry points, build callbacks, `CreateScope`.
- **One lifecycle interface.** `IAsyncInitializable.InitializeAsync(InitContext, CancellationToken)`
  replaces 22 lifecycle marker interfaces across three orthogonal axes, two initialization signatures,
  five disposal contracts and four activation contracts.
- **One entry point.** `RuntimeFlowHost` (Global + Session, restart) or `ScopeRun` (any resolver)
  replace the five ways 0.x offered to start a game.
- **Transparent failures by default.** The default logger is `UnityConsoleLogger`, never `NullLogger`.
  A failed run raises one `RuntimeFlowException` naming the service, scope, phase and elapsed time,
  listing everything that completed and everything left unfinished with the reason it was blocked, and
  carrying the original exception as `InnerException` (an `AggregateException` only when two or more
  services failed). Sibling failures are collected instead of being lost.
- **No default timeout.** The self-learning health watchdog (5 s default, keyed by registered
  interface) is gone. A deadline exists only where `[Init(TimeoutSeconds = n)]` says so, scaled by
  `RuntimeFlowOptions.TimeoutMultiplier`. A run that makes no progress produces a warning naming what
  is running, awaiting the player and blocked — never a kill.
- **Single-threaded by contract.** Everything runs on the caller's `SynchronizationContext`; no locks,
  no `ConfigureAwait(false)`, no thread-affinity providers, no scheduler abstractions, no ambient
  statics holding runtime state.
- **Restart is budgeted.** Restarts are deferred (safe to request from inside `InitializeAsync`),
  coalesced, and limited to `MaxRestartsPerWindow` per `RestartWindow`; exceeding the budget raises a
  `RuntimeFlowException` listing the reasons instead of looping in production.
- **Layout and packaging.** Inside the package, namespace follows folder (`RuntimeFlow`,
  `RuntimeFlow.Internal`, `RuntimeFlow.Testing`, `RuntimeFlow.Editor`) and a script enforces it; the
  test and demo assemblies keep their asmdef root namespace instead. No solution file, no `dotnet`
  build, no analyzers shipped in the package; the demo lives in the Unity test project instead of
  `Samples~`.
- **Unity 2022.3 is the minimum**, up from 2021.3: the dashboard uses UI Toolkit APIs introduced in
  2022.2. Both API compatibility levels of 2022.3 support the default interface members the package
  relies on, so no project setting has to change.
- **VContainer must be declared in the consuming project's manifest.** The package still names
  `jp.hadashikick.vcontainer` (upstream, tag 1.15.3, pinned by SHA) in its `dependencies`, but UPM
  resolves git-URL dependencies only from `Packages/manifest.json`, so a consumer adds the same line
  next to `com.praxeum.runtimeflow`. Projects that already ship VContainer keep theirs.

### Added

- `IAsyncInitializable` — the single lifecycle contract.
- `InitContext` — `Scope`, `IsRestart`, `Generation`, `DegradedServices`, `Halt(reason)`,
  `ReportProgress(fraction)`.
- `InitAttribute` — `[Init(Phase, Optional, UserGated, TimeoutSeconds, Weight)]`, read with inheritance.
- `DependsOnAttribute` — validated ordering escape hatch.
- `RuntimeFlowHost` — Global + Session lifecycle: `StartAsync`, `RestartAsync`, `InitializeScopeAsync`,
  `GetStatus`, `Describe`, `Global`, `Session`, `State`, `Generation`, `RestartCount`, `IsQuitting`,
  `DisposeAsync`, and the `From(existingGlobal, session, options)` factory for projects that already own
  a root container.
- `RuntimeFlowOptions` — `Logger`, `Phases`, `DefaultPhase`, `StallWarningAfter` (10 s),
  `CancellationGrace` (5 s), `TimeoutMultiplier` (1.0), `MaxRestartsPerWindow` (5), `RestartWindow`
  (60 s), `Observers`.
- `ScopeRun` — the graph of one resolver: `Create`, `RunAsync`, `CancelAsync`, `GetStatus`, `Describe`,
  `DisposeAsync`, usable standalone on any container.
- `StartupResult` / `StartupOutcome` — `Completed` or `Halted`, with `HaltReason`, `HaltedBy`,
  `Degraded`, `Elapsed`.
- `RuntimeFlowException` — `Scope`, `Service`, `Phase`, `Elapsed`, `Completed`, `Unfinished`,
  `Failures`.
- `InitGraphException` — graph problems raised before the first `InitializeAsync`.
- `IRuntimeFlowObserver` — default-interface-member hooks for run, phase and service events; every run
  ends in exactly one of `OnRunCompleted`, `OnRunHalted`, `OnRunFailed`, `OnRunCancelled`.
- `RuntimeFlowStatus`, `ServiceStatus`, `ServiceState`, `RunState` — pull snapshots with weighted
  `Percent`, `WaitingOn`, elapsed, errors, `HaltReason` and `HaltedBy`.
- `UnityConsoleLogger` — the default Microsoft.Extensions.Logging sink for the Unity console.
- `RuntimeFlowRegistrationExtensions.RegisterInitializable<T>()` — optional registration sugar.
- `RuntimeFlow.Testing`: `TestFlow` (headless host with `Override<T>` — instance or per-build factory —,
  `Override<T, TImpl>`, `Configure`, `ObserveWith`,
  `WithStartupTimeout`, captured `Log`), `LifecycleFake` (+ `LifecycleFakeHandle`, `FakeBehavior`,
  `FakeInvocationLog`, `FakeDispatchProxy`), `CollectingObserver`.
- `RuntimeFlow.Editor`: `RuntimeFlowDashboardWindow` — Graph / Scopes / Last run tabs, session restart,
  diagnostics JSON and `Describe()` export.
- `scripts/check_package_namespaces.sh` and `scripts/check_docs_types.sh`, wired into a Unity-free
  package gate that runs on every push and pull request.
- `docs/DESIGN.md` — the one-graph model, scheduler guarantees and the exact message catalogue.

### Fixed

Found by the pre-release review of the restart, scheduler, graph and host code; each has a regression
test.

- **Restart chains.** A restart requested before a session exists (during the global phase) makes the
  startup build its first session directly as the restart instead of waiting for a generation 0 that
  nothing would cancel — a session service that then requested a second restart and parked on its
  token hung forever. A dropped request (the global phase failed) is not budgeted and lets the host be
  started again. A restart whose session fails to build (installer, entry point or graph error) leaves
  the host restartable, with `State == Failed`, the error in `GetStatus().Error` and a `Session` getter
  that says so; before, every later request coalesced into the failed one forever.
- **Doomed generations.** Accepting a restart freezes the running session synchronously, so no
  dependent of the requesting service starts in the generation being replaced; its tokens are still
  cancelled only after a yield.
- **Restart tokens.** The requesting service's own token (any token of the run being torn down) is
  ignored with a warning instead of cancelling the new generation at birth; an already cancelled token
  yields a cancelled task and tears nothing down. A request from the global scope warns that global
  services are not rebuilt and must not wait on their token.
- **Dispose and quit during a chain.** A startup or restart stops after any await once the host is
  disposed or the application quits, instead of building a session on a disposed global;
  `DisposeAsync` cancels and waits (bounded) for the chain, and concurrent calls share one teardown.
  `State` is `Running` during a restart's teardown instead of `Disposed`.
- **Budget.** Refused requests no longer consume the restart budget; `RestartWindow <= 0` is documented
  as a lifetime budget.
- **Scheduler.** Several timeouts in one tick are all reported as timeouts, and a service that returns
  from its own timeout cancellation can no longer complete (or doubly release) a timed-out node; every
  node transition is guarded. Teardown waits only for services still in flight, and `CancelAsync` /
  `DisposeAsync` wait for a run that is already halting or failing. Required construction failures are
  all reported before anything starts; an optional one degrades in graph order. `InitContext.Halt` and
  `ReportProgress` are marshalled to the run's context; a run without a `SynchronizationContext` is
  refused; a caller cancellation landing after completion is ignored. `CancellationGrace` accepts
  `Timeout.InfiniteTimeSpan` (and huge values) instead of hanging teardown, a negative grace is
  rejected, and a failing watch tick no longer silently ends timeouts. A cancelled run is logged and
  reported through the new `OnRunCancelled`.
- **Edges follow VContainer.** Constructor selection mirrors VContainer exactly (equal-arity
  constructors without `[Inject]` warn instead of being tie-broken differently from VContainer);
  `[Inject]` methods, fields and properties are edges; a single parameter depends on the registration
  VContainer picks, not on every assignable class (no more invented edges or false cycles); only
  `IEnumerable<T>`/`IReadOnlyList<T>` are barriers; dependencies through plain registrations are
  followed transitively (`ctor: IProfileApi api via ProfileApi`); `WithParameter` values and
  `ContainerLocal<T>` are handled.
- **Graph building.** A throwing constructor is reported with its own exception (no
  `TargetInvocationException` text, no "register the missing type" advice); a graph error releases the
  services and the owned scope it already constructed; `RegisterComponentInHierarchy` (always Scoped)
  is accepted; duplicate phase names are rejected; huge timeouts no longer overflow.
- **Host.** The entry-point exception handler collects only while a scope is built — later player-loop
  exceptions are logged, not swallowed — and a consumer handler in the global installer also covers the
  session; the failing entry point is named correctly when a helper threw for it. A throwing
  `Dispose()` no longer aborts teardown or wedges a restart. Child scopes see every ancestor run
  (grandchildren), are refused while a parent is not `Completed` or a restart is in flight, reject the
  host's own scopes, repeated and stale-session scopes, are forgotten after their teardown finishes, and
  a child of Global survives session restarts. A global supplied through `From` no longer has its
  services disposed by the host, and a failed one cannot be re-initialized.
- **Testing.** `TestFlow.StartAsync` reports a caller cancellation as a cancellation, not a timeout;
  `LifecycleFake` without a stub returns completed tasks.

### Fixed (second review round)

Found by a second review and by the restart chaos fixture; each has a regression test, and the suite
now also runs against the VContainer 1.19 fork sfs-client ships.

- **A halt in the global scope stops the startup.** Before, the host ignored it and built the session on
  global services the halt had skipped, which the session saw as initialized. Now no session is built:
  `StartAsync` returns the global `Halted` result, `State`/`GetStatus()` report `Halted` with the reason
  and the halting service (new `RuntimeFlowStatus.HaltedBy`), `Session` throws explaining the halt, and
  `RestartAsync` is refused with `InvalidOperationException` — the global scope is never rebuilt, so
  starting over means a new host. External nodes keep the real state of their parent node (read again
  when the child run starts): a child service depending on a parent service that never initialized fails
  instead of starting.
- **Disposal and quitting.** `DisposeAsync` freezes every run — every child run included — before it
  awaits anything, so no service of any scope starts once it was called (an older child used to keep
  starting services while a newer one was disposed), and cancels them only after a yield, so a service
  may call it from its own `InitializeAsync`. `Global` and `Session` throw `ObjectDisposedException`
  afterwards. A quit also cancels the child runs (one frozen by a restart the quit aborted hung its
  awaiter), refuses new child scopes, and a quit that tears the session down leaves `State == Cancelled`
  with a `Session` getter that says so instead of reporting the global run's `Completed`. Iterations over
  the child runs work on snapshots (a cancellation callback disposing another child threw "Collection was
  modified"). A child scope whose constructor requests a restart is refused and disposed instead of
  starting on the doomed session. A restart folded into a chain that then fails before honouring it gives
  its budget entry back.
- **Caller-owned containers.** A graph error of a container the run does not own (`RuntimeFlowHost.From`,
  `ScopeRun.Create` without `ownsScope`) no longer disposes the caller's singletons. `[Init]` values are
  validated before anything is constructed.
- **Scheduler robustness.** A cancellation callback that throws (`ct.Register(() => request.Abort())`)
  is logged instead of escaping: halts and failures still settle, a timed-out service's dependents are
  still released, and disposal still reaches every service and the scope. A logger that throws can no
  longer wedge a run (every log call is contained; the run's task completes in a `finally`). A frozen run
  never finishes on its own, so a generation doomed by a restart can no longer report `Completed` over a
  service that never initialized, and no run reports `Completed` while a service is `Cancelled`.
  Failures while a halt or cancellation settles are logged as warnings. Progress reported after a
  service finished is ignored. A timed-out service still inside its `InitializeAsync` is waited for
  (bounded by the grace) before it is disposed. Observers are notified of a failure only after the run
  is failing, so one that cancels the run from `OnServiceFailed` cannot hide the failure. The observer
  set is snapshotted by `RunAsync`, not by `ScopeRun.Create`. `InitContext.DegradedServices` is an
  immutable snapshot per read (enumerating it across an `await` threw "Collection was modified").
  `GetStatus()` shares dependency-name lists and terminal snapshots instead of allocating them per poll.
  Scope disposal retries are bounded at 64 attempts, with a diagnostic when the bound is exhausted;
  distinct disposables throwing the same exception object still allow teardown to continue, and
  identical failures are logged once plus a count. A re-entrant `ScopeRun.DisposeAsync` joins the teardown in flight.
- **Entry points on VContainer 1.19.** The fork's `EnsureDispatcherRegistered` registers
  `Debug.LogException` as the handler of any scope without one, which made session `IInitializable`
  failures (and global ones, with `UseEntryPoints`) log instead of failing the build. The host now
  registers its collector in every scope it composes, before the installer; a consumer handler still
  supersedes it (its entry is replaced, since 1.15.3 refuses two), a consumer handler of a parent scope
  stays in charge of the session, and a parent handler that is exactly `Debug.LogException` is not
  mistaken for a consumer's.
- **Graph discovery.** A walk through plain registrations yields each node once and is memoised per
  registration and effective construction scope, so diamond lattices no longer cost their number of paths; edge de-duplication is O(1).
  `[Key(x)]` members (1.19 fork) depend on the keyed registration. A lookup that throws while edges are
  derived is no edge, leaving the construction error in charge. Instance and factory registrations no
  longer get edges from `[Inject]` members VContainer never injects into them (false cycles). A child
  service injecting a parent's `RegisterComponentInHierarchy` component — Scoped on 1.15.3, so VContainer
  would re-inject the scene component into the child and dispose it with it — is a graph error.
  Inherited singleton construction follows the actual resolving scope; recreated parent services are
  rejected before child construction. Injected overloads retain distinct keyed edges, and local
  collections exclude parent singleton elements the same way VContainer does.
- **Documentation.** The README no longer calls any fork of 1.15.3's API a safe drop-in; it lists the
  patterns that end the transitive walk and need `[DependsOn]` (factories, instances, the fork's
  `RegisterFromResolve`, delegate `WithParameter`), and the cases that wait forever with an infinite
  `CancellationGrace`.
- **Test infrastructure.** `scripts/run_unity_editmode_tests.sh` takes `RUNTIMEFLOW_VCONTAINER=fork`
  (temporary manifest swap, restored on every exit path), `RUNTIMEFLOW_TEST_CATEGORY`, and
  `RUNTIMEFLOW_CHAOS_BATCHES` for the opt-in chaos sweep; the regular EditMode run keeps a fast chaos
  subset. A stale results file can no longer be reported as the current run's. Invalid explicit chaos
  ranges, missing results and runs that execute no tests fail. Disposing child runs remain tracked until
  completion, while weak scope identity rejects their reuse afterwards; host status preserves `HaltedBy`.

### Removed

Everything below existed in 0.x and no longer exists. Nothing is `[Obsolete]`; there are no shims.

- **Pipeline family**: `RuntimePipeline` (and its `Configuration`, `FlowExecution`, `Guards`,
  `OperationExecution`, `ScopeOperations`, `StatusLifecycle` partials), `RuntimePipelineOptions`,
  `RuntimePipelinePresets`, `RuntimePipelineBootstrapHost`, `BootstrapResult`,
  `IRuntimeFlowPipelineProvider`, `RuntimeFlowPipelineProvider`, `RuntimeFlowRunner`,
  `RuntimeFlowServiceResolver`, `RuntimeFlowPrioritizedInitializableResolver`, `RuntimeOperationCodes`,
  `RuntimePipelineStateReasonCodes`, `PipelineOperationExecutor`, `PipelineStatusService`, and the
  ambient `ActivePipeline` static.
- **Installer modules and options**: `RuntimeFlowInstallerModules` (+ its `EntryPoints` and
  `LoadingAndRestart` partials), `RuntimeFlowInstallerModuleOptions`,
  `RuntimeFlowLoadingRestartInstallerOptions`, `RuntimeFlowGlobalInstallerOptions`,
  `RuntimeFlowGlobalBootstrapPresetOptions`, `RuntimeFlowSessionInstallerOptions`,
  `RuntimeFlowSessionBootstrapInstallerOptions`, `RuntimeFlowSessionBootstrapPresetOptions`,
  `RuntimeFlowStartupBootstrapOptions`, `RuntimeFlowVContainerEntryPointsInstallerOptions`,
  `RuntimeFlowSessionVContainerEntryPointsInstallerOptions`,
  `RuntimeFlowVContainerEntryPointsSettings`, `RuntimeFlowVContainerEntryPointsSettingsContribution`,
  `RuntimeFlowVContainerEntryPointsInitialization`, `RuntimeFlowVContainerInitializableRunner`,
  `IRuntimeFlowSessionSyncEntryPointsBootstrapService`.
- **Stage state**: `IRuntimePipelineStageStateProvider`, `IRuntimePipelineStageStateQuery`,
  `IRuntimePipelineStateProvider`, `IRuntimePipelineStateQuery`, `IRuntimePipelineStageSnapshotObserver`,
  `RuntimePipelineStageState`, `RuntimePipelineStageSnapshot`, `RuntimePipelineStageStateStore`,
  `RuntimePipelineStringStageStateProvider`, `RuntimePipelineStringStageStateProviderOptions`.
- **Scenarios, flows and presets**: `IRuntimeFlowScenario`, `IRuntimeFlowContext`,
  `RuntimeFlowPresets`, `StandardSessionScenario`, `StandardSessionFlowBuilder`,
  `InitializeOnlyScenario`, `EnsureSceneLoadedThenInitializeScenario`,
  `RestartAwareSceneBootstrapScenario`, `IRestartAwareSceneBootstrapScenario`,
  `RestartAwareSceneBootstrapScenarioOptions`, `SceneRoute`, `SceneRouteDecisionContext`,
  `ISessionSceneRouteResolver`, `IGameSceneLoader`, `IGameSceneLoaderWithProgress`,
  `UnityGameSceneLoader`, `NoOpSceneLoader`, `SceneLoaderProgressBridge`,
  `GameSceneLoadProgressSnapshot`, `RuntimeFlowSceneUtilities`.
- **Golden Path**: `GameFlow`, `GameHandle`, `EntryRoute`, `IEntryRouteResolver`,
  `FlowNotConfiguredException`.
- **Content**: `ContentSource`, `IContentSource`, `IContentSourceInfo`, `DelegateContentSource`,
  `ContentSourceRegistrationExtensions`, `ContentPolicy`, `ContentPlanEntry`, `FlowLoadContext`.
- **Context and builder**: `GameContext`, `GameContextBuilder` (and all its partials),
  `IGameContext`, `IGameContextBuilder`, `IGameScopeRegistrationBuilder`, `IScopeInstaller`,
  `GameContextContainerBuilder`, `GameContextCore`, `GameContextInstanceLedger`,
  `GameContextRegistrationStore`, `GameContextScopeRegistry`, `GameContextScopeProfileStore`,
  `GameContextDecorationChain`, `GameContextDeferredRegistrationQueue`,
  `GameContextLazyInitializationRegistry`, `GameContextThreadDispatcher`, `GameContextType`,
  `ExplicitTypeCatalogProvider`, `DiLifetime`, `DiLifetimeMapper`, `ActiveScopeState`,
  `ScopeLifecycleState`, `ScopeNotDeclaredException`, `ScopeNotInitializedException`,
  `ScopeNotRestartableException`, `ScopeRegistrationException`, `RuntimeScopeRestartabilityPolicy`.
- **Lifecycle marker families** (scope × stage × trait): `IGlobalInitializableService`,
  `ISessionInitializableService`, `ISceneInitializableService`, `IModuleInitializableService`,
  `IStartupStageInitializableService`, `IPreBootstrapStartupInitializableService`,
  `IPlatformStartupInitializableService`, `IContentStartupInitializableService`,
  `ISessionStartupInitializableService`, `IUiStartupInitializableService`,
  `IUserInteractionGatedInitializableService`, `ILazyInitializableService`,
  `IProgressAwareInitializableService`, `IAsyncInitializableService`, `IGlobalBootstrapOperation`,
  `IStartupOperationContext`, `IServiceInitializationContext`, `ServiceInitializationContext`,
  `InitializationContractCatalog`.
- **Disposal and activation families**: `IAsyncDisposableService`, `IGlobalDisposableService`,
  `ISessionDisposableService`, `ISceneDisposableService`, `IModuleDisposableService`,
  `IAsyncScopeActivationService`, `ISessionScopeActivationService`, `ISceneScopeActivationService`,
  `IModuleScopeActivationService`, `ScopeActivationService`, `ScopeDisposalService`,
  `ScopeInitializationService`, `ScopePreloadService`, `ScopeTransitionService`,
  `ScopeOperationCoordinator`, `ScopeCleanupFailures`, `ScopeLifecycleDependencies`,
  `RuntimeLifecycleOrchestrator`.
- **Guards and transitions**: `IRuntimeFlowGuard`, `RuntimeFlowGuardStage`, `RuntimeFlowGuardContext`,
  `RuntimeFlowGuardResult`, `RuntimeFlowGuardFailedException`, `IScopeTransitionHandler`,
  `NullScopeTransitionHandler`, `ScopeTransitionContext`.
- **Event bus**: `IScopeEventBus`, `ScopeEventBus`, `IScopeEvent`, `EventPropagation`.
- **Scene and module scopes**: `ISceneScope`, `IModuleScope` and the `Global -> Session -> Scene ->
  Module` hierarchy. 1.0 has Global, Session and any child scope the consumer creates.
- **Health, retry and error classification**: `RuntimeHealthOptions`, `RuntimeHealthSupervisor`,
  `RuntimeHealthStatus`, `RuntimeHealthAnomaly`, `RuntimeHealthEvaluation`,
  `RuntimeHealthCriticalException`, `IRuntimeHealthObserver`, `IRuntimeHealthEvaluator`,
  `IRuntimeHealthBaselineStore`, `InMemoryRuntimeHealthBaselineStore`, `RuntimeServiceHealthMetric`,
  `RuntimeRetryPolicyOptions`, `RuntimeRetryDecision`, `IRuntimeRetryObserver`, `RuntimeErrorPolicy`,
  `RuntimeErrorKind`, `RuntimeErrorClassification`, `IRuntimeErrorClassifier`.
- **Restart machinery**: `RuntimeRestartCoordinator`, `IRuntimeRestartCoordinator`,
  `RuntimeRestartCoordinatorRequest`, `RuntimeRestartRequest`, `RuntimeRestartDispatch`,
  `RuntimeRestartDispatchKind`, `RuntimeRestartDuplicateReason`, `RuntimeRestartExecutionOutcome`,
  `RuntimeRestartExecutionResult`, `RuntimeRestartLifecycleManager`,
  `IRuntimeRestartLifecycleManager`, `RuntimeRestartLifecycleSnapshot`, `RuntimeRestartLifecycleStage`,
  `RuntimeRestartStageProjector`, `RuntimeRestartStageProjectionOptions`, `RuntimeRestartReadiness`,
  `IRuntimeRestartReadinessProvider`, `IRuntimeRestartGuard`, `RuntimeRestartGuardContext`,
  `RuntimeReadinessGate`, `IRuntimeReadinessGate`, `RuntimeReadinessStatus`,
  `IGameRestartHandler`, `RuntimeFlowGameRestartHandler`, `IGameRestartStateSaver`,
  `IGameDataCleaner`, `ISessionRestartAware`, `IRuntimeSessionRestartPreparationHook`,
  `RuntimeSessionRestartPreparationContext`, `RuntimeFlowReplayScope`.
- **Loading, progress and status**: `RuntimeLoadingOperationSnapshot`, `RuntimeLoadingOperationKind`,
  `RuntimeLoadingOperationStage`, `RuntimeLoadingOperationState`,
  `IRuntimeLoadingProgressObserver`, `RuntimeLoadingProgressNotifierAdapter`,
  `IRuntimeScopeLifecycleProgressNotifier`, `IInitializationProgressNotifier`,
  `CompositeInitializationProgressNotifier`, `NullInitializationProgressNotifier`,
  `IWeightedInitializationProgressNotifier`, `IStartupOperationProgressNotifier`, `IUserGate`,
  `IUserGateProgressNotifier`, `UserGate`, `UserGateProgressExtensions`, `RuntimeStatus`,
  `IRuntimeExecutionContext`, `IRuntimeExecutionContextProvider`, `RuntimeExecutionContextManager`,
  `RuntimeExecutionContextSnapshot`, `RuntimeExecutionPhase`, `RuntimeExecutionState`.
- **Planning and scheduling internals**: `LoadGraphBuilder`, `LoadGraphNode`, `LoadGraphTopology`,
  `LoadNodeWeight`, `InitializationGraphResolver`, `InitializationExecutionPolicy`,
  `InitializationGraphRules`, `DependencyCycleDetector`, `IInitializationExecutionScheduler`,
  `IInitializationThreadAffinityProvider`.
- **Roslyn source generator**: the `RuntimeFlow.Generators` and `RuntimeFlow.Generators.Tests`
  projects, the shipped analyzer payload, the opt-in
  `[assembly: GenerateRuntimeFlowInitializationGraph]` attribute and the diagnostics `RF0001`
  (duplicate implementation), `RF0002` (missing dependency), `RF0003` (scope violation) and `RF0004`
  (circular dependency). Graph validation now happens at run time, in `ScopeRun.Create`, with messages
  that name the services involved.
- **VContainer fork dependency**: `Bezarius/VContainer#1.15.3.1` is replaced by upstream
  `hadashiA/VContainer` pinned to the 1.15.3 tag by SHA, in both the package manifest and the Unity
  test project. `docs/VCONTAINER_FORK.md` and `docs/CONTENT_AND_PLATFORM_SOURCES.md` are gone.
- **Test harness**: `TestPipeline`, `StaticContent`, `NoopSceneLoader`,
  `CollectingLoadingProgressObserver`. Replaced by `TestFlow` and `CollectingObserver`;
  `LifecycleFake` survives with the new `(InitContext, CancellationToken)` signature.
- **Repository scaffolding**: `RuntimeFlow.sln`, `Directory.Build.props`, the generator CI job and the
  duplicate `scripts/run_unity_playmode_tests.sh` (the EditMode script takes `playmode` as an argument).

### Migration

- Replace every lifecycle marker interface with `IAsyncInitializable` and move the stage information
  into `[Init(Phase = "…")]` plus `RuntimeFlowOptions.Phases`, or delete it: a service that simply
  takes its dependency as a constructor parameter needs no phase at all.
- Replace `RuntimePipeline` / `GameFlow` / `RuntimePipelineBootstrapHost` startup with a single
  `RuntimeFlowHost(globalInstaller, sessionInstaller, options)`; installers are now plain
  `Action<IContainerBuilder>` receiving the real VContainer builder, so `IGameScopeRegistrationBuilder`
  registrations become ordinary `Register`/`RegisterInstance` calls.
- Replace health timeouts and retry policies with `[Init(TimeoutSeconds = n)]` where a deadline is
  genuinely wanted, and handle retry inside the service — there is no default watchdog to disable.
- Replace `IGameRestartHandler` and the restart lifecycle hooks with `RuntimeFlowHost.RestartAsync`
  plus `InitContext.IsRestart`; ordered teardown is now `IAsyncDisposable.DisposeAsync` in reverse
  completion order.
- Replace `TestPipeline` with `TestFlow`, which runs the production installers and filters overridden
  registrations out of them; `Assert`-level behaviour is unchanged, but a mistyped override now fails
  the test by name.

### Notes

- `Runtime/Plugins/Microsoft.Extensions.Logging.Abstractions.dll.meta` keeps `validateReferences: 0`
  on purpose: the assembly references Microsoft.Extensions.DependencyInjection.Abstractions, which
  RuntimeFlow does not use and does not ship, and Unity would otherwise flag it as a missing reference.
  The dead `Assets/Packages/Microsoft.Extensions.DependencyInjection.Abstractions*` copy in the Unity
  test project has been deleted.
- The same importer carries the define constraint `!RUNTIMEFLOW_EXTERNAL_MEL`: a project that already
  ships Microsoft.Extensions.Logging.Abstractions (NuGetForUnity) defines `RUNTIMEFLOW_EXTERNAL_MEL` and
  RuntimeFlow compiles against that copy instead of failing with duplicate precompiled assemblies.

## [0.9.0] - 2026-08-22

### Added
- **`RuntimeFlow.Testing` harness** (test-only assembly, stripped from player builds via
  `UNITY_INCLUDE_TESTS`): addresses the framework's second design goal — fast runtime tests
  with overridable dependencies.
  - `TestPipeline.Create(...).Override<TService,TOverride>() / Override(fakeInstance) /
    StartAsync()`: deterministic defaults (inline scheduler, health/retry off), fluent
    overrides with post-build verification (a shadowed or typo'd override fails the test
    descriptively), ambient activation (`Activate()` sets/clears `ActivePipeline`).
  - `LifecycleFake.Of/OfHandle<TService>()`: DispatchProxy-based fakes with lifecycle fault
    injection — `FailInitializeAttempts(n)`, `FailDisposeAttempts(n)`, delays, full call log.
    Instance overrides are automatically exposed under every lifecycle contract their runtime
    type implements, so instance fakes participate in startup discovery exactly like
    type-registered services.
  - Shipped test doubles: `NoopSceneLoader`, `CollectingLoadingProgressObserver`.
- **Golden Path (`GameFlow`)** — intent vocabulary for a typical mobile startup:
  `Config / Auth / Profile / Catalog / Scene / Entry / LoadingUi`, class- or delegate-based
  sources, explicit failure policies, entry-scene auto-load, deterministic scheduler for
  tests and `StartupTimeout` watchdog. Startup plan inspection via
  `DescribeStartupPlan()` (nodes, edges, origins) with loud validation of unregistered
  content dependencies at composition time.
- **`RuntimeFlow.Content` — universal content/config/platform-source primitive**
  (`ContentSource<TData>`): one small subclass per concern (remote config, Addressables-style
  catalogs, game-service authentication) plus a one-line registration
  (`builder.Global().Content<TSource,TData>()`). Inherits wave scheduling, health timeouts,
  retries, progress and cancellation from the pipeline; session-stage placement via the
  existing `IPlatformStartupInitializableService` / `IContentStartupInitializableService`
  markers; sign-in dialogs exempt from the watchdog via
  `IUserInteractionGatedInitializableService`. Per-source failure policy: required sources
  fail startup, optional sources degrade to `FallbackData` with `UsedFallback` marking.
  Design notes and platform sketches: `docs/CONTENT_AND_PLATFORM_SOURCES.md`.

### Breaking changes
- **Synchronous cross-thread resolution removed.** `Resolve<T>()` / `Resolve(type)` /
  `Resolve(registration)` from a background thread (with a captured main-thread
  `SynchronizationContext`) now throw `InvalidOperationException` pointing at
  `await ResolveAsync<T>()`. Previously such calls blocked the worker against the main thread
  (sync-over-async with an internal 2-minute timeout) and could deadlock. The blocking
  dispatch path no longer exists. Headless environments without a captured context (EditMode
  tests) keep the inline fallback with a one-time warning.
- `Initialize()` is now documented and implemented as thread-agnostic: it only builds the
  registration graph (pure C#); Unity-bound construction happens at resolve time, which
  enforces the main-thread contract above.

### Changed
- Internal startup paths no longer rely on blocking dispatch:
  - seeded state for child scopes reads initialized instances straight from the instance
    ledger (no construction, no dispatch; a missing instance is now a loud invariant error);
  - async-initializer wave construction and lazy bindings resolve via the async dispatch path;
  - VContainer entry-point settings/contributions and global bootstrap operations resolve
    through `ResolveAsync` during plan building; cancellation now propagates through plan
    building instead of being dropped;
  - **decorated services materialize lazily at resolve time** instead of eagerly at context
    initialize — the pipeline builds contexts on worker continuations, so eager decoration
    would have thrown under the new contract; chained decorators keep their registration
    order (pinned by PlayMode coverage);
  - auto-service parent fallbacks read the parent's instance ledger directly instead of
    dispatching a synchronous resolve.

### Testing
- PlayMode 7 → 10 tests: worker-thread `Resolve` throws with actionable guidance;
  `ResolveAsync` from a worker still constructs on the Unity main thread; decorated services
  resolve correctly across worker-built contexts; parent-ledger reads work without dispatch.
- Editor dashboard: scope-hierarchy view tolerates live preloaded/additive dictionaries that
  mutate during preload/unload operations (skips the card until the next refresh instead of
  throwing inside the editor update loop).
- Test project drops the unused Microsoft.Extensions.DependencyInjection.Abstractions
  assembly and its asmdef references.

## [0.8.0] - 2026-08-21

### Breaking changes
- **Namespaces now follow folder structure.** The 66 files under the runtime pipeline
  subsystems leave the flat `RuntimeFlow.Contexts` namespace:
  `RuntimeFlow.Pipeline`, `RuntimeFlow.Flow`, `RuntimeFlow.Health`,
  `RuntimeFlow.Loading`, `RuntimeFlow.Status`, `RuntimeFlow.Errors`,
  `RuntimeFlow.Transitions`. `RuntimeFlow.Contexts` remains the home of contexts, DI,
  scopes, and initialization contracts. Consumer fix is mechanical: add the matching
  `using` next to the existing `using RuntimeFlow.Contexts;`.
  Generator-emitted code (`RuntimeFlow.Contexts.Generated.*`) is unchanged.

### Changed
- **`ScopePreloadService`** extracted from `GameContextBuilder` — preload/additive-module
  cores live in a service with explicit collaborators; the builder keeps thin public
  methods owning only generation gating.
- **Wave scheduler flattened**: the ~6-level nested initialization loop in
  `ScopeInitializationService` is decomposed into named phases (`CollectReadyServices`,
  `ThrowIfDependencyCycle`, `RunWaveAsync`, ...).
- `RuntimePipeline.ActivePipeline` intentionally kept as a public-read/internal-write
  static: its only consumer is the Editor dashboard bridge; a registry would add API
  surface without a second consumer.

### Testing
- PlayMode suite grown 4 → 7 tests: worker-thread Resolve constructs on the Unity main
  thread, restart exit hooks run on the main thread, worker-initiated pipeline disposal
  completes without deadlock.

## [0.7.0] - 2026-08-21

### Breaking changes
- **`DiLifetime` replaces `VContainer.Lifetime` in RuntimeFlow registration APIs**
  (`IGameContext.Register`, `IGameScopeRegistrationBuilder.Register`). The VContainer type no
  longer leaks through the contract; use `RuntimeFlow.Contexts.DiLifetime.Singleton/Transient/Scoped`.
- **Removed `GameContextBuilderExtensions`** (`WithScene<T>()`, `WithModule<T>()` sugar) —
  unused 1:1 duplicates of `IGameContextBuilder.Scene<T>()` / `.Module<T>()`.
- **Removed the `Runtime/Runtime/Lifecycle` subsystem** (`LifecycleStateEngine`,
  `ILifecycleTransitions`, `NullLifecycleTransitions`, `ILifecycleSnapshotObserver`) — dead code,
  zero references since introduction.
- **Removed internal shims**: `GenerationGate` (use `ScopeOperationCoordinator`),
  dead `ScopeLoadingService`/`ScopeTransitionService` chain, nested `ScopeTransitionEngine`.

### Fixed
- **Broken build baseline restored**: the branch did not compile before this release
  (duplicate `Register` overload, missing `CreateAndInitializeScopeContextAsync`, wrong
  `setState` delegate signature, `ValueTask`/`Task` mismatch in scope disposal).
- Scope contexts created during initialization now receive the execution scheduler —
  async-disposable services no longer fail teardown with "An ExecutionScheduler is required".
- Session restart / re-initialization now runs **activation exit hooks** (module → scene →
  session) before tearing down scopes; previously exit hooks were skipped on those paths.
- `RuntimePipeline.DisposeAsync` logs scope-teardown failures instead of swallowing them.
- Editor dashboard bridge logs swallowed exceptions instead of silent defaults.
- `IsObjectDisposedFailure` classifies by exception type, not by message string matching.
- `GameContextThreadDispatcher` warns once when no main-thread context was captured and work
  falls back to the calling thread (latent wrong-thread hazard is now observable).

### Changed
- **DI core**: dual instance ledgers merged into a single chronological
  `GameContextInstanceLedger`; teardown walks reverse order so dependents dispose first.
  Full delegation to VContainer's `Container` was evaluated and rejected: it provides neither
  reverse-initialization-order async disposal nor registration-keyed instance lookup, both
  required by RuntimeFlow's lifecycle-native contracts.
- **21-parameter scope initialization** collapsed into a `ScopeLifecycleDependencies`
  collaborator bundled once per owner.
- Teardown helpers (`CaptureCleanupFailuresAsync`, `CreateCleanupAggregate`,
  `FilterCancellationFailures`, `DisposeAndClearEventBuses`) consolidated into
  `ScopeCleanupFailures` (were duplicated across builder/orchestrator/disposal service).
- `ScopeTransitionEngine` extracted from `GameContextBuilder` internals into a standalone
  `ScopeTransitionService` with explicit collaborators; builder disposal partials slimmed to
  thin delegations (~480 lines removed overall).

### Packaging
- `Microsoft.Extensions.Logging.Abstractions.dll` now ships inside the package
  (`Runtime/Plugins/`); previously consumers had to place it manually. Added
  `Third Party Notices.md`.
- Added `docs/VCONTAINER_FORK.md`: pin policy for the Bezarius VContainer fork, the VContainer
  internals RuntimeFlow relies on, and an upgrade checklist.

### Testing / CI
- Unity EditMode suite grown to **268 tests**, all green; PlayMode suite runs in CI too.
- CI: Unity EditMode + PlayMode jobs run nightly and via manual dispatch; PRs keep .NET-only
  gates unless `RUNTIMEFLOW_RUN_UNITY_TESTS=1`.
- New coverage: Editor dashboard bridge (`RuntimePipelineEditorBridgeTests`), flow scenarios
  and presets behavior (`RuntimeFlowScenarioTests`).
- `scripts/run_unity_editmode_tests.sh`: auto-selects the editor matching
  `ProjectVersion.txt`, supports `playmode`, distinguishes skipped tests, uses absolute
  result paths, fails loudly when Unity produces no results XML.

## [0.6.0] - 2026-08-21

### Added
- **UI Toolkit Dashboard (`RuntimeFlow.Editor`)**: Full-featured diagnostic window (`Window → RuntimeFlow → Dashboard & Graph`) with live PlayMode monitoring, DAG inspector, scope tree, and fault telemetry.
- **Interactive Demo Scene & Chaos Testing (`Assets/Demo`)**: Complete demonstration showcasing Global, Session (Stages 1-4), Scene, Module, and Additive Scopes with live fault and timeout injection.
- **`ResolveAsync<TService>()` on `IGameContext`**: Safe async DI resolution for background worker threads without sync-over-async blocking.
- **Fluent Scope Registration DSL**: Extension methods `WithScene<T>()`, `WithModule<T>()`, and `WithTransition<T>()` on `IGameContextBuilder`.
- **IL2CPP Code Preservation in Source Generator**: Automatic generation of `PreserveTypes()` and `PreserveCollection<T>()` with `[UnityEngine.Scripting.Preserve]` annotations in `RuntimeFlowGeneratedCatalog.g.cs`.
- Regression tests covering registration-store lifetime/ownership semantics, resolver-backed
  `IsRegistered`, guaranteed scope teardown, non-blocking bootstrap disposal, and session
  restart recovery after a failed deactivation hook.

### Changed
- **Zero-Reflection Decorators**: `GameContextDecorationChain` now compiles and caches direct factory delegates via `System.Linq.Expressions` with AOT fallback.
- **Scope Plan Caching**: `ScopeActivationService` now caches discovered `ScopeActivationExecutionPlan` instances using `ConditionalWeakTable` + `ConcurrentDictionary`.
- **Hot-Path Allocations & LINQ Optimization**: Replaced LINQ queries with indexed loops and reusable collections across `GameContextBuilder` initialization and service discovery.
- **Canonical Restart Contracts**: Removed legacy SFS namespaces; framework restart contracts are now canonically in `RuntimeFlow.Contexts` (`IGameRestartHandler`, `IGameDataCleaner`, `ISessionRestartAware`, `IGameRestartStateSaver`).

### Fixed
- Fixed `IsRegistered` constructing services during registration queries: checks now use the
  container registration table and never instantiate the service.
- Fixed double-disposal of scope-owned `RegisterInstance` services that were already resolved
  through the container. Instances spawned by VContainer are now left to the container for
  disposal, both during scope teardown and when an instance registration is replaced.
- Fixed instance registration replacement: re-registering the same implementation type now
  updates its lifetime (last registration wins) and disposes the replaced owned instance
  instead of silently keeping the first registration.
- Fixed scope teardown after a failed deactivation hook: teardown now always completes, the
  active-scope reference is always cleared, and failures are aggregated into an
  `AggregateException`. Cancellation-driven (superseded) transitions still surface as
  `OperationCanceledException`.
- Fixed `BootstrapResult.Dispose()` blocking the Unity main thread: disposal is now
  non-blocking on the main thread (synchronous on worker threads).
- `RegisterInstance` now validates eagerly that the exposed service types are assignable from
  the instance type, consistent with `Register`.
- `RestartSessionAsync` now clears active-scope references even when teardown fails, so a
  failed restart never leaves a stale disposed session context.
- Declared the VContainer dependency in `package.json` so UPM resolves it automatically.

## [0.5.0] - 2026-07-13

### Added
- Added `IUserInteractionGatedInitializableService` marker for async-init services whose `InitializeAsync`
  legitimately blocks on user/player interaction (consent, migration, progress choice, or any modal dialog)
  for an unbounded time. `RuntimeHealthSupervisor.GetServiceTimeout` returns an infinite timeout for any
  service implementing it, so the pipeline is no longer torn down mid-dialog by the ~5s health watchdog. This
  replaces per-service `ServiceTimeoutOverrides` entries for interaction-gated services. The marker is
  marker-only, so any number of services may implement it without an initializer-discovery collision, and an
  explicit `ServiceTimeoutOverrides` entry still takes precedence.

## [0.4.0] - 2026-07-13

### Added
- Added per-assembly opt-in for compiled initialization graphs through `GenerateRuntimeFlowInitializationGraphAttribute`.
- Added source-generator and runtime support for explicit `[DependsOn]` dependencies, including concrete marker-only async initializers and VContainer entry-point completion markers.
- Added inherited VContainer `IInitializable`/`IStartable` interface discovery and independently configurable startable exclusions.
- Added additive entry-point settings contributions so test or child scopes can exclude production entry points without replacing preset settings.
- Added startup-time restart support that publishes the active pipeline before session async initialization and permits controlled restart replay while the runtime is initializing.

### Changed
- Compiled graph generation now runs only for assemblies explicitly marked with `GenerateRuntimeFlowInitializationGraphAttribute`.
- Compiled graph rendering can resolve inaccessible nested implementation types without emitting invalid direct `typeof(...)` references.
- Session initialization state is seeded from the recorded successful initialization ledger instead of rediscovering registrations from parent scopes.
- Restarted startup flows now wait for the replacement replay and report the original run as completed only after the restart lifecycle reaches `Completed`.
- The packaged analyzer now targets Roslyn 4.3 for Unity compatibility.

### Fixed
- Fixed scope-local VContainer entry-point discovery dropping explicitly supplied registration lists when their registrations originated in a parent resolver.
- Fixed VContainer entry-point completion markers being recorded but silently discarded from runtime and compiled dependency graphs.
- Fixed direct and inherited VContainer entry-point registrations not being deduplicated consistently.

## [0.3.1] - 2026-07-07

### Fixed
- Fixed marker-only async initializer discovery incorrectly using ordinary service interfaces as lifecycle keys, which could resolve unrelated non-async services such as `ISessionReset`.

## [0.3.0] - 2026-07-07

### Added
- Added first-class global bootstrap operations via `IGlobalBootstrapOperation`, executed after global VContainer `IInitializable` and before global async initializers.
- Added startup operation diagnostics with phase, scope, operation, step, detail, elapsed time, and exception context.
- Added Unity EditMode NUnit lifecycle coverage with real UPM VContainer instead of fake container shims.
- Added cancellation/failure diagnostics coverage for startup operations and loading snapshots.
- Added CI coverage for the packaged Roslyn analyzer payload to prevent stale analyzer DLLs from shipping.
- Added repo-relative RuntimeFlow Unity test package wiring for portable Unity test runs.

### Changed
- Moved VContainer `IStartable` execution after RuntimeFlow async initialization for each scope.
- Hardened global/session lifecycle tracking so parent/global initialized services can satisfy dependencies without suppressing local/session entry points.
- Session restart now reruns session `IInitializable`, session async initializers, and session `IStartable` while keeping global lifecycle state intact.
- Global bootstrap operations now run through the RuntimeFlow execution scheduler with main-thread affinity by default and health timeout supervision.
- Runtime loading progress now separates current startup operation from last startup operation, preserving completed operation step/detail without masking later hangs.
- Enabled C# nullable reference types throughout the package via `Runtime/csc.rsp`. All public APIs that could return `null` are now annotated with `?`; all parameters with `null` defaults are nullable. No public surface was renamed or removed; only nullability annotations were tightened. Consumers that were already correctly checking for `null` need no changes.
- `BootstrapResult` now implements `IAsyncDisposable`. The synchronous `Dispose()` remains as a fallback for callers that cannot await (e.g. Unity `OnDestroy`); prefer `await DisposeAsync()` when possible to avoid blocking on `Pipeline.DisposeAsync()`.
- Documented the `async void` contract of `InitializationExecutionPolicy.RunOnPostedContext` — exceptions are routed via `TaskCompletionSource` and never escape to the synchronization context's unhandled handler.

### Removed
- Removed legacy fake VContainer/runtime shims from the .NET runtime test path; runtime lifecycle behavior is now validated in Unity with the real package dependency.

### Fixed
- Fixed inherited/global async registrations being rediscovered as local session services.
- Fixed parent initialized state satisfying a same-key local dependency before the local service initialized.
- Fixed caller cancellation during global bootstrap being reported as a generic startup failure.
- Fixed completed startup operation snapshots losing their last reported step/detail.
- Fixed session restart clearing global scope lifecycle diagnostics.

## [0.1.0] - 2026-04-16

### Added
- Hierarchical DI scope system (Global → Session → Scene → Module)
- Scope installer pattern — scene and module scopes implement `ISceneScope` / `IModuleScope` with `Configure()` method
- Async service initialization DAG with topological ordering
- Compile-time dependency graph validation via Roslyn source generator
- Health supervision with configurable timeouts and auto-restart
- Event bus with Local, Bubble, and Broadcast propagation modes
- Runtime pipeline for game lifecycle management
- Flow scenario API for defining game startup sequences
- Scene loading abstraction with progress tracking
- Lazy initialization support
- Service decoration support
