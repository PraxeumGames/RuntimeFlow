# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
