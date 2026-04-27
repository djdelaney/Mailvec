#!/usr/bin/env bash
# Stdio-mode launcher for Claude Desktop. Claude Desktop only supports stdio
# in claude_desktop_config.json (HTTP custom connectors require HTTPS, which
# our local server doesn't provide), so this wrapper builds the project and
# execs the compiled DLL with --stdio.
#
# Stdout MUST stay clean — it's the JSON-RPC channel. Build chatter goes to a
# log file; only the failure path forwards build output to stderr.
#
# Defaults to the test paths at ~/mailvec-test; override the env vars to point
# at the production archive once you have one.
set -euo pipefail

# Claude Desktop spawns children with a minimal PATH that omits
# /usr/local/share/dotnet (where the official .NET macOS installer lands).
# Bake the standard install paths in so the script works regardless of how the
# parent is invoked.
export DOTNET_ROOT="${DOTNET_ROOT:-/usr/local/share/dotnet}"
export PATH="$DOTNET_ROOT:/usr/local/bin:/opt/homebrew/bin:$PATH"

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LOG_DIR="${TMPDIR:-/tmp}"
BUILD_LOG="$LOG_DIR/mailvec-mcp-stdio-build.log"

export Archive__DatabasePath="${Archive__DatabasePath:-$HOME/mailvec-test/archive.sqlite}"

cd "$REPO_ROOT"

if ! dotnet build src/Mailvec.Mcp/Mailvec.Mcp.csproj -c Debug --nologo -v quiet > "$BUILD_LOG" 2>&1; then
    cat "$BUILD_LOG" >&2
    echo "build failed; see $BUILD_LOG" >&2
    exit 1
fi

exec dotnet src/Mailvec.Mcp/bin/Debug/net10.0/Mailvec.Mcp.dll --stdio
