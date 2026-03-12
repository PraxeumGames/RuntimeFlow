# RuntimeFlow

**Game startup orchestration framework for Unity**

## Overview

RuntimeFlow is a Unity framework that brings order to game startup. It provides a hierarchical dependency injection system with four strict scope levels — Global, Session, Scene, and Module — each with its own DI container backed by [VContainer](https://vcontainer.hadashikick.jp/). Services are registered into scopes and initialized asynchronously in topological order, with the entire dependency graph validated at compile time by a Roslyn source generator.

The framework manages the full game lifecycle through a runtime pipeline. You define scope installers, register services, and write a flow scenario that orchestrates scene transitions and scope loading. RuntimeFlow handles initialization ordering, scope activation/deactivation, disposal in reverse order, and progress reporting — all with `async`/`await` and cancellation support.

RuntimeFlow also includes a health supervision system that monitors service initialization timeouts and can automatically restart the session scope when failures occur, making your game startup resilient to transient errors.

## Features

- **Four-level scope hierarchy** — Global → Session → Scene → Module with strict dependency rules (services can only depend on same or earlier scopes)
- **Compile-time dependency graph validation** — Roslyn source generator detects duplicate implementations, missing dependencies, scope violations, and cycles before runtime
- **Async initialization with topological ordering** — Services initialize in dependency order and dispose in reverse
- **Health monitoring** — Configurable per-service timeouts with automatic session scope restart on failure
- **Event bus** — `IScopeEventBus` with Local, Bubble (up to parents), and Broadcast (down to children) propagation modes
- **Lazy initialization** — Mark services with `ILazyInitializableService` to defer initialization until first use
- **Scene loading abstraction** — Pluggable `IGameSceneLoader` with scene route resolution
- **Progress tracking** — `IInitializationProgressNotifier` reports per-service and per-scope initialization progress
- **Scope preloading** — Pre-initialize scene and module scopes before they are needed
- **Additive modules** — Load and unload module scopes additively within a scene

## Installation

### Prerequisites

- Unity 2021.3 or later
- [VContainer](https://vcontainer.hadashikick.jp/) (DI framework by hadashiA) — must be installed separately (see Step 1)

> **Note:** `Microsoft.Extensions.Logging.Abstractions` is bundled with the package as a precompiled DLL — no separate installation is required.

### Step 1: Install VContainer

VContainer is **not** on Unity's built-in package registry. Install it using one of the options below.

**Option A — Via OpenUPM** (recommended):

```bash
openupm add jp.hadashikick.vcontainer
```

**Option B — Via git URL** in Unity Package Manager:

Open **Window → Package Manager → + → Add package from git URL** and enter:

```
https://github.com/hadashiA/VContainer.git?path=VContainer/Assets/VContainer
```

Or add it directly to your `Packages/manifest.json`:

```json
{
  "dependencies": {
    "jp.hadashikick.vcontainer": "https://github.com/hadashiA/VContainer.git?path=VContainer/Assets/VContainer"
  }
}
```

### Step 2: Install RuntimeFlow

**Via git URL** in Unity Package Manager:

Open **Window → Package Manager → + → Add package from git URL** and enter:

```
https://github.com/PraxeumGames/RuntimeFlow.git?path=com.praxeum.runtimeflow
```

Or add it directly to your `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.praxeum.runtimeflow": "https://github.com/PraxeumGames/RuntimeFlow.git?path=com.praxeum.runtimeflow"
  }
}
```

## Quick Start

```csharp
// 1. Define a scene scope installer
public class GameplayScene : ISceneScope
{
    public void Configure(IGameScopeRegistrationBuilder builder)
    {
        builder.Register<IMyService, MyService>(Lifetime.Singleton);
    }
}

// 2. Define a service with a scope-specific interface
public interface IMyService : ISceneInitializableService { }

public class MyService : IMyService
{
    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        // Initialization logic
        return Task.CompletedTask;
    }
}

// 3. Build the pipeline, register services, and run
var pipeline = RuntimePipeline.Create(builder =>
{
    builder.Session()
        .Register<IAuthService, AuthService>(Lifetime.Singleton);

    builder.Scene<GameplayScene>();
});

pipeline.ConfigureFlow(new MyFlowScenario());
await pipeline.RunAsync(sceneLoader);
```

Flow scenarios implement `IRuntimeFlowScenario` to orchestrate the game lifecycle:

```csharp
public class MyFlowScenario : IRuntimeFlowScenario
{
    public async Task ExecuteAsync(
        IRuntimeFlowContext context, CancellationToken cancellationToken)
    {
        await context.InitializeAsync(cancellationToken);
        await context.LoadScopeSceneAsync<GameplayScene>(cancellationToken);
    }
}
```

## Architecture

### Scope Hierarchy

```
Global (0)  →  Session (1)  →  Scene (2)  →  Module (3)
```

Each scope gets its own `IGameContext` wrapping a VContainer `IObjectResolver`. Services in a scope may depend only on services from the same or an earlier (wider) scope — this rule is enforced at compile time.

Scene and module scopes are defined as **installer classes** that implement `ISceneScope` or `IModuleScope`. These classes configure their own DI registrations, giving them a concrete purpose beyond being type keys. Global and Session scopes use built-in types and are configured inline.

### Service Lifecycle

Services implement scope-specific marker interfaces that extend `IAsyncInitializableService`:

```
IAsyncInitializableService
├── IGlobalInitializableService
├── ISessionInitializableService
├── ISceneInitializableService
└── IModuleInitializableService
```

The same pattern applies to `IAsyncDisposableService` and `IAsyncScopeActivationService`.

### Key Types

| Concern | Type | Description |
|---|---|---|
| Pipeline orchestration | `RuntimePipeline` | Main entry point — create, configure, and run |
| Flow execution | `IRuntimeFlowContext` | Scene/module loading, service resolution |
| Flow definition | `IRuntimeFlowScenario` | Define top-level game flow |
| DI context | `IGameContext` | Scoped service registration and resolution |
| Health monitoring | `RuntimeHealthSupervisor` | Timeout tracking and auto-restart |
| Event bus | `IScopeEventBus` | Scoped event publish/subscribe |
| Builder API | `GameContextBuilder` | Configure scopes and service registrations |

## Source Generator

RuntimeFlow includes a Roslyn incremental source generator (`IIncrementalGenerator`) that analyzes service registrations at compile time and generates `CompiledInitializationGraph.g.cs`. The generator builds a dependency graph from constructor parameters and `[DependsOn]` attributes, then validates it.

### Diagnostics

| Code | Severity | Description |
|---|---|---|
| `RF0001` | Error | **Duplicate implementation** — multiple classes implement the same service interface |
| `RF0002` | Error | **Missing dependency** — constructor parameter has no registered implementation |
| `RF0003` | Error | **Scope violation** — a service depends on a service from a later (narrower) scope |
| `RF0004` | Error | **Cycle detected** — circular dependency in the initialization graph |

## Project Structure

```
RuntimeFlow/
├── com.praxeum.runtimeflow/       # Unity UPM package (runtime code)
│   ├── Runtime/
│   │   ├── Contexts/              # IGameContext, GameContextBuilder
│   │   ├── Events/                # IScopeEventBus, event propagation
│   │   ├── Initialization/        # Service interfaces, DAG execution
│   │   └── Runtime/               # Pipeline, flow, health supervision
│   └── package.json
├── RuntimeFlow/                   # .NET class library wrapper for testing
├── RuntimeFlow.Generators/        # Roslyn source generator
├── RuntimeFlow.Tests/             # xUnit test suite (.NET 9.0)
├── RuntimeFlow.sln                # Solution file
└── LICENSE
```

## Development

```bash
# Build the entire solution
dotnet build RuntimeFlow.sln

# Build the source generator
dotnet build RuntimeFlow.Generators

# Run all tests
dotnet test RuntimeFlow.Tests

# Run a specific test class
dotnet test RuntimeFlow.Tests --filter "FullyQualifiedName~ScopeEventBusTests"

# Run a single test
dotnet test RuntimeFlow.Tests --filter "FullyQualifiedName~ScopeEventBusTests.Publish_Local_DoesNotReachParent"
```

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.
