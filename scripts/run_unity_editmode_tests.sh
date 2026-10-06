#!/usr/bin/env bash
set -euo pipefail

# Runs the RuntimeFlow Unity test suite in batch mode and reports an NUnit summary.
#
# Usage:
#   scripts/run_unity_editmode_tests.sh              # EditMode (default)
#   scripts/run_unity_editmode_tests.sh playmode     # PlayMode
#
# Environment:
#   UNITY_BIN                       path to the Unity executable; when unset, the newest
#                                   editor under /Applications/Unity/Hub/Editor is used (macOS)
#   RUNTIMEFLOW_UNITY_TEST_PROJECT  project path (default: RuntimeFlow.UnityTests)
#   RUNTIMEFLOW_TEST_FILTER         optional -testFilter value (namespace, class or test name)
#   RUNTIMEFLOW_TEST_CATEGORY       optional -testCategory value (for example "Chaos")
#   RUNTIMEFLOW_CHAOS_BATCHES       "first,count": also run the [Category("Chaos")] sweep over chaos
#                                   batches first..first+count-1 (25 seeds each; 0,30 = seeds 0-749).
#                                   Must be nonnegative first and positive count with all seeds in Int32 range.
#                                   Unset, the regular suite runs only the fast chaos subset.
#   RUNTIMEFLOW_VCONTAINER          "upstream" (default: the project manifest as committed) or "fork":
#                                   temporarily point jp.hadashikick.vcontainer at the Bezarius fork
#                                   sfs-client ships (1.19.0), run, and restore Packages/manifest.json and
#                                   Packages/packages-lock.json afterwards (also on failure or Ctrl-C).
#   RUNTIMEFLOW_VCONTAINER_FORK     override the fork's package URL (a git URL or a file: path to a local
#                                   copy, e.g. file:/path/to/jp.hadashikick.vcontainer@033b44e9f30d)

TEST_MODE="${1:-editmode}"
case "$TEST_MODE" in
  editmode|playmode) ;;
  *) echo "Unknown test mode: $TEST_MODE (use 'editmode' or 'playmode')" >&2; exit 2 ;;
esac

PROJECT_PATH="${RUNTIMEFLOW_UNITY_TEST_PROJECT:-RuntimeFlow.UnityTests}"

# Keep this contract aligned with RestartChaosTests.ParseSweepBatches. Validate before Unity starts so a
# typo or range that overflows chaos seed calculations cannot silently turn into a skipped sweep.
if [[ ${RUNTIMEFLOW_CHAOS_BATCHES+x} == x ]]; then
  python3 - "$RUNTIMEFLOW_CHAOS_BATCHES" <<'PYEOF'
import re
import sys

value = sys.argv[1]
match = re.fullmatch(r"([0-9]+),([0-9]+)", value)
int32_max = 2**31 - 1
batch_size = 25
max_batch = (int32_max - (batch_size - 1)) // batch_size
if match is None:
    valid = False
else:
    first, count = map(int, match.groups())
    valid = (
        first <= int32_max
        and 0 < count <= int32_max
        and first + count - 1 <= max_batch
    )
if not valid:
    print(
        f"ERROR: Invalid RUNTIMEFLOW_CHAOS_BATCHES value {value!r}. "
        f"Use 'first,count' with 0 <= first, count > 0, and last batch <= {max_batch}; "
        f"each batch has {batch_size} seeds that must fit in Int32.",
        file=sys.stderr,
    )
    sys.exit(2)
PYEOF
fi

# The VContainer fork sfs-client pins (Packages/manifest.json of sfs-client).
FORK_URL_DEFAULT="https://github.com/Bezarius/VContainer.git?path=VContainer/Assets/VContainer#253e998cc5d6f74d161f728d429962e51a55f875"
VCONTAINER_FLAVOUR="${RUNTIMEFLOW_VCONTAINER:-upstream}"
case "$VCONTAINER_FLAVOUR" in
  upstream|fork) ;;
  *) echo "Unknown RUNTIMEFLOW_VCONTAINER: $VCONTAINER_FLAVOUR (use 'upstream' or 'fork')" >&2; exit 2 ;;
esac

