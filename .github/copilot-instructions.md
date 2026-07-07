# Copilot Instructions — RuntimeFlow

## Project overview

RuntimeFlow is a Unity game startup orchestration framework delivered as a UPM package (`com.praxeum.runtimeflow`). It provides hierarchical DI scopes, an async service initialization DAG, health supervision with automatic recovery, and a runtime pipeline for managing game lifecycle.

The repository has three components:

- **`com.praxeum.runtimeflow/`** — The Unity package (runtime code, no `.csproj`). Uses VContainer for DI and `Microsoft.Extensions.Logging.Abstractions` for logging. Targets Unity 2021.3+.
- **`RuntimeFlow.UnityTests/`** — Unity test project containing NUnit EditMode runtime tests against the local UPM package and the real VContainer package.
- **`RuntimeFlow.Generators/`** — A Roslyn `IIncrementalGenerator` that builds `CompiledInitializationGraph.g.cs` at compile time, validating scope rules and detecting dependency cycles.

## Build and test

```bash
# Build the entire solution
dotnet build RuntimeFlow.sln

# Build the source generator
dotnet build RuntimeFlow.Generators

# Run generator tests
dotnet test RuntimeFlow.Generators.Tests

# Run runtime tests in Unity EditMode
scripts/run_unity_editmode_tests.sh
```

> Runtime tests intentionally run inside Unity with NUnit and the real `jp.hadashikick.vcontainer` package. Do not reintroduce a .NET wrapper or local fake VContainer implementation for runtime tests.

## Architecture

### Scope hierarchy

The framework enforces a strict four-level scope hierarchy:

```
Global (0)  →  Session (1)  →  Scene (2)  →  Module (3)
```

- Each scope gets its own `GameContext` wrapping a VContainer `IObjectResolver`.
- Services in a scope may depend only on services from the same or earlier scopes (enforced at compile time by the source generator — diagnostic `RF0003`).
- Scope types are identified by installer classes implementing `ISceneScope` or `IModuleScope` (with a `Configure` method that registers services). Global and Session scopes use built-in types.

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
    builder.Session()
        .Register<IMyService, MyService>(Lifetime.Singleton);

    builder.Scene<MySceneScope>();
    builder.Module<MyModuleScope>();
});

pipeline.ConfigureFlow(new MyFlowScenario());
await pipeline.RunAsync(sceneLoader);
```

Scene and module scopes are installer classes:
```csharp
public class MySceneScope : ISceneScope
{
    public void Configure(IGameScopeRegistrationBuilder builder)
    {
        builder.Register<ISceneService, SceneService>(Lifetime.Singleton);
    }
}
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

- Runtime tests use `RuntimePipeline.Create(...)` to wire the full pipeline, then assert on tracked state (attempt counters, call logs, observer collections).
- Test services live in `RuntimeFlow.UnityTests/Assets/Tests/EditMode/RuntimeFlow.Tests/Services/` and use `AttemptControlled*` naming — they accept a `Func<int, CancellationToken, Task>` to control per-attempt behavior.
- Test scope installers live in `RuntimeFlow.UnityTests/Assets/Tests/EditMode/RuntimeFlow.Tests/Scopes/` (e.g., `TestSceneScope : ISceneScope`, `TestModuleScope : IModuleScope`) — they accept an `Action<IGameScopeRegistrationBuilder>` lambda to configure registrations dynamically.
- Session scope in tests uses `builder.DefineSessionScope()` (parameterless, built-in type) and `builder.Session()` for inline registration.
- Test observers in `RuntimeFlow.Tests/Observers/` collect health metrics and retry events.
- `TestDoubles/DelegateRuntimeFlowScenario` wraps a lambda as `IRuntimeFlowScenario`.
- `TestDoubles/NoopSceneLoader` provides a no-op `IGameSceneLoader`.

### Event propagation

`IScopeEventBus.Publish` supports three modes: `Local` (current scope only), `Bubble` (up to parent scopes), and `Broadcast` (down to child scopes). Events implement the `IScopeEvent` marker interface.

### Health and recovery

`RuntimePipelineOptions.Health` configures timeouts and auto-restart limits. The health supervisor tracks per-service metrics and can automatically restart the session scope when a service times out, up to `MaxAutoSessionRestartsPerRun` times.
