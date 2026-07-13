# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
