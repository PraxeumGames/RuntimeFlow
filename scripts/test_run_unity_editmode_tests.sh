#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
RUNNER="$SCRIPT_DIR/run_unity_editmode_tests.sh"
TEMP_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/runtimeflow-fake-unity.XXXXXX")"
trap 'rm -rf "$TEMP_ROOT"' EXIT

PROJECT="$TEMP_ROOT/project"
mkdir -p "$PROJECT/Packages"
cat > "$PROJECT/Packages/manifest.json" <<'JSON'
{
  "dependencies": {
    "jp.hadashikick.vcontainer": "upstream-pin",
    "example.package": "1.0.0"
  }
}
JSON
cat > "$PROJECT/Packages/packages-lock.json" <<'JSON'
{
  "dependencies": {
    "jp.hadashikick.vcontainer": { "version": "upstream-pin" },
    "example.package": { "version": "1.0.0" }
  }
}
JSON
cp "$PROJECT/Packages/manifest.json" "$TEMP_ROOT/manifest.original.json"
cp "$PROJECT/Packages/packages-lock.json" "$TEMP_ROOT/packages-lock.original.json"

FAKE_UNITY="$TEMP_ROOT/fake-unity"
cat > "$FAKE_UNITY" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail

results=""
log=""
while (($#)); do
  case "$1" in
    -testResults) results="$2"; shift 2 ;;
    -logFile) log="$2"; shift 2 ;;
    *) shift ;;
  esac
done
[[ -z "${FAKE_UNITY_MARKER:-}" ]] || : > "$FAKE_UNITY_MARKER"
[[ -z "$log" ]] || : > "$log"

case "$FAKE_UNITY_MODE" in
  no-xml)
    exit 0
    ;;
  zero)
    cat > "$results" <<'XML'
<test-run name="Fake Unity" total="0" passed="0" failed="0" skipped="0" inconclusive="0" />
XML
    ;;
  skipped)
    cat > "$results" <<'XML'
<test-run name="Fake Unity" total="1" passed="0" failed="0" skipped="1" inconclusive="0"><test-case fullname="Fake.Skipped" result="Skipped" /></test-run>
XML
    ;;
  passed|nonzero)
    cat > "$results" <<'XML'
<test-run name="Fake Unity" total="1" passed="1" failed="0" skipped="0" inconclusive="0"><test-case fullname="Fake.Passed" result="Passed" /></test-run>
XML
    if [[ "$FAKE_UNITY_MODE" == nonzero ]]; then exit 7; fi
    ;;
  *)
    echo "Unexpected fake Unity mode: $FAKE_UNITY_MODE" >&2
    exit 99
    ;;
esac
FAKE
chmod +x "$FAKE_UNITY"

assert_restored_packages() {
  cmp -s "$PROJECT/Packages/manifest.json" "$TEMP_ROOT/manifest.original.json" || {
    echo "FAIL: package manifest was not restored" >&2
    exit 1
  }
  cmp -s "$PROJECT/Packages/packages-lock.json" "$TEMP_ROOT/packages-lock.original.json" || {
    echo "FAIL: package lock was not restored" >&2
    exit 1
  }
}

expect_failure() {
  local mode="$1" expected_status="$2" output="$TEMP_ROOT/$1.output"
  set +e
  FAKE_UNITY_MODE="$mode" \
    UNITY_BIN="$FAKE_UNITY" \
    RUNTIMEFLOW_UNITY_TEST_PROJECT="$PROJECT" \
    RUNTIMEFLOW_VCONTAINER=fork \
    RUNTIMEFLOW_VCONTAINER_FORK=file:/fake-vcontainer \
    "$RUNNER" editmode >"$output" 2>&1
  local actual_status=$?
  set -e
  if [[ "$actual_status" -ne "$expected_status" ]]; then
    cat "$output" >&2
    echo "FAIL: mode '$mode' returned $actual_status; expected $expected_status" >&2
    exit 1
  fi
  assert_restored_packages
}

# An explicitly invalid range must fail before invoking even the fake editor.
MARKER="$TEMP_ROOT/unity-was-launched"
set +e
FAKE_UNITY_MODE=passed \
  FAKE_UNITY_MARKER="$MARKER" \
  UNITY_BIN="$FAKE_UNITY" \
  RUNTIMEFLOW_UNITY_TEST_PROJECT="$PROJECT" \
  RUNTIMEFLOW_CHAOS_BATCHES=0,0 \
  "$RUNNER" editmode >"$TEMP_ROOT/invalid-range.output" 2>&1
invalid_status=$?
set -e
if [[ "$invalid_status" -ne 2 || -e "$MARKER" ]]; then
  cat "$TEMP_ROOT/invalid-range.output" >&2
  echo "FAIL: invalid chaos range was not rejected before Unity launch" >&2
  exit 1
fi
grep -q 'Invalid RUNTIMEFLOW_CHAOS_BATCHES' "$TEMP_ROOT/invalid-range.output"

# A stale result must be removed; a successful Unity exit without new XML is a failure.
mkdir -p "$PROJECT/TestResults"
printf 'stale result\n' > "$PROJECT/TestResults/editmode-results.xml"
expect_failure no-xml 1
if [[ -e "$PROJECT/TestResults/editmode-results.xml" ]]; then
  echo "FAIL: stale test results survived a run that produced no XML" >&2
  exit 1
fi

expect_failure zero 1
grep -q 'selected no tests' "$TEMP_ROOT/zero.output"

expect_failure skipped 1
grep -q 'executed no tests' "$TEMP_ROOT/skipped.output"

expect_failure nonzero 7

echo "PASS: fake Unity runner validation and package restoration checks"
