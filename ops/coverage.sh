#!/usr/bin/env bash
# Runs the test suite with coverlet and generates an HTML coverage report
# via ReportGenerator. Output lands at coverage/index.html and a Markdown
# summary at coverage/SummaryGithub.md.
#
# Usage:
#   ops/coverage.sh                # full run + report
#   ops/coverage.sh --no-build     # reuse existing build (faster iteration)
#   ops/coverage.sh --open         # open the HTML report when done (macOS)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

OPEN_REPORT=0
TEST_ARGS=()
for arg in "$@"; do
  case "$arg" in
    --open) OPEN_REPORT=1 ;;
    *) TEST_ARGS+=("$arg") ;;
  esac
done

# Coverlet drops one coverage.cobertura.xml per test project under
# tests/<Name>/TestResults/<guid>/. Wipe these before each run so the
# merge step doesn't pick up stale files from prior invocations.
echo "==> Cleaning previous coverage results"
find tests -type d -name TestResults -prune -exec rm -rf {} + 2>/dev/null || true
rm -rf coverage

echo "==> Restoring local dotnet tools (reportgenerator)"
dotnet tool restore >/dev/null

echo "==> Running tests with coverage collection"
dotnet test \
  --collect:"XPlat Code Coverage" \
  --settings "$REPO_ROOT/coverlet.runsettings" \
  --results-directory "$REPO_ROOT/coverage/raw" \
  ${TEST_ARGS[@]+"${TEST_ARGS[@]}"}

# `--results-directory` puts each test project's xml under coverage/raw/<guid>/.
REPORTS=$(find "$REPO_ROOT/coverage/raw" -name 'coverage.cobertura.xml' | paste -sd ';' -)
if [[ -z "$REPORTS" ]]; then
  echo "ERROR: no cobertura xml produced under coverage/raw/" >&2
  exit 1
fi

echo "==> Generating HTML report"
dotnet reportgenerator \
  -reports:"$REPORTS" \
  -targetdir:"$REPO_ROOT/coverage" \
  -reporttypes:"Html;MarkdownSummaryGithub;Cobertura" \
  -title:"Mailvec coverage" \
  -historydir:"$REPO_ROOT/coverage/history" \
  >/dev/null

echo
echo "Coverage report: file://$REPO_ROOT/coverage/index.html"
echo "Markdown summary: $REPO_ROOT/coverage/SummaryGithub.md"

if [[ "$OPEN_REPORT" == "1" ]]; then
  open "$REPO_ROOT/coverage/index.html"
fi
