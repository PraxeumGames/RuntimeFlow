# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

### Changed
- **Zero-Reflection Decorators**: `GameContextDecorationChain` now compiles and caches direct factory delegates via `System.Linq.Expressions` with AOT fallback.
- **Scope Plan Caching**: `ScopeActivationService` now caches discovered `ScopeActivationExecutionPlan` instances using `ConditionalWeakTable` + `ConcurrentDictionary`.
- **Hot-Path Allocations & LINQ Optimization**: Replaced LINQ queries with indexed loops and reusable collections across `GameContextBuilder` initialization and service discovery.
- **Canonical Restart Contracts**: Removed legacy SFS namespaces; framework restart contracts are now canonically in `RuntimeFlow.Contexts` (`IGameRestartHandler`, `IGameDataCleaner`, `ISessionRestartAware`, `IGameRestartStateSaver`).

## [0.5.0] - 2026-07-13
  non-blocking on the main thread (synchronous on worker threads).
- `RegisterInstance` now validates eagerly that the exposed service types are assignable from
  the instance type, consistent with `Register`.
- `RestartSessionAsync` now clears active-scope references even when teardown fails, so a
  failed restart never leaves a stale disposed session context.
- Declared the VContainer dependency in `package.json` so UPM resolves it automatically.

### Added
- Regression tests covering registration-store lifetime/ownership semantics, resolver-backed
  `IsRegistered`, guaranteed scope teardown, non-blocking bootstrap disposal, and session
  restart recovery after a failed deactivation hook.

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