if [[ -z "${UNITY_BIN:-}" ]]; then
  PROJECT_VERSION=""
  if [[ -f "$PROJECT_PATH/ProjectSettings/ProjectVersion.txt" ]]; then
    PROJECT_VERSION=$(sed -n 's/^m_EditorVersion: //p' "$PROJECT_PATH/ProjectSettings/ProjectVersion.txt" | head -1)
  fi
  if [[ "$(uname)" == "Darwin" ]]; then
    # Prefer the editor matching the project version; fall back to the newest stable-looking one.
    if [[ -n "$PROJECT_VERSION" ]]; then
      UNITY_BIN="/Applications/Unity/Hub/Editor/$PROJECT_VERSION/Unity.app/Contents/MacOS/Unity"
      [[ -x "$UNITY_BIN" ]] || UNITY_BIN=""
    fi
    if [[ -z "$UNITY_BIN" ]]; then
      UNITY_BIN=$(ls -d /Applications/Unity/Hub/Editor/*/Unity.app/Contents/MacOS/Unity 2>/dev/null | grep -vE '/[0-9]+\.[0-9]+\.[0-9]+[ab][0-9]+/' | sort -V | tail -1 || true)
    fi
    if [[ -z "$UNITY_BIN" ]]; then
      UNITY_BIN=$(ls -d /Applications/Unity/Hub/Editor/*/Unity.app/Contents/MacOS/Unity 2>/dev/null | sort -V | tail -1 || true)
    fi
  fi
  if [[ -z "$UNITY_BIN" ]]; then
    echo "ERROR: UNITY_BIN is not set and no Unity editor was found automatically." >&2
    if [[ -n "$PROJECT_VERSION" ]]; then
      echo "Project requires Unity $PROJECT_VERSION (see $PROJECT_PATH/ProjectSettings/ProjectVersion.txt)." >&2
    fi
    echo "Set UNITY_BIN=/path/to/Unity and retry." >&2
    exit 1
  fi
fi

if [[ ! -x "$UNITY_BIN" ]]; then
  echo "ERROR: Unity executable not found or not executable: $UNITY_BIN" >&2
  exit 1
fi

RESULTS_DIR="$PROJECT_PATH/TestResults"
LOG_DIR="$PROJECT_PATH/Logs"
mkdir -p "$RESULTS_DIR" "$LOG_DIR"
# Unity resolves relative -testResults/-logFile paths against the project folder,
# so hand it absolute paths and keep the friendly relative ones for logging.
RESULTS_XML="$(cd "$(dirname "$PROJECT_PATH")" && pwd)/$(basename "$PROJECT_PATH")/TestResults/${TEST_MODE}-results.xml"
LOG_FILE="$(cd "$(dirname "$PROJECT_PATH")" && pwd)/$(basename "$PROJECT_PATH")/Logs/${TEST_MODE}-tests.log"

FILTER_ARGS=()
if [[ -n "${RUNTIMEFLOW_TEST_FILTER:-}" ]]; then
  FILTER_ARGS=(-testFilter "$RUNTIMEFLOW_TEST_FILTER")
fi
if [[ -n "${RUNTIMEFLOW_TEST_CATEGORY:-}" ]]; then
  FILTER_ARGS+=(-testCategory "$RUNTIMEFLOW_TEST_CATEGORY")
fi

MANIFEST="$PROJECT_PATH/Packages/manifest.json"
LOCK="$PROJECT_PATH/Packages/packages-lock.json"
BACKUP_DIR=""
restore_manifest() {
  if [[ -n "$BACKUP_DIR" && -d "$BACKUP_DIR" ]]; then
    cp "$BACKUP_DIR/manifest.json" "$MANIFEST"
    if [[ -f "$BACKUP_DIR/packages-lock.json" ]]; then
      cp "$BACKUP_DIR/packages-lock.json" "$LOCK"
    else
      rm -f "$LOCK"
    fi
    rm -rf "$BACKUP_DIR"
    BACKUP_DIR=""
    echo "Restored $MANIFEST (VContainer back to the committed pin)."
  fi
}
if [[ "$VCONTAINER_FLAVOUR" == "fork" ]]; then
  FORK_URL="${RUNTIMEFLOW_VCONTAINER_FORK:-$FORK_URL_DEFAULT}"
  BACKUP_DIR=$(mktemp -d "${TMPDIR:-/tmp}/runtimeflow-manifest.XXXXXX")
  cp "$MANIFEST" "$BACKUP_DIR/manifest.json"
  [[ -f "$LOCK" ]] && cp "$LOCK" "$BACKUP_DIR/packages-lock.json"
  # Restore on every exit path: success, failure, set -e aborts, Ctrl-C, kill.
  trap restore_manifest EXIT
  trap 'restore_manifest; exit 130' INT
  trap 'restore_manifest; exit 143' TERM
  python3 - "$MANIFEST" "$FORK_URL" <<'PYEOF'
