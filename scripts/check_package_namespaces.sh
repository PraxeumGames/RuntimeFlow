#!/usr/bin/env bash
# Package layout guard for com.praxeum.runtimeflow (runs in CI and locally, no Unity needed).
#  1. namespace == folder:  Runtime/*.cs -> RuntimeFlow, Runtime/Internal -> RuntimeFlow.Internal,
#     Runtime/Testing -> RuntimeFlow.Testing, Editor/** -> RuntimeFlow.Editor. No other Runtime
#     subfolders. Runtime/Properties and Editor/Properties hold assembly attributes and no namespace.
#     The rule covers the package only; the test and demo assemblies use their asmdef root namespace.
#  2. forbidden tokens: VContainer internals, Reflection.Emit, ConfigureAwait(false), resurrected 0.x
#     names — in every text file of the package, not only sources: an asmdef reference, a USS class, a
#     package.json keyword or an rsp flag can resurrect a name just as well.
#  3. binaries only under Runtime/Plugins; no Analyzers/; no mono crash dumps anywhere.
#  4. csc.rsp with -nullable:enable next to every asmdef.
set -uo pipefail
cd "$(dirname "$0")/.."
PKG=com.praxeum.runtimeflow
fail=0
err() { echo "::error::$*"; fail=1; }

# 1. namespace == folder
while IFS= read -r f; do
  rel=${f#"$PKG/"}
  case "$rel" in
    Runtime/Properties/*|Editor/Properties/*) continue ;;
    Runtime/Plugins/*) err "$rel: no sources allowed under Runtime/Plugins"; continue ;;
    Runtime/Internal/*/*) err "$rel: Runtime/Internal must be flat"; continue ;;
    Runtime/Internal/*) expected=RuntimeFlow.Internal ;;
    Runtime/Testing/*/*) err "$rel: Runtime/Testing must be flat"; continue ;;
    Runtime/Testing/*) expected=RuntimeFlow.Testing ;;
    Runtime/*/*) err "$rel: unexpected Runtime subfolder (only Internal, Testing, Plugins, Properties)"; continue ;;
    Runtime/*) expected=RuntimeFlow ;;
    Editor/*) expected=RuntimeFlow.Editor ;;
    *) err "$rel: sources must live under Runtime/ or Editor/"; continue ;;
  esac
  actual=$(grep -m1 -E '^[[:space:]]*namespace[[:space:]]+' "$f" | sed -E 's/^[[:space:]]*namespace[[:space:]]+([A-Za-z0-9_.]+).*/\1/')
  if [[ -z "$actual" ]]; then err "$rel: no namespace declaration"; continue; fi
  if [[ "$actual" != "$expected" ]]; then err "$rel: namespace '$actual' but folder implies '$expected'"; fi
done < <(find "$PKG" -name '*.cs' | sort)

# 2. forbidden tokens
forbidden='VContainer\.Internal|System\.Reflection\.Emit|ConfigureAwait\(false\)|\bGameContext\b|\bRuntimePipeline\b|\bGameFlow\b|\bContentSource\b|\bActivePipeline\b|RuntimeFlowInstallerModules|GenerateRuntimeFlowInitializationGraph|IUserInteractionGatedInitializableService'
if hits=$(grep -rnE "$forbidden" \
  --include='*.cs' --include='*.asmdef' --include='*.uss' --include='*.json' --include='*.rsp' \
  --exclude='*.meta' "$PKG"); then
  while IFS= read -r h; do err "forbidden token: $h"; done <<< "$hits"
fi

# 3. binaries and junk
while IFS= read -r f; do
  case "$f" in "$PKG/Runtime/Plugins/"*) ;; *) err "$f: binaries are allowed only under Runtime/Plugins" ;; esac
done < <(find "$PKG" -name '*.dll' | sort)
[[ -e "$PKG/Analyzers" ]] && err "$PKG/Analyzers must not exist"
if crash=$(find . -path ./RuntimeFlow.UnityTests/Library -prune -o -name 'mono_crash*' -print | grep .); then err "crash dumps committed: $crash"; fi

# 4. csc.rsp
for d in "$PKG/Runtime" "$PKG/Runtime/Testing" "$PKG/Editor"; do
  [[ -f "$d/csc.rsp" ]] && grep -q -- '-nullable:enable' "$d/csc.rsp" || err "$d/csc.rsp missing or lacks -nullable:enable"
done

if [[ $fail -ne 0 ]]; then echo "check_package_namespaces: FAILED"; exit 1; fi
echo "check_package_namespaces: OK"
