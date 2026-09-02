# Third Party Notices

## Microsoft.Extensions.Logging.Abstractions

RuntimeFlow's runtime assembly logs through `Microsoft.Extensions.Logging.Abstractions`
(`ILogger`). A precompiled copy of the assembly is shipped under `Runtime/Plugins/` so the
package is self-contained; the asmdef references it by name.

- Source: https://github.com/dotnet/runtime (src/libraries/Microsoft.Extensions.Logging.Abstractions)
- Version: 8.0.0 (netstandard2.0)
- License: MIT — https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

Copyright (c) .NET Foundation and Contributors

## VContainer

RuntimeFlow builds on the public API of VContainer only (no internals, no fork).

- Upstream: https://github.com/hadashiA/VContainer (by hadashiA, MIT)
- Pinned: tag `1.15.3` by commit SHA in `package.json` (path `VContainer/Assets/VContainer`)
