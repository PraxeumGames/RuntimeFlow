# RuntimeFlow (Unity UPM package)

RuntimeFlow is a Unity startup orchestration framework that combines:

- hierarchical DI scopes (`Global -> Session -> Scene -> Module`),
- async initialization/disposal pipelines,
- runtime health supervision and recovery,
- scene/module flow orchestration APIs.

This README is focused on **using the package in a Unity project**.  
For repository-level architecture and development layout, see the root [`README.md`](../README.md).

## Installation

### Prerequisites

- Unity `2021.3+`
- [VContainer](https://vcontainer.hadashikick.jp/) (required dependency)

Install VContainer first:

```bash
openupm add jp.hadashikick.vcontainer
```

Or via git URL in Package Manager:

```text
https://github.com/hadashiA/VContainer.git?path=VContainer/Assets/VContainer
```

Install RuntimeFlow package and pin version `0.1.0`:

```text
https://github.com/PraxeumGames/RuntimeFlow.git?path=com.praxeum.runtimeflow#0.1.0
```

In `Packages/manifest.json`:

```json
{
  "dependencies": {
    "jp.hadashikick.vcontainer": "https://github.com/hadashiA/VContainer.git?path=VContainer/Assets/VContainer",
    "com.praxeum.runtimeflow": "https://github.com/PraxeumGames/RuntimeFlow.git?path=com.praxeum.runtimeflow#0.1.0"
  }
}
```

> `#0.1.0` pins the dependency to the RuntimeFlow `0.1.0` git tag. Publish that tag in Git before using the URL in Unity.
> `RuntimeFlow.Runtime.asmdef` references `Microsoft.Extensions.Logging.Abstractions.dll` as a precompiled dependency.

## Quick start

### 1. Define scope installers

```csharp
public sealed class GameplaySceneScope : ISceneScope
{
    public void Configure(IGameScopeRegistrationBuilder builder)
    {
        builder.Register<IPlayerSpawnService, PlayerSpawnService>(Lifetime.Singleton);
    }
}

public sealed class HudModuleScope : IModuleScope
{
    public void Configure(IGameScopeRegistrationBuilder builder)
    {
        builder.Register<IHudService, HudService>(Lifetime.Singleton);
    }
}
```

### 2. Define services with scope-aware lifecycle contracts

```csharp
public interface IPlayerSpawnService : ISceneInitializableService { }

public sealed class PlayerSpawnService : IPlayerSpawnService
{
    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
```

### 3. Build and run a pipeline

```csharp
var pipeline = RuntimePipeline.Create(
    builder =>
    {
        builder.DefineSessionScope();
        builder.Session()
            .Register<IGameSessionService, GameSessionService>(Lifetime.Singleton);

        builder.Scene<GameplaySceneScope>();
        builder.Module<HudModuleScope>();
    },
    RuntimePipelinePresets.Production);

pipeline.ConfigureFlow(new GameStartupFlow());
await pipeline.RunAsync(new UnityGameSceneLoader(), cancellationToken: token);
```

```csharp
public sealed class GameStartupFlow : IRuntimeFlowScenario
{
    public async Task ExecuteAsync(IRuntimeFlowContext context, CancellationToken cancellationToken)
    {
        await context.InitializeAsync(cancellationToken);
        await context.LoadScopeSceneAsync<GameplaySceneScope>(cancellationToken);
        await context.LoadScopeModuleAsync<HudModuleScope>(cancellationToken);
    }
}
```

## Runtime API highlights

### `RuntimePipeline` operations

`RuntimePipeline` supports:

- pipeline creation: `Create`, `CreateFromGlobalContext`, `CreateFromResolver`,
- execution: `ConfigureFlow(...).RunAsync(...)`,
- scope operations: `LoadSceneAsync`, `LoadModuleAsync`, `PreloadSceneAsync`, `PreloadModuleAsync`,
- additive modules: `LoadAdditiveModuleAsync`, `UnloadAdditiveModuleAsync`,
- reload/restart: `ReloadScopeAsync`, `ReloadModuleAsync`, `RestartSessionAsync`,
- state/readiness inspection: `GetRuntimeStatus`, `GetReadinessStatus`, `GetRestartReadiness`, `GetExecutionContext`.

### `IRuntimeFlowContext` operations

Inside `IRuntimeFlowScenario.ExecuteAsync(...)`, you can:

- initialize and navigate: `InitializeAsync`, `GoToAsync`, `ResolveRouteAsync`,
- load/reload scopes: `LoadScopeSceneAsync`, `LoadScopeModuleAsync`, `ReloadScopeAsync`,
- preload scopes: `PreloadSceneAsync`, `PreloadModuleAsync`, `HasPreloadedScope`,
- manage additive modules: `LoadAdditiveModuleAsync`, `UnloadAdditiveModuleAsync`,
- access session services: `ResolveSessionService`, `TryResolveSessionService`.

### Flow presets

`RuntimeFlowPresets` includes reusable scenario builders:

- `InitializeOnly()`
- `EnsureSceneLoadedThenInitialize(sceneName)`
- `RestartAwareSceneBootstrap(options)`
- `StandardSession(fallbackRoute, configure)`

## Health, retry, and recovery

Configure `RuntimePipelineOptions` to control runtime resilience:

- `Health` (`RuntimeHealthOptions`): timeout model, stall timeout, auto-restart limits,
- `RetryPolicy` (`RuntimeRetryPolicyOptions`): retry attempts, backoff, jitter,
- `ErrorClassifier`: custom `IRuntimeErrorClassifier`,
- `HealthObserver`, `RetryObserver`, `LoadingProgressObserver`,
- `ReplayFlowOnSessionRestart` to replay configured flow during session restart,
- `SessionRestartPreparationHooks` for pre-restart preparation logic.

Built-in presets:

- `RuntimePipelinePresets.Minimal`
- `RuntimePipelinePresets.Development`
- `RuntimePipelinePresets.Production`

## Guards, transitions, and events

### Guards and restart preparation

- Implement `IRuntimeFlowGuard` to block unsafe transitions.
- Guard stages include `BeforeInitialize`, `BeforeNavigation`, `BeforeScopeReload`, `BeforeSessionRestart`, etc.
- Implement `IRuntimeSessionRestartPreparationHook` for restart-preparation tasks.
- `RuntimePipeline.Guards` executes registered restart preparation hooks before session restart.
- Legacy reflection (`ISessionRestartAware`, `RuntimeSessionRestartStateResetter`) is a transitional fallback and runs only when no `IRuntimeSessionRestartPreparationHook` is registered.

### Restart lifecycle ownership (breaking migration)

- RuntimeFlow now owns restart contracts and orchestration via:
  - `Runtime/Runtime/Pipeline/GameRestartContracts.cs`
  - `Runtime/Runtime/Pipeline/RuntimeFlowGameRestartHandler.cs`
  - `Runtime/Runtime/Pipeline/RuntimePipeline.Guards.cs`
- Consumers should use framework restart contracts from `SFS.Core.GameLoading`
  (`IGameRestartHandler`, `ISessionRestartAware`, `IGameDataCleaner`).
- Project-local restart contracts/orchestrator and `SfsGameBootstrapper` glue wiring were removed.
- This migration is a **breaking change** for integrations that referenced removed project-side restart types.
- `RuntimeSessionRestartStateResetter` remains project-owned and is invoked through framework restart preparation hooks.

### Transition hooks

Use `IScopeTransitionHandler` to react to:

- `OnTransitionOutAsync(...)`
- `OnTransitionProgressAsync(...)`
- `OnTransitionInAsync(...)`

### Event propagation

`IScopeEventBus.Publish(...)` supports:

- `Local` (current scope only)
- `Bubble` (up through parents)
- `Broadcast` (down through children)

## Initialization graph and diagnostics

RuntimeFlow validates initialization dependency graphs using constructor dependencies and `[DependsOn(typeof(...))]`.

Diagnostics:

| Code | Meaning |
|---|---|
| `RF0001` | Duplicate implementation |
| `RF0002` | Missing dependency |
| `RF0003` | Scope violation |
| `RF0004` | Dependency cycle |

## Package contents

```text
com.praxeum.runtimeflow/
├── Runtime/                         # Package runtime source
├── Analyzers/RuntimeFlow.Generators.dll
├── package.json
├── CHANGELOG.md
└── LICENSE.md
```

## License

MIT. See [LICENSE.md](LICENSE.md).
