#!/usr/bin/env bash
# Stop running Mailvec services so the database file can be safely deleted
# (or replaced, copied, etc).
#
# Usage:
#   ops/stop.sh                    stop indexer + embedder + mcp launchd agents
#   ops/stop.sh embedder           stop just the embedder
#   ops/stop.sh indexer mcp        stop a subset
#   ops/stop.sh --all              also stop mbsync
#   ops/stop.sh --kill-stdio       also SIGTERM Claude Desktop's stdio MCP processes
#
# What gets stopped:
# - Launchd-managed services (com.mailvec.<svc>): unloaded with `launchctl
#   bootout`. They will NOT auto-restart until you run ops/redeploy.sh or
#   ops/install.sh — `bootout` is the durable counterpart to `kickstart`.
# - mbsync: only stopped with --all. It's IMAP sync only and doesn't touch
#   the SQLite DB, so it's left running by default to avoid an unnecessary
#   gap in incoming-mail indexing.
# - Claude Desktop stdio MCP processes (spawned by the MCPB extension):
#   detected and listed but NOT killed unless --kill-stdio is passed,
#   because Claude Desktop will respawn them and a freshly-killed pair can
#   leave the extension in a zombie state. Quitting Claude Desktop is the
#   clean path; --kill-stdio is the "I'm scripting a teardown" escape hatch.
#
# Common workflow (drop and recreate the DB):
#   1. Quit Claude Desktop (or pass --kill-stdio).
#   2. ops/stop.sh
#   3. rm "$HOME/Library/Application Support/Mailvec/archive.sqlite"*
#   4. ops/redeploy.sh    # republishes binaries + re-bootstraps the agents
set -euo pipefail

trap 'echo "stop.sh: failed at line $LINENO" >&2' ERR

ALL_SERVICES=(indexer embedder mcp)

usage() {
    sed -n '2,/^set -/p' "$0" | sed 's/^# \{0,1\}//' | sed '$d'
}

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
    usage
    exit 0
fi

if [[ "$(uname)" != "Darwin" ]]; then
    echo "stop.sh: macOS only (launchd)" >&2
    exit 1
fi

# ---------------------------------------------------------------------------
# Parse args. --all and --kill-stdio are flags; everything else is a service
# name from ALL_SERVICES. With no args we stop the default three.
# ---------------------------------------------------------------------------
INCLUDE_MBSYNC=0
KILL_STDIO=0
SERVICES=()
for arg in "$@"; do
    case "$arg" in
        --all)         INCLUDE_MBSYNC=1 ;;
        --kill-stdio)  KILL_STDIO=1 ;;
        indexer|embedder|mcp|mbsync) SERVICES+=("$arg") ;;
        *) echo "stop.sh: unknown arg '$arg'. Run with --help for usage." >&2; exit 1 ;;
    esac
done

if (( ${#SERVICES[@]} == 0 )); then
    SERVICES=("${ALL_SERVICES[@]}")
    if (( INCLUDE_MBSYNC == 1 )); then
        SERVICES+=("mbsync")
    fi
fi

# ---------------------------------------------------------------------------
# Bootout each launchd agent. `bootout` is the proper "stop and don't auto-
# respawn" verb; `kill -TERM` would just hand control to KeepAlive=true and
# the agent would come right back.
# ---------------------------------------------------------------------------
NOT_FOUND=()
for svc in "${SERVICES[@]}"; do
    label="com.mailvec.$svc"
    target="gui/$UID/$label"
    if launchctl print "$target" >/dev/null 2>&1; then
        echo "==> bootout $label"
        launchctl bootout "$target" 2>&1 || {
            echo "    bootout returned non-zero (the agent may already be exiting)" >&2
        }
    else
        NOT_FOUND+=("$svc")
    fi
done

if (( ${#NOT_FOUND[@]} > 0 )); then
    echo
    echo "Note: not loaded (nothing to stop): ${NOT_FOUND[*]}" >&2
fi

# ---------------------------------------------------------------------------
# Wait briefly for processes to actually exit. bootout is async — the entries
# linger in `launchctl list` for a moment after the SIGTERM is delivered.
# ---------------------------------------------------------------------------
deadline=$(( $(date +%s) + 5 ))
while (( $(date +%s) < deadline )); do
    if ! launchctl list | grep -qE '^[^#]*com\.mailvec\.(indexer|embedder|mcp)\b'; then
        break
    fi
    sleep 0.5
done

# ---------------------------------------------------------------------------
# Detect Claude Desktop's stdio MCP processes. These are spawned outside
# launchd by the MCPB extension and hold the SQLite file open just like the
# launchd MCP did.
# ---------------------------------------------------------------------------
# `pgrep -f` matches against the full command line. The MCPB-spawned binary
# lives under ~/Library/Application Support/Claude/Claude Extensions/.../Mailvec.Mcp,
# which won't collide with the launchd-published binary path.
STDIO_PIDS=$(pgrep -f "Claude Extensions/.*Mailvec\.Mcp" || true)

if [[ -n "$STDIO_PIDS" ]]; then
    echo
    if (( KILL_STDIO == 1 )); then
        echo "==> SIGTERM Claude Desktop stdio MCP processes:"
        # shellcheck disable=SC2086
        ps -o pid=,command= -p $STDIO_PIDS | sed 's/^/    /' >&2
        # shellcheck disable=SC2086
        kill $STDIO_PIDS 2>/dev/null || true
        # If the parent (Claude Desktop) is still running, it WILL respawn
        # these. The user's already been told this in the help text.
    else
        echo "WARNING: Claude Desktop stdio MCP processes still hold the DB:" >&2
        # shellcheck disable=SC2086
        ps -o pid=,command= -p $STDIO_PIDS | sed 's/^/    /' >&2
        echo >&2
        echo "  Quit Claude Desktop (or disable the Mailvec extension) to release the DB." >&2
        echo "  Or re-run with --kill-stdio (Claude Desktop will respawn them on its next poll)." >&2
    fi
fi

echo
echo "Done."
