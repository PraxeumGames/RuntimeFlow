# RuntimeFlow

[![CI](https://github.com/PraxeumGames/RuntimeFlow/actions/workflows/ci.yml/badge.svg)](https://github.com/PraxeumGames/RuntimeFlow/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**RuntimeFlow is a Unity startup orchestration framework centered on scoped DI, async lifecycle execution, and runtime recovery controls.**

For package installation and Unity integration usage, start with [`com.praxeum.runtimeflow/README.md`](com.praxeum.runtimeflow/README.md).

## Repository map

| Path | Purpose |
|---|---|
| `com.praxeum.runtimeflow/` | Unity UPM package (runtime code, package metadata, analyzer payload) |
| `RuntimeFlow.UnityTests/` | Unity test project for NUnit EditMode runtime tests against the real UPM package and VContainer |
| `RuntimeFlow.Generators/` | Roslyn incremental generator project for initialization graph diagnostics |
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

Generation is opt-in per consumer assembly via `[assembly: RuntimeFlow.Contexts.GenerateRuntimeFlowInitializationGraph]`, preventing unrelated Unity assemblies from emitting duplicate graph types.

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
dotnet test RuntimeFlow.Generators.Tests
scripts/run_unity_editmode_tests.sh              # EditMode suite
scripts/run_unity_editmode_tests.sh playmode     # PlayMode suite
```

Useful focused runs:

```bash
UNITY_BIN=/path/to/Unity scripts/run_unity_editmode_tests.sh
```

The script auto-selects the Unity editor matching `RuntimeFlow.UnityTests/ProjectSettings/ProjectVersion.txt`
when `UNITY_BIN` is unset.

## Quality gates

- `dotnet build RuntimeFlow.sln` — must succeed with zero warnings (warnings are treated as errors via `Directory.Build.props`).
- `dotnet test RuntimeFlow.Generators.Tests --no-build` — Roslyn generator regression tests for RF0001..RF0004 diagnostics.
- `scripts/run_unity_editmode_tests.sh` — NUnit EditMode runtime tests using the real Unity package and real VContainer.
- The .NET generator gates run on every push / pull request via [`.github/workflows/ci.yml`](.github/workflows/ci.yml). Runtime lifecycle tests run through `RuntimeFlow.UnityTests`; the workflow runs both EditMode and PlayMode jobs nightly and on manual dispatch. PRs run .NET gates only unless the repository variable `RUNTIMEFLOW_RUN_UNITY_TESTS=1` is set and Unity license secrets are configured.
- VContainer is pinned to an immutable commit SHA of a maintained fork; see [`docs/VCONTAINER_FORK.md`](docs/VCONTAINER_FORK.md) before touching the pin.
- The package declares minimum Unity `2021.3`; the suite is validated on `2022.3` (local runs and CI). Other editor versions are untested claims, not guarantees.

## License

MIT. See [LICENSE](LICENSE).