import json, sys
path, url = sys.argv[1], sys.argv[2]
with open(path) as f:
    manifest = json.load(f)
manifest["dependencies"]["jp.hadashikick.vcontainer"] = url
with open(path, "w") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")
PYEOF
  # The lock pins the resolved upstream commit; drop the entry so UPM resolves the fork.
  if [[ -f "$LOCK" ]]; then
    python3 - "$LOCK" <<'PYEOF'
import json, sys
path = sys.argv[1]
with open(path) as f:
    lock = json.load(f)
lock.get("dependencies", {}).pop("jp.hadashikick.vcontainer", None)
with open(path, "w") as f:
    json.dump(lock, f, indent=2)
    f.write("\n")
PYEOF
  fi
  echo "VContainer: fork ($FORK_URL) for this run only."
fi

# A results file left over from an earlier run must never be mistaken for this run's (a crash writes none).
rm -f "$RESULTS_XML"

echo "Running Unity $TEST_MODE tests (VContainer: $VCONTAINER_FLAVOUR) with: $UNITY_BIN"
"$UNITY_BIN" \
  -batchmode \
  -nographics \
  -projectPath "$PROJECT_PATH" \
  -runTests \
  -testPlatform "$TEST_MODE" \
  -testResults "$RESULTS_XML" \
  ${FILTER_ARGS[@]+"${FILTER_ARGS[@]}"} \
  -logFile "$LOG_FILE" || UNITY_EXIT_CODE=$?

UNITY_EXIT_CODE=${UNITY_EXIT_CODE:-0}

RESOLVED_VCONTAINER=$(ls -d "$PROJECT_PATH"/Library/PackageCache/jp.hadashikick.vcontainer@* 2>/dev/null | head -1 || true)
if [[ -n "$RESOLVED_VCONTAINER" && -f "$RESOLVED_VCONTAINER/package.json" ]]; then
  echo "Resolved VContainer: $(basename "$RESOLVED_VCONTAINER") version $(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$RESOLVED_VCONTAINER/package.json" | head -1)"
fi

if [[ ! -f "$RESULTS_XML" ]]; then
  echo "No test results XML found at $RESULTS_XML" >&2
  echo "See log: $LOG_FILE" >&2
  grep -E "error CS|Aborting batchmode" "$LOG_FILE" | sort -u | head -20 >&2 || true
  exit $(( UNITY_EXIT_CODE != 0 ? UNITY_EXIT_CODE : 1 ))
fi

# A stale results XML can coexist with a failed compilation (Bee keeps old assemblies
# and the runner may still execute them). Compilation errors are authoritative.
if grep -qE "Scripts have compiler errors|Script Compilation Error" "$LOG_FILE"; then
  echo "COMPILATION ERRORS detected in $LOG_FILE; any executed tests ran against stale assemblies." >&2
  grep -E "error CS" "$LOG_FILE" | sed 's/.*Assets/Assets/' | sort -u | head -10 >&2 || true
  exit $(( UNITY_EXIT_CODE != 0 ? UNITY_EXIT_CODE : 1 ))
fi

PY_EXIT=0
python3 - "$RESULTS_XML" <<'PYEOF' || PY_EXIT=$?
import sys
import xml.etree.ElementTree as ET

tree = ET.parse(sys.argv[1])
root = tree.getroot()
total = int(root.get("total", 0))
passed = int(root.get("passed", 0))
failed = int(root.get("failed", 0))
skipped = int(root.get("skipped", 0)) + int(root.get("inconclusive", 0))
print(f"{root.get('name', 'Unity tests')}: total={total} passed={passed} failed={failed} skipped={skipped}")
if total <= 0 or passed + failed <= 0:
    print("ERROR: Unity selected no tests or executed no tests (all selected tests were skipped/inconclusive).", file=sys.stderr)
    sys.exit(1)
for case in root.iter("test-case"):
    if case.get("result") not in ("Passed", "Skipped"):
        print(f"FAILED: {case.get('fullname')}")
        failure = case.find("failure")
        if failure is not None:
            message = failure.find("message")
            if message is not None and message.text:
                for line in message.text.strip().splitlines()[:5]:
                    print(f"    {line}")
sys.exit(1 if failed > 0 else 0)
PYEOF

echo "Results: $RESULTS_XML"
if [[ "$UNITY_EXIT_CODE" -ne 0 ]]; then
  echo "Unity exited with code $UNITY_EXIT_CODE" >&2
  exit "$UNITY_EXIT_CODE"
fi
exit "$PY_EXIT"
