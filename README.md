# RuntimeFlow

[![CI](https://github.com/PraxeumGames/RuntimeFlow/actions/workflows/ci.yml/badge.svg)](https://github.com/PraxeumGames/RuntimeFlow/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

RuntimeFlow runs the asynchronous startup of a Unity game on top of stock VContainer. A service is a
class that implements one interface, `IAsyncInitializable`, and is registered with VContainer in any
way the container supports; after `Build()` the framework reads the scope's own registrations, derives
an initialization DAG from constructor parameters, validates it before the first `InitializeAsync`, and
runs it dynamically — each service starts as soon as its own dependencies are done, independent
services overlap. Failures are the point: one exception names the service, the scope, the phase, the
elapsed time, everything that completed and everything that was still blocked, with the original
exception as `InnerException`. There is no central flow file, no stage markers and no custom container.

Package documentation: [`com.praxeum.runtimeflow/README.md`](com.praxeum.runtimeflow/README.md).
Design rationale and exact message formats: [`docs/DESIGN.md`](docs/DESIGN.md).

## Repository map

| Path | Purpose |
|---|---|
| `com.praxeum.runtimeflow/` | The UPM package. `Runtime/` — public API (namespace `RuntimeFlow`); `Runtime/Internal/` — graph builder and scheduler; `Runtime/Testing/` — test harness, compiled only under `UNITY_INCLUDE_TESTS`; `Editor/` — the dashboard window; `Runtime/Plugins/` — the Microsoft.Extensions.Logging.Abstractions assembly. |
| `RuntimeFlow.UnityTests/` | Unity project holding the authoritative test suite (`Assets/Tests`, EditMode and PlayMode) and the demo (`Assets/Demo`) that exercises the broken-flow cases. |
| `scripts/` | `run_unity_editmode_tests.sh [playmode]`, `check_package_namespaces.sh`, `check_docs_types.sh`. |
| `docs/` | [`docs/DESIGN.md`](docs/DESIGN.md) and the allowlist used by the docs gate. |

There is no solution file, no `dotnet` build and no source generator; tests run only inside Unity.

## Install

In the consuming project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.praxeum.runtimeflow": "https://github.com/PraxeumGames/RuntimeFlow.git?path=com.praxeum.runtimeflow#1.0.0",
    "jp.hadashikick.vcontainer": "https://github.com/hadashiA/VContainer.git?path=VContainer/Assets/VContainer#f2afd2ac175a1e04ac59a8f69794df827b53b732"
  },
  "testables": [
    "com.praxeum.runtimeflow"
  ]
}
```

**Both lines are required.** The package declares `jp.hadashikick.vcontainer` in its own
`package.json`, but UPM does not resolve a git-URL dependency that is declared *inside* a package: git
dependencies are only fetched from the project manifest. The declaration in the package documents the
requirement and is satisfied when the project manifest names the same package; without the second line
above the project fails to compile with unresolved `VContainer` references. The pin is upstream
`hadashiA/VContainer`, tag 1.15.3, referenced by commit SHA
(`f2afd2ac175a1e04ac59a8f69794df827b53b732`) so the resolved API is exactly the one the suite runs
against. A project that already ships its own VContainer — a fork, a registry copy or an embedded one —
keeps it and omits the second line, as long as it is a superset of 1.15.3. CI asserts that the SHA in
`com.praxeum.runtimeflow/package.json` and the one in `RuntimeFlow.UnityTests/Packages/manifest.json`
never drift apart.

The `testables` entry is what makes Unity define `UNITY_INCLUDE_TESTS` for the package, which is the
constraint that compiles the `RuntimeFlow.Testing` assembly (`TestFlow`, `LifecycleFake`,
`CollectingObserver`). Without it the harness is simply absent from the project — the runtime package
works either way, and nothing from `RuntimeFlow.Testing` ever reaches a player build.

## Quick start

```csharp
public sealed class RemoteConfig : IAsyncInitializable                  // no attribute: required, weight 1
{
    public string CatalogUrl { get; private set; } = "builtin://catalog";
    public async Task InitializeAsync(InitContext ctx, CancellationToken ct)
        => CatalogUrl = await Backend.FetchCatalogUrlAsync(ct);
}

