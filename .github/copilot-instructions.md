# Copilot Instructions — RuntimeFlow

## Project overview

RuntimeFlow is a Unity game startup orchestration framework delivered as a UPM package (`com.praxeum.runtimeflow`). It provides hierarchical DI scopes, an async service initialization DAG, health supervision with automatic recovery, and a runtime pipeline for managing game lifecycle.

The repository has three components:

- **`com.praxeum.runtimeflow/`** — The Unity package (runtime code, no `.csproj`). Uses VContainer for DI and `Microsoft.Extensions.Logging.Abstractions` for logging. Targets Unity 2021.3+.
- **`RuntimeFlow.Generators/`** — A Roslyn `IIncrementalGenerator` that builds `CompiledInitializationGraph.g.cs` at compile time, validating scope rules and detecting dependency cycles.
- **`RuntimeFlow.Tests/`** — xUnit test suite (.NET 9.0) covering scopes, initialization, health, events, and pipeline orchestration.

## Build and test

```bash
# Build the entire solution
dotnet build RuntimeFlow.sln

# Build the source generator
dotnet build RuntimeFlow.Generators

# Run all tests
dotnet test RuntimeFlow.Tests

# Run a single test class
dotnet test RuntimeFlow.Tests --filter "FullyQualifiedName~ScopeEventBusTests"

# Run a single test method
dotnet test RuntimeFlow.Tests --filter "FullyQualifiedName~ScopeEventBusTests.Publish_Local_DoesNotReachParent"
```

> The test project references a `RuntimeFlow` class library (`../RuntimeFlow/RuntimeFlow.csproj`) that compiles the Unity package sources for .NET testing.

## Architecture

### Scope hierarchy

The framework enforces a strict four-level scope hierarchy:

```
Global (0)  →  Session (1)  →  Scene (2)  →  Module (3)
```

- Each scope gets its own `GameContext` wrapping a VContainer `IObjectResolver`.
- Services in a scope may depend only on services from the same or earlier scopes (enforced at compile time by the source generator — diagnostic `RF0003`).
- Scope types are identified by empty marker classes (e.g., `sealed class MySceneScope { }`).

### Initialization DAG

Services implement scope-specific marker interfaces (`IGlobalInitializableService`, `ISessionInitializableService`, etc.) which all extend `IAsyncInitializableService`. The source generator scans the compilation for these implementations and builds a dependency graph from:

1. Constructor parameters that are themselves async-initializable services.
2. Explicit `[DependsOn(typeof(...))]` attributes on the implementation class.

At runtime, services are initialized in topological order and disposed in reverse.

### Key entry points

| Concern | Type | Location |
|---|---|---|
| Builder API | `GameContextBuilder` | `Runtime/Contexts/Core/` |
| Pipeline orchestration | `RuntimePipeline` | `Runtime/Runtime/Pipeline/` |
| Flow execution | `RuntimeFlowRunner` (implements `IRuntimeFlowContext`) | `Runtime/Runtime/Flow/` |
| Health monitoring | `RuntimeHealthSupervisor` | `Runtime/Runtime/Health/` |
| Event bus | `ScopeEventBus` (implements `IScopeEventBus`) | `Runtime/Events/` |
| Source generator | `InitializationGraphGenerator` | `RuntimeFlow.Generators/` |

### Registration and flow

```csharp
var pipeline = RuntimePipeline.Create(builder =>
{
    builder.DefineSessionScope<MySessionScope>();
    builder.DefineSceneScope<MySceneScope>();
    builder.DefineModuleScope<MyModuleScope>();

    builder.For<MySessionScope>()
        .Register<IMyService, MyService>(Lifetime.Singleton);
});

pipeline.ConfigureFlow(new MyFlowScenario());
await pipeline.RunAsync(sceneLoader);
```

Top-level game flow is defined by implementing `IRuntimeFlowScenario.ExecuteAsync(IRuntimeFlowContext, CancellationToken)`.

## Source generator diagnostics

| Code | Description |
|---|---|
| `RF0001` | Duplicate implementation — multiple classes implement the same service interface |
| `RF0002` | Missing dependency — constructor parameter has no registered implementation |
| `RF0003` | Scope violation — a service depends on a service from a later (narrower) scope |
| `RF0004` | Cycle detected — circular dependency in the initialization graph |

## Conventions

### Service interface hierarchy

Scope-specific interfaces follow a consistent marker pattern:

```
IAsyncInitializableService
├── IGlobalInitializableService
├── ISessionInitializableService
├── ISceneInitializableService
└── IModuleInitializableService
```

The same pattern applies to `IAsyncDisposableService`, `IAsyncScopeActivationService`, and `ILazyInitializableService`.

### Constructor injection

VContainer resolves constructors by: `[Inject]` attribute first, then most-parameter constructor. The source generator mirrors this logic via `InitializationGraphRules.SelectConstructor`.

### Test patterns

- Tests use `RuntimePipeline.Create(...)` to wire the full pipeline, then assert on tracked state (attempt counters, call logs, observer collections).
- Test services live in `RuntimeFlow.Tests/Services/` and use `AttemptControlled*` naming — they accept a `Func<int, CancellationToken, Task>` to control per-attempt behavior.
- Scope markers for tests live in `RuntimeFlow.Tests/Scopes/` (e.g., `TestSessionScope`, `TestSceneScope`).
- Test observers in `RuntimeFlow.Tests/Observers/` collect health metrics and retry events.
- `TestDoubles/DelegateRuntimeFlowScenario` wraps a lambda as `IRuntimeFlowScenario`.
- `TestDoubles/NoopSceneLoader` provides a no-op `IGameSceneLoader`.

### Event propagation

`IScopeEventBus.Publish` supports three modes: `Local` (current scope only), `Bubble` (up to parent scopes), and `Broadcast` (down to child scopes). Events implement the `IScopeEvent` marker interface.

### Health and recovery

`RuntimePipelineOptions.Health` configures timeouts and auto-restart limits. The health supervisor tracks per-service metrics and can automatically restart the session scope when a service times out, up to `MaxAutoSessionRestartsPerRun` times.
