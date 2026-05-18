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

# Optional Swift step: only runs when Xcode + xcresultparser are present.
# Anyone running coverage on Linux (or without Xcode) gets the .NET-only
# subset, same as before this block existed.
TRAY_PROJECT="$REPO_ROOT/src/Mailvec.Tray/Mailvec.Tray.xcodeproj"
if command -v xcresultparser >/dev/null 2>&1 \
    && [[ -d "$TRAY_PROJECT" ]] \
    && [[ -d /Applications/Xcode.app ]]; then
  echo "==> Running Swift tray tests with coverage"
  # System xcode-select can point at Command Line Tools; force the real
  # Xcode for the duration of the test invocation so xcodebuild resolves.
  TRAY_XCRESULT="$REPO_ROOT/coverage/raw/tray.xcresult"
  rm -rf "$TRAY_XCRESULT"
  # xcresultparser shells out to `xcresulttool`, which lives inside
  # Xcode.app. If the system xcode-select points at Command Line Tools
  # (default on machines that installed Xcode after CLT), DEVELOPER_DIR
  # has to be set for both xcodebuild AND xcresultparser.
  export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
  if xcodebuild test \
       -project "$TRAY_PROJECT" \
       -scheme Mailvec.Tray \
       -destination "platform=macOS" \
       -resultBundlePath "$TRAY_XCRESULT" \
       -quiet >/dev/null 2>&1; then
    # `-t Mailvec.Tray.app` restricts the report to the app target's
    # own source (otherwise xcresultparser also includes the test
    # bundle's source, which would inflate the coverage % artificially).
    # The .app suffix is xcresultparser's internal target naming —
    # `xcresultparser <file>` with no -t lists available names.
    if xcresultparser \
         -o cobertura \
         -t Mailvec.Tray.app \
         -p "$REPO_ROOT" \
         "$TRAY_XCRESULT" > "$REPO_ROOT/coverage/raw/tray.cobertura.raw.xml" 2>/dev/null; then
      # xcresultparser derives Cobertura `package name` and `class name`
      # from the file's directory path, giving us multiple split-out
      # "assemblies" like `src.Mailvec.Tray.Sources` /
      # `src.Mailvec.Tray.Sources.Preferences` /
      # `src.Mailvec.Tray.Sources.Components`, and class names containing
      # the full path. ReportGenerator presents one `<details>` section
      # per package, which fragments the Swift result and bloats every
      # row. Normalise the XML so all Swift coverage rolls up into a
      # single `Mailvec.Tray` assembly with short class names matching
      # the file's basename.
      sed -E \
        -e 's/<package name="src\.Mailvec\.Tray\.Sources[^"]*"/<package name="Mailvec.Tray"/g' \
        -e 's|<class name="src\.Mailvec\.Tray\.Sources[^"]*/Sources/([^"]+)"|<class name="Mailvec.Tray.Sources.\1"|g' \
        -e 's|(<class name="Mailvec\.Tray\.Sources)\.([^"]*)/([^"]+)"|\1.\2.\3"|g' \
        "$REPO_ROOT/coverage/raw/tray.cobertura.raw.xml" \
        > "$REPO_ROOT/coverage/raw/tray.cobertura.xml"
      rm "$REPO_ROOT/coverage/raw/tray.cobertura.raw.xml"
      echo "    Swift coverage: $REPO_ROOT/coverage/raw/tray.cobertura.xml"
    else
      rm -f "$REPO_ROOT/coverage/raw/tray.cobertura.xml" "$REPO_ROOT/coverage/raw/tray.cobertura.raw.xml"
      echo "    warning: xcresultparser failed; Swift coverage skipped" >&2
    fi
  else
    echo "    warning: xcodebuild test failed; Swift coverage skipped" >&2
  fi
else
  echo "==> Skipping Swift coverage (need Xcode + xcresultparser)"
fi

# `--results-directory` puts each test project's xml under coverage/raw/<guid>/,
# and the Swift step (if it ran) drops tray.cobertura.xml alongside.
REPORTS=$( { find "$REPO_ROOT/coverage/raw" -name 'coverage.cobertura.xml' ; \
             find "$REPO_ROOT/coverage/raw" -maxdepth 1 -name 'tray.cobertura.xml' ; \
           } | paste -sd ';' -)
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
