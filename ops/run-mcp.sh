#!/usr/bin/env bash
# Starts Mailvec.Mcp pointed at the test Maildir / archive at ~/mailvec-test.
# Defaults match Phase 3 sub-step 2 wiring; override the env vars to point
# elsewhere (e.g. to the production paths once the launchd plist lands).
#
# Usage:
#   ops/run-mcp.sh                  # build + run
#   ops/run-mcp.sh --no-build       # skip build (faster iteration)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

export Archive__DatabasePath="${Archive__DatabasePath:-$HOME/mailvec-test/archive.sqlite}"

cd "$REPO_ROOT"
exec dotnet run --project src/Mailvec.Mcp "$@"
