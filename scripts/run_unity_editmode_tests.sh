#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project_path="${RUNTIMEFLOW_UNITY_TEST_PROJECT:-"$repo_root/RuntimeFlow.UnityTests"}"
unity_bin="${UNITY_BIN:-/Applications/Unity/Hub/Editor/2022.3.62f2/Unity.app/Contents/MacOS/Unity}"
results_dir="${RUNTIMEFLOW_UNITY_TEST_RESULTS:-"$project_path/TestResults"}"
logs_dir="${RUNTIMEFLOW_UNITY_TEST_LOGS:-"$project_path/Logs"}"

mkdir -p "$results_dir" "$logs_dir"

"$unity_bin" \
  -batchmode \
  -projectPath "$project_path" \
  -runTests \
  -testPlatform EditMode \
  -testResults "$results_dir/editmode-results.xml" \
  -logFile "$logs_dir/editmode-tests.log"
