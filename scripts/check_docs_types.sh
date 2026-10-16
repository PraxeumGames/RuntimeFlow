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

# Mirror the sources with comments and string literals removed, and extract each type's own body
# so a sibling type declared in the same file cannot satisfy a `Type.Member` check.
mkdir -p "$STRIP/types"
python3 - "$STRIP" "$PKG/Runtime" "$PKG/Editor" <<'PYEOF'
import os, re, sys

strip_dir = sys.argv[1]
roots = sys.argv[2:]

def strip_comments_and_strings(text):
    out = []
    i, n = 0, len(text)
    while i < n:
        if text[i:i+2] == '//':
            j = text.find('\n', i + 2)
            if j < 0:
                break
            out.append('\n')
            i = j + 1
        elif text[i:i+2] == '/*':
            j = text.find('*/', i + 2)
            if j < 0:
                break
            out.append('\n' * text[i:j+2].count('\n'))
            i = j + 2
        elif text[i:i+2] in ('@"', '$"') or (i + 2 < n and text[i:i+3] in ('$@"', '@$"')):
            prefix = 3 if text[i:i+3] in ('$@"', '@$"') else 2
            verbatim = '@' in text[i:i+prefix]
            i += prefix
            lines = 0
            while i < n:
                if text[i] == '\n':
                    lines += 1
                if verbatim:
                    if text[i:i+2] == '""':
                        i += 2
                        continue
                    if text[i] == '"':
                        i += 1
                        break
                else:
                    if text[i] == '\\':
                        i += 2
                        continue
                    if text[i] == '"':
                        i += 1
                        break
                i += 1
            out.append('""' + ('\n' * lines))
        elif text[i] in ('"', "'"):
            q = text[i]
            i += 1
            while i < n:
                if text[i] == '\\':
                    i += 2
                    continue
                if text[i] == q:
                    i += 1
                    break
                i += 1
            out.append('""' if q == '"' else "''")
        else:
            out.append(text[i])
            i += 1
    return ''.join(out)

decl_re = re.compile(r'\b(?:class|struct|interface|enum|record|delegate)\s+([A-Za-z_][A-Za-z0-9_]*)\b')

def strip_nested_type_bodies(body, header_end):
    while True:
        m = decl_re.search(body, header_end)
        if not m:
            break
        brace = body.find('{', m.end())
        semi = body.find(';', m.end())
        if brace == -1 or (semi != -1 and semi < brace):
            header_end = m.end()
            continue
        depth = 0
        k = brace
        while k < len(body):
            if body[k] == '{':
                depth += 1
            elif body[k] == '}':
                depth -= 1
                if depth == 0:
                    break
            k += 1
        body = body[:brace+1] + body[k:]
        header_end = brace + 2
    return body

for root in roots:
    for dirpath, _, filenames in os.walk(root):
        for fn in sorted(filenames):
            if not fn.endswith('.cs'):
                continue
            rel = os.path.join(dirpath, fn)
            with open(rel, encoding='utf-8') as f:
                raw = f.read()
            cleaned = strip_comments_and_strings(raw)
            out_path = os.path.join(strip_dir, rel)
            os.makedirs(os.path.dirname(out_path), exist_ok=True)
            with open(out_path, 'w', encoding='utf-8') as f:
                f.write(cleaned)

            for m in decl_re.finditer(cleaned):
                name = m.group(1)
                brace = cleaned.find('{', m.end())
                semi = cleaned.find(';', m.end())
                if semi != -1 and (brace == -1 or semi < brace):
                    body = cleaned[m.start():semi+1]
                elif brace != -1:
                    depth = 0
                    k = brace
                    while k < len(cleaned):
                        if cleaned[k] == '{':
                            depth += 1
                        elif cleaned[k] == '}':
                            depth -= 1
                            if depth == 0:
                                k += 1
                                break
                        k += 1
                    body = strip_nested_type_bodies(cleaned[m.start():k], brace - m.start() + 1)
                else:
                    body = cleaned[m.start():]
                body_path = os.path.join(strip_dir, 'types', f'{name}.body')
                with open(body_path, 'a', encoding='utf-8') as f:
                    f.write(body + '\n')
                file_path = os.path.join(strip_dir, 'types', f'{name}.file')
                if not os.path.exists(file_path):
                    with open(file_path, 'w', encoding='utf-8') as f:
                        f.write(rel + '\n')
PYEOF

SRC=()
while IFS= read -r f; do
  SRC+=("$f")
done < <(find "$STRIP/$PKG/Runtime" "$STRIP/$PKG/Editor" -name '*.cs' | sort)
[[ ${#SRC[@]} -gt 0 ]] || { echo "::error::no sources found under $PKG"; exit 1; }

NAMESPACES=$(grep -hoE '^[[:space:]]*namespace[[:space:]]+[A-Za-z0-9_.]+' "${SRC[@]}" \
  | sed -E 's/.*namespace[[:space:]]+//' | sort -u)

allowed()  { grep -qxF "$1" "$ALLOW" 2>/dev/null; }
# `[DependsOn]` and `[Init]` are the legal spellings of the *Attribute types, so both are accepted.
typebody() {
  if [[ -f "$STRIP/types/$1.body" ]]; then echo "$STRIP/types/$1.body"
  elif [[ -f "$STRIP/types/${1}Attribute.body" ]]; then echo "$STRIP/types/${1}Attribute.body"
  fi
}
declared() { [[ -n "$(typebody "$1")" ]]; }
declfile() {
  if [[ -f "$STRIP/types/$1.file" ]]; then head -1 "$STRIP/types/$1.file"
  elif [[ -f "$STRIP/types/${1}Attribute.file" ]]; then head -1 "$STRIP/types/${1}Attribute.file"
  fi
}

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
      body=$(typebody "${segments[0]}")
      file=$(declfile "${segments[0]}")
      memberof "${segments[1]}" "$body" && continue
      allowed "${segments[1]}" && continue
      echo "::error::$doc references \`$tok\` but '${segments[1]}' is not a member of '${segments[0]}' ($file)"
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
