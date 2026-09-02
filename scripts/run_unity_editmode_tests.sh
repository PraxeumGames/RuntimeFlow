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

TEST_MODE="${1:-editmode}"
case "$TEST_MODE" in
  editmode|playmode) ;;
  *) echo "Unknown test mode: $TEST_MODE (use 'editmode' or 'playmode')" >&2; exit 2 ;;
esac

PROJECT_PATH="${RUNTIMEFLOW_UNITY_TEST_PROJECT:-RuntimeFlow.UnityTests}"

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

echo "Running Unity $TEST_MODE tests with: $UNITY_BIN"
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

if [[ ! -f "$RESULTS_XML" ]]; then
  echo "No test results XML found at $RESULTS_XML" >&2
  echo "See log: $LOG_FILE" >&2
  grep -E "error CS|Aborting batchmode" "$LOG_FILE" | sort -u | head -20 >&2 || true
  exit "$UNITY_EXIT_CODE"
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
