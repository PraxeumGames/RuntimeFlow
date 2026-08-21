# VContainer fork pin policy

RuntimeFlow declares a single package dependency:

```json
"jp.hadashikick.vcontainer": "https://github.com/Bezarius/VContainer.git?path=VContainer/Assets/VContainer#1.15.3.1"
```

This is a **fork** of upstream [hadashiA/VContainer](https://github.com/hadashiA/VContainer),
tag `1.15.3.1` (upstream reports version `1.15.3`). The same pin is used by the
RuntimeFlow Unity test project and by SF2 — do not change it without re-running the
full EditMode suite.

## Why a fork

The fork's delta from upstream `1.15.3` is maintained in the Bezarius repository;
consult its commit history between the upstream `1.15.3` tag and `1.15.3.1` for the
exact changes. RuntimeFlow relies on VContainer internals that are not covered by
its public API surface:

- `VContainer.Internal.Registry` — built directly by `RuntimeFlowContainerBuilder.BuildRegistry`
- `Registration` / `RegistrationBuilder` / `IInstanceProvider` internals
- `FixedInstanceProvider` detection during instance-ownership tracking
- Diagnostics integration (`DiagnosticsCollector.TraceRegister/TraceBuild/TraceResolve`)

Because of this, **upgrading VContainer is a breaking change by default**: internal
shapes may move between versions. Any bump must:

1. Re-run `scripts/run_unity_editmode_tests.sh` locally (255 tests).
2. Verify `GameContextNativeDisposalTests`, `LifetimePassthroughTests`, and
   `RegistrationStoreRegressionTests` still pass — they pin the container semantics.
3. Update this document and the pin in both `package.json` files.

## Upstreaming

Where possible, changes needed by RuntimeFlow should be proposed upstream to
hadashiA/VContainer so the fork can converge back to the original.