[Init(Phase = "content", Weight = 3)]                                   // three times the progress weight
public sealed class Catalog : IAsyncInitializable
{
    private readonly RemoteConfig _config;
    public Catalog(RemoteConfig config) => _config = config;            // ctor parameter = edge "after RemoteConfig"
    public Task InitializeAsync(InitContext ctx, CancellationToken ct)
        => Backend.LoadCatalogAsync(_config.CatalogUrl, ct);
}

[Init(UserGated = true)]                                                // waits for the player, never times out
public sealed class GdprConsent : IAsyncInitializable
{
    public Task InitializeAsync(InitContext ctx, CancellationToken ct) => ConsentDialog.ShowAsync(ct);
}

public sealed class UpdateCheck : IAsyncInitializable
{
    public async Task InitializeAsync(InitContext ctx, CancellationToken ct)
    {
        if (await Backend.MustUpdateAsync(ct)) ctx.Halt("update.required");   // stop startup, no exception
    }
}

var host = new RuntimeFlowHost(
    global: b => b.RegisterInitializable<RemoteConfig>(),
    session: b => { b.RegisterInitializable<GdprConsent>(); b.RegisterInitializable<UpdateCheck>(); b.RegisterInitializable<Catalog>(); },
    new RuntimeFlowOptions { Phases = new[] { "platform", "content" } });

var result = await host.StartAsync();
if (result.Outcome == StartupOutcome.Halted) return;                    // result.HaltReason == "update.required"
await host.RestartAsync("bundles-updated");                             // Global stays warm, Session is rebuilt
UnityEngine.Debug.Log(host.Describe());                                 // the graph, its edges and their origins
```

`RegisterInitializable<T>()` is optional sugar for
`Register<T>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces()`; any VContainer registration that
exposes `IAsyncInitializable` is discovered the same way.

## Development

```bash
UNITY_BIN=/Applications/Unity/Hub/Editor/2022.3.62f2/Unity.app/Contents/MacOS/Unity \
  scripts/run_unity_editmode_tests.sh                       # EditMode suite (authoritative)
UNITY_BIN=... scripts/run_unity_editmode_tests.sh playmode  # PlayMode suite

scripts/check_package_namespaces.sh && scripts/check_docs_types.sh   # package gate, no Unity needed
```

`UNITY_BIN` may be omitted on macOS: the script picks the editor matching
`RuntimeFlow.UnityTests/ProjectSettings/ProjectVersion.txt`. Narrow a run with a namespace, fixture or
test name:

```bash
RUNTIMEFLOW_TEST_FILTER=RuntimeFlow.Tests.Failure UNITY_BIN=... scripts/run_unity_editmode_tests.sh
```

Results land in `RuntimeFlow.UnityTests/TestResults/`, the editor log in `RuntimeFlow.UnityTests/Logs/`.

## CI

- **Package gate** runs on every push and pull request and needs no Unity: the namespace/layout guard,
  the docs-reference guard, JSON validity of `package.json`, the manifest and every assembly
  definition, a check that the package version has a matching changelog section, and a check that the
  VContainer pin is identical in `package.json` and in the test project's manifest.
- **Unity EditMode and PlayMode suites** run nightly and on manual dispatch. They additionally run on a
  push or pull request when the repository variable `RUNTIMEFLOW_RUN_UNITY_TESTS` is set to `1`; the
  Unity license secrets (`UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD`) are what those jobs need to
  pass, not part of the trigger condition.

## Supported versions

- Unity `2022.3` or newer is declared in `package.json`; the suite is validated on 2022.3.62f2 locally
  and in CI. 2022.2 is the floor for the UI Toolkit API the dashboard uses; older editors are not
  supported.
- VContainer 1.15.3 or newer (upstream, or a fork that is a superset of it), declared in the consuming
  project's own `Packages/manifest.json` — see [Install](#install).
- The package is compiled with nullable reference types enabled and uses default interface members
  (`IRuntimeFlowObserver`). On Unity 2022.3+ both API compatibility levels — ".NET Standard" (2.1) and
  ".NET Framework" — support them, so no project setting has to change.

## License

MIT. See [LICENSE](LICENSE).
