#!/usr/bin/env bash
# Stdio-mode launcher for Claude Desktop. Claude Desktop only supports stdio
# in claude_desktop_config.json (HTTP custom connectors require HTTPS, which
# our local server doesn't provide), so this wrapper builds the project and
# execs the compiled DLL with --stdio.
#
# Stdout MUST stay clean — it's the JSON-RPC channel. Build chatter goes to a
# log file; only the failure path forwards build output to stderr.
#
# This is the DEV-ITERATION launcher: it builds Debug from the working tree, so
# it reflects uncommitted changes. For wiring a real client up, use
# ops/install-stdio-launcher.sh instead — it publishes Release and writes a
# stable shim at ~/.local/bin/mailvec-mcp-stdio.
#
# Config comes from the shared appsettings.Local.json like every other Mailvec
# binary. It deliberately sets NO Archive__DatabasePath: env vars are the
# highest-precedence source, so a default here would OVERRIDE the shared file —
# and it used to default to ~/mailvec-test/archive.sqlite, a path that on most
# machines doesn't exist, which SchemaMigrator then silently creates as an
# empty archive. That reads as "my mail vanished". Point it at a throwaway DB
# by exporting Archive__DatabasePath yourself (docs/dev-walkthrough.md).
set -euo pipefail

# Claude Desktop spawns children with a minimal PATH that omits
# /usr/local/share/dotnet (where the official .NET macOS installer lands).
# Bake the standard install paths in so the script works regardless of how the
# parent is invoked.
export DOTNET_ROOT="${DOTNET_ROOT:-/usr/local/share/dotnet}"
export PATH="$DOTNET_ROOT:/usr/local/bin:/opt/homebrew/bin:$PATH"

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# The build log keeps a STABLE name on purpose — the failure path below tells
# the developer to go read it — so mktemp is the wrong tool here, and the
# trailing exec means a cleanup trap would never fire anyway. Harden the
# directory instead of randomising the file: on macOS $TMPDIR is already a
# per-user 0700 directory, but the bare /tmp fallback is shared and world-
# writable, where a stable filename is pre-plantable as a symlink that the
# build redirect below would follow and truncate. Give the fallback its own
# owner-only directory so the name can stay predictable safely.
if [[ -n "${TMPDIR:-}" ]]; then
    LOG_DIR="$TMPDIR"
else
    LOG_DIR="/tmp/mailvec-$(id -u)"
    mkdir -p "$LOG_DIR"
    chmod 700 "$LOG_DIR"
fi
BUILD_LOG="$LOG_DIR/mailvec-mcp-stdio-build.log"

cd "$REPO_ROOT"

if ! dotnet build src/Mailvec.Mcp/Mailvec.Mcp.csproj -c Debug --nologo -v quiet > "$BUILD_LOG" 2>&1; then
    cat "$BUILD_LOG" >&2
    echo "build failed; see $BUILD_LOG" >&2
    exit 1
fi

exec dotnet src/Mailvec.Mcp/bin/Debug/net10.0/Mailvec.Mcp.dll --stdio
