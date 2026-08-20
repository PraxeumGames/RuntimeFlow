#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project_path="${RUNTIMEFLOW_UNITY_TEST_PROJECT:-"$repo_root/RuntimeFlow.UnityTests"}"
unity_bin="${UNITY_BIN:-/Applications/Unity/Hub/Editor/2022.3.62f2/Unity.app/Contents/MacOS/Unity}"
results_dir="${RUNTIMEFLOW_UNITY_TEST_RESULTS:-"$project_path/TestResults"}"
logs_dir="${RUNTIMEFLOW_UNITY_TEST_LOGS:-"$project_path/Logs"}"

mkdir -p "$results_dir" "$logs_dir"
rm -f "$results_dir/editmode-results.xml"

set +e
"$unity_bin" \
  -batchmode \
  -projectPath "$project_path" \
  -runTests \
  -testPlatform EditMode \
  -testResults "$results_dir/editmode-results.xml" \
  -logFile "$logs_dir/editmode-tests.log"
unity_exit_code=$?
set -e

if [ -f "$results_dir/editmode-results.xml" ]; then
    python3 - "$results_dir/editmode-results.xml" <<'PY' || true
import sys
import xml.etree.ElementTree as ET

tree = ET.parse(sys.argv[1])
total = passed = failed = 0
for tc in tree.iter("test-case"):
    total += 1
    if tc.get("result") == "Passed":
        passed += 1
    else:
        failed += 1
        print("FAILED:", tc.get("fullname"))

print(f"EditMode tests: total={total} passed={passed} failed={failed}")
PY
else
    echo "No test results XML found at $results_dir/editmode-results.xml" >&2
fi

exit "$unity_exit_code"