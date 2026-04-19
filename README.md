# RuntimeFlow

[![CI](https://github.com/PraxeumGames/RuntimeFlow/actions/workflows/ci.yml/badge.svg)](https://github.com/PraxeumGames/RuntimeFlow/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**RuntimeFlow is a Unity startup orchestration framework centered on scoped DI, async lifecycle execution, and runtime recovery controls.**

For package installation and Unity integration usage, start with [`com.praxeum.runtimeflow/README.md`](com.praxeum.runtimeflow/README.md).

## Repository map

| Path | Purpose |
|---|---|
| `com.praxeum.runtimeflow/` | Unity UPM package (runtime code, package metadata, analyzer payload) |
| `RuntimeFlow.Generators/` | Roslyn incremental generator project for initialization graph diagnostics |
| `RuntimeFlow/` | .NET wrapper that compiles package runtime sources for test execution |
| `RuntimeFlow.Tests/` | xUnit integration/behavior tests for scopes, flow, health, loading, and recovery |
| `RuntimeFlow.sln` | Main solution for local build and test workflows |

## Core capabilities

- **Strict scope hierarchy**: `Global -> Session -> Scene -> Module`
- **Async lifecycle orchestration**: initialization, disposal, scope activation/deactivation contracts
- **Runtime flow API**: scene/module loading, route navigation, preloading, reload, and additive modules
- **Health and recovery**: service timeout supervision, retry policy, and controlled session restart paths
- **Flow safety hooks**: transition handlers, guard stages, and restart-preparation hooks
- **Scoped event propagation**: local, bubble-up, and broadcast-down delivery
- **Loading progress model**: operation snapshots (`kind`, `stage`, `state`, percent, errors)
- **Graph validation support**: constructor + `[DependsOn]` dependencies with RF diagnostics

## Architecture snapshot

### Scope model

```text
Global (0) -> Session (1) -> Scene (2) -> Module (3)
```

Services can depend only on same-or-wider scopes. `ISceneScope` and `IModuleScope` installers define scope-local registrations through `Configure(IGameScopeRegistrationBuilder)`.

### Runtime entry points

| Area | Primary APIs |
|---|---|
| Builder | `GameContextBuilder`, `IGameContextBuilder`, `IGameScopeRegistrationBuilder` |
| Pipeline | `RuntimePipeline`, `RuntimePipelineOptions`, `RuntimePipelinePresets` |
| Flow | `IRuntimeFlowScenario`, `IRuntimeFlowContext`, `SceneRoute`, `RuntimeFlowPresets` |
| Health/retry | `RuntimeHealthOptions`, `RuntimeRetryPolicyOptions`, `IRuntimeHealthObserver`, `IRuntimeRetryObserver` |
| Guarding | `IRuntimeFlowGuard`, `RuntimeFlowGuardStage`, `IRuntimeSessionRestartPreparationHook` |
| Status/readiness | `RuntimeStatus`, `RuntimeReadinessStatus`, `IRuntimeExecutionContext` |
| Loading telemetry | `RuntimeLoadingOperationSnapshot`, `IRuntimeLoadingProgressObserver` |

### Source-generator diagnostics

`RuntimeFlow.Generators` defines diagnostics for initialization graph problems:

| Code | Description |
|---|---|
| `RF0001` | Duplicate implementation for a service interface |
| `RF0002` | Missing dependency for constructor/service graph |
| `RF0003` | Scope violation (dependency points to narrower scope) |
| `RF0004` | Circular dependency in initialization graph |

## Development

```bash
dotnet build RuntimeFlow.sln
dotnet build RuntimeFlow.Generators
dotnet test RuntimeFlow.Tests
```

Useful focused runs:

```bash
dotnet test RuntimeFlow.Tests --filter "FullyQualifiedName~ScopeEventBusTests"
dotnet test RuntimeFlow.Tests --filter "FullyQualifiedName~RuntimeScopeReloadApiTests"
```

## Quality gates

- `dotnet build RuntimeFlow.sln` — must succeed with zero warnings (warnings are treated as errors via `Directory.Build.props`).
- `dotnet test RuntimeFlow.Tests --no-build` — unit + integration tests for the runtime.
- `dotnet test RuntimeFlow.Generators.Tests --no-build` — Roslyn generator regression tests for RF0001..RF0004 diagnostics.
- The same gates run on every push / pull request via [`.github/workflows/ci.yml`](.github/workflows/ci.yml), with coverage collected via Coverlet (Cobertura) and uploaded as a workflow artifact.

## License

MIT. See [LICENSE](LICENSE).
