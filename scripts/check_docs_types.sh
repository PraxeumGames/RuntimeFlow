#!/usr/bin/env bash
# Every backtick-quoted identifier in the docs must resolve against a *declaration* in the package.
#
# The point of the gate is to catch a doc that names a type or member which no longer exists, so a
# mention inside a `///` doc comment must not vouch for the name: the sources are stripped of doc
# comment lines first, and a name then has to be either a declared type (class/struct/interface/enum/
# record/delegate) or a public member. `Type.Member` is checked on both halves — the type must be
# declared and the member must appear in the file that declares it. Tokens carrying generic arguments
# or a call (`RegisterInitializable<T>()`, `host.Session.CreateScope(installer)`) are reduced to their
# dotted identifier; segments starting lowercase (`host`, `builder`, `package.json`) are skipped.
# Genuinely external names live in docs/external-types.txt, matched on the whole token or its first
# segment (so `Lifetime.Singleton` is covered by `Lifetime`).
set -uo pipefail
cd "$(dirname "$0")/.."
PKG=com.praxeum.runtimeflow
DOCS=(README.md "$PKG/README.md" docs/DESIGN.md)
ALLOW=docs/external-types.txt

STRIP=$(mktemp -d)
trap 'rm -rf "$STRIP"' EXIT

# Mirror the sources with every `///` line removed, keeping the directory layout so an error message
# can name the real file.
SRC=()
while IFS= read -r f; do
  out="$STRIP/$f"
  mkdir -p "$(dirname "$out")"
  grep -v '^[[:space:]]*///' "$f" > "$out"
  SRC+=("$out")
done < <(find "$PKG/Runtime" "$PKG/Editor" -name '*.cs' | sort)
[[ ${#SRC[@]} -gt 0 ]] || { echo "::error::no sources found under $PKG"; exit 1; }

NAMESPACES=$(grep -hoE '^[[:space:]]*namespace[[:space:]]+[A-Za-z0-9_.]+' "${SRC[@]}" \
  | sed -E 's/.*namespace[[:space:]]+//' | sort -u)

DECL='(class|struct|interface|enum|record|delegate)[[:space:]]+'

allowed()  { grep -qxF "$1" "$ALLOW" 2>/dev/null; }
# `[DependsOn]` and `[Init]` are the legal spellings of the *Attribute types, so both are accepted.
declared() { grep -qE "${DECL}($1|${1}Attribute)\b" "${SRC[@]}"; }
declfile() { grep -lE "${DECL}($1|${1}Attribute)\b" "${SRC[@]}" | head -1; }

# A member declaration, in the given files. Five shapes, because a "public X Name" grep alone misses
# a tuple-returning property (`public IReadOnlyList<(string A, Exception B)> Failures`), an interface
# method (no accessibility at all) and an enum member (neither accessibility nor type).
memberin() {
  local name=$1
  shift
  grep -qE "(public[^=;(]*\b$name\b)\
|(public[^=;]*\b$name[[:space:]]*\()\
|(^[[:space:]]+[A-Za-z_][][A-Za-z0-9_<>,.? ]*[[:space:]]$name[[:space:]]*\()\
|(\b$name[[:space:]]*\{[[:space:]]*get)\
|(^[[:space:]]+$name[[:space:]]*(,|=|$))" "$@"
}

member()   { memberin "$1" "${SRC[@]}"; }
memberof() { memberin "$1" "$2"; }
isnamespace() { grep -qxF "$1" <<< "$NAMESPACES"; }

fail=0
for doc in "${DOCS[@]}"; do
  [[ -f "$doc" ]] || { echo "::error::$doc not found"; fail=1; continue; }
  while IFS= read -r tok; do
    # Reduce `Foo<T>.Bar(baz)` to `Foo.Bar`.
    base=${tok%%(*}
    base=$(sed -E 's/<[^>]*>//g' <<< "$base")
    [[ -n "$base" ]] || continue

    allowed "$base" && continue
    allowed "${base%%.*}" && continue
    isnamespace "$base" && continue

    IFS='.' read -r -a parts <<< "$base"
    segments=()
    for part in "${parts[@]}"; do
      [[ "$part" =~ ^[A-Z] ]] && segments+=("$part")
    done
    [[ ${#segments[@]} -eq 0 ]] && continue

    if [[ ${#segments[@]} -eq 2 ]] && declared "${segments[0]}"; then
      file=$(declfile "${segments[0]}")
      memberof "${segments[1]}" "$file" && continue
      allowed "${segments[1]}" && continue
      echo "::error::$doc references \`$tok\` but '${segments[1]}' is not a member of '${segments[0]}' (${file#"$STRIP/"})"
      fail=1
      continue
    fi

    for segment in "${segments[@]}"; do
      allowed "$segment" && continue
      declared "$segment" && continue
      member "$segment" && continue
      echo "::error::$doc references \`$tok\` but '$segment' is neither declared nor a public member of $PKG (add it to $ALLOW if external)"
      fail=1
    done
  done < <(grep -oE '`[A-Za-z_][A-Za-z0-9_]*(<[^`>]*>)?(\.[A-Za-z_][A-Za-z0-9_]*(<[^`>]*>)?)*(\([^`]*\))?`' "$doc" \
             | tr -d '`' | sort -u)
done

if [[ $fail -ne 0 ]]; then echo "check_docs_types: FAILED"; exit 1; fi
echo "check_docs_types: OK"
