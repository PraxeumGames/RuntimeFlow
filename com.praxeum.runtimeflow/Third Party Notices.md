# Third Party Notices

## Microsoft.Extensions.Logging.Abstractions

RuntimeFlow's runtime assembly logs through `Microsoft.Extensions.Logging.Abstractions`
(`ILogger<T>`, `NullLogger`). A precompiled copy of the assembly is shipped under
`Runtime/Plugins/` so the package is self-contained; the asmdef references it by name.

- Source: https://github.com/dotnet/runtime (src/libraries/Microsoft.Extensions.Logging.Abstractions)
- Version: 8.0.0 (netstandard2.0)
- License: MIT — https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

Copyright (c) .NET Foundation and Contributors

## VContainer

RuntimeFlow builds on VContainer. The dependency pin resolves to a maintained fork:

- Upstream: https://github.com/hadashiA/VContainer (by hadashiA, MIT)
- Pinned fork: https://github.com/Bezarius/VContainer (`#1.15.3.1`, path `VContainer/Assets/VContainer`)

See `docs/VCONTAINER_FORK.md` for the pin policy.
