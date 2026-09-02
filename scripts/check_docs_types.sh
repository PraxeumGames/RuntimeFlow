#!/usr/bin/env bash
# Every backtick-quoted type name in the docs must exist in the package sources (or be allowlisted).
# Checked tokens look like `TypeName`, `TypeName<T>` or `TypeName.Member`; paths/commands are ignored.
set -uo pipefail
cd "$(dirname "$0")/.."
PKG=com.praxeum.runtimeflow
DOCS=(README.md "$PKG/README.md" docs/DESIGN.md)
ALLOW=docs/external-types.txt
fail=0
for doc in "${DOCS[@]}"; do
  [[ -f "$doc" ]] || { echo "::error::$doc not found"; fail=1; continue; }
  while IFS= read -r tok; do
    name=${tok%%[<.]*}
    grep -qx "$name" "$ALLOW" 2>/dev/null && continue
    grep -rqE "\b$name\b" --include='*.cs' "$PKG/Runtime" "$PKG/Editor" && continue
    echo "::error::$doc references \`$tok\` but '$name' does not exist in $PKG sources (add it to $ALLOW if external)"
    fail=1
  done < <(grep -oE '`[A-Z][A-Za-z0-9]*(<[^`]*>)?(\.[A-Za-z][A-Za-z0-9]*)*`' "$doc" | tr -d '`' | sort -u)
done
if [[ $fail -ne 0 ]]; then echo "check_docs_types: FAILED"; exit 1; fi
echo "check_docs_types: OK"
