# Copilot Instructions — RuntimeFlow

## Project overview

RuntimeFlow is a Unity UPM package (`com.praxeum.runtimeflow`) that runs the asynchronous
initialization of a game's services on top of stock VContainer. Services implement one
interface, declare dependencies through their constructors, initialize concurrently as a DAG,
and fail transparently (one exception naming the service, what completed and what was blocked).

Repository layout:

- `com.praxeum.runtimeflow/` — the package. `Runtime/` (namespace `RuntimeFlow`),
  `Runtime/Internal/` (`RuntimeFlow.Internal`), `Runtime/Testing/` (`RuntimeFlow.Testing`,
  compiled only with `UNITY_INCLUDE_TESTS`), `Editor/` (`RuntimeFlow.Editor`, UI Toolkit dashboard).
- `RuntimeFlow.UnityTests/` — Unity 2022.3 project with the authoritative NUnit EditMode/PlayMode
  suite (`Assets/Tests`) and the demo (`Assets/Demo`) that exercises broken-flow cases.
- `scripts/` — `run_unity_editmode_tests.sh [playmode]`, `check_package_namespaces.sh`,
  `check_docs_types.sh`.
- `docs/DESIGN.md` — the one-graph model and failure semantics.

There is no `.sln`, no `dotnet` build and no source generator. Tests run only inside Unity.

## Build and test

```bash
UNITY_BIN=/Applications/Unity/Hub/Editor/2022.3.62f2/Unity.app/Contents/MacOS/Unity scripts/run_unity_editmode_tests.sh
UNITY_BIN=... scripts/run_unity_editmode_tests.sh playmode
scripts/check_package_namespaces.sh && scripts/check_docs_types.sh   # CI package gate, no Unity
```

## Architecture rules (do not violate)

- **One graph.** The initialization order is derived from VContainer registrations: constructor
  parameters that are other initializable services are edges; `[DependsOn(typeof(X))]` is a
  validated escape hatch; optional named phases add barrier edges. There is no central flow
  file, no stage-marker interfaces, no `GameFlow`-style vocabulary.
- **Stock VContainer only.** Never reference `VContainer.Internal`. The framework never wraps
  `IContainerBuilder`; consumers keep the full VContainer API.
- **Single-threaded.** Everything runs on the caller's `SynchronizationContext`. No
  `ConfigureAwait(false)`, no locks, no scheduler abstractions, no ambient statics holding
  runtime state (`FlowRegistry` is diagnostics-only, weak references).
- **Transparent failures.** Every failure names the service, scope, phase and elapsed time, lists
  completed and unfinished services, and keeps the original exception as `InnerException`.
  The default logger writes to the Unity console; `NullLogger` is never the default.
- **No compatibility shims.** Public surface is small (~25 types); namespaces follow folders.

## Key types

| Concern | Type | Location |
|---|---|---|
| Service contract | `IAsyncInitializable`, `InitContext` | `Runtime/` |
| Per-service policy | `[Init(Phase, Optional, UserGated, TimeoutSeconds, Weight)]`, `[DependsOn]` | `Runtime/` |
| Global + Session lifecycle, restart | `RuntimeFlowHost` | `Runtime/RuntimeFlowHost.cs` |
| One scope's graph | `ScopeRun` | `Runtime/ScopeRun.cs` |
| Graph build and validation | `GraphBuilder`, `ConstructorEdges` | `Runtime/Internal/` |
| Execution | `Scheduler`, `StallWatch` | `Runtime/Internal/` |
| Observability | `IRuntimeFlowObserver`, `RuntimeFlowStatus`, `Describe()` | `Runtime/` |
| Tests | `TestFlow`, `LifecycleFake`, `CollectingObserver` | `Runtime/Testing/` |

## Test conventions

- `[Test] public async Task` with `[Timeout(10000)]`; synchronise with `TaskCompletionSource`
  gates (`Support/ControlledService.cs`), never with `Task.Delay` races. Real time only in
  timeout/stall tests with 50–200 ms thresholds.
- Use `Support/AsyncTestAssert.cs` (`await AsyncTestAssert.ThrowsAsync<T>(...)`); NUnit's
  `Assert.ThrowsAsync` deadlocks on the Unity main thread.
- Tests inject a capturing `ILogger`; a stray `Debug.LogError` fails the test unless wrapped in
  `LogAssert.Expect`.
- Tests assert on the exact message formats documented in `docs/DESIGN.md`.
