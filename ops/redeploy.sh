#!/usr/bin/env bash
# Redeploy Mailvec .NET services after a code change.
#
# Republishes each service into ~/.local/share/mailvec/<svc>/ (the same
# prefix install.sh uses) and kickstarts the matching launchd agent so
# the new binary takes effect. Plists are NOT touched — run install.sh
# instead if you've changed plist templates or want to change config
# knobs (maildir, db path, ollama url).
#
# Usage:
#   ops/redeploy.sh                    redeploy all three services
#   ops/redeploy.sh embedder           redeploy just the embedder
#   ops/redeploy.sh indexer mcp        redeploy a subset
#
# Notes:
# - Safe while services are running. macOS pins the old .dll inode for
#   any process that has it mapped, so `dotnet publish` over a live file
#   doesn't disturb the running instance; the kickstart that follows
#   stops it and the replacement picks up the new bytes.
# - mbsync is not a .NET service; this script never touches it.
set -euo pipefail

trap 'echo "redeploy.sh: failed at line $LINENO" >&2' ERR

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PREFIX="$HOME/.local/share/mailvec"
LOG_DIR="$HOME/Library/Logs/Mailvec"
ALL_SERVICES=(indexer embedder mcp)
MCP_HEALTH_URL="http://127.0.0.1:3333/health"
HEALTH_TIMEOUT=15

usage() {
    cat <<EOF
Usage: $0 [service ...]

  (no args)        redeploy all three: indexer embedder mcp
  service [...]    redeploy only the listed services

Run ops/install.sh first if these agents have never been installed.
EOF
}

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
    usage
    exit 0
fi

if [[ "$(uname)" != "Darwin" ]]; then
    echo "redeploy.sh: macOS only (launchd)" >&2
    exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
    echo "redeploy.sh: dotnet not on PATH." >&2
    exit 1
fi

# ---------------------------------------------------------------------------
# Resolve which services to redeploy
# ---------------------------------------------------------------------------
SERVICES=()
if [[ $# -eq 0 ]]; then
    SERVICES=("${ALL_SERVICES[@]}")
else
    for arg in "$@"; do
        case "$arg" in
            indexer|embedder|mcp) SERVICES+=("$arg") ;;
            *) echo "redeploy.sh: unknown service '$arg' (expected one of: ${ALL_SERVICES[*]})" >&2; exit 1 ;;
        esac
    done
fi

project_for() {
    case "$1" in
        indexer)  echo "src/Mailvec.Indexer/Mailvec.Indexer.csproj" ;;
        embedder) echo "src/Mailvec.Embedder/Mailvec.Embedder.csproj" ;;
        mcp)      echo "src/Mailvec.Mcp/Mailvec.Mcp.csproj" ;;
    esac
}

# ---------------------------------------------------------------------------
# Publish + kickstart each service
# ---------------------------------------------------------------------------
for svc in "${SERVICES[@]}"; do
    label="com.mailvec.$svc"
    proj="$(project_for "$svc")"
    out="$PREFIX/$svc"

    echo "==> $svc"
    echo "    publish -> $out"
    dotnet publish "$REPO_ROOT/$proj" -c Release -o "$out" --nologo -v quiet

    echo "    kickstart $label"
    if ! launchctl kickstart -k "gui/$UID/$label" 2>/dev/null; then
        echo "    note: $label is not loaded; run ops/install.sh to register it." >&2
    fi
done

# ---------------------------------------------------------------------------
# Verify MCP /health if it was redeployed (other services have no endpoint)
# ---------------------------------------------------------------------------
if [[ " ${SERVICES[*]} " == *" mcp "* ]]; then
    echo
    echo "Waiting for MCP /health (up to ${HEALTH_TIMEOUT}s)..."
    deadline=$(( $(date +%s) + HEALTH_TIMEOUT ))
    status=""
    body=""
    while (( $(date +%s) < deadline )); do
        response="$(curl -sS -o /tmp/mailvec-redeploy-health.$$ -w '%{http_code}' "$MCP_HEALTH_URL" 2>/dev/null || true)"
        if [[ "$response" == "200" || "$response" == "503" ]]; then
            status="$response"
            body="$(cat /tmp/mailvec-redeploy-health.$$ 2>/dev/null || true)"
            break
        fi
        sleep 1
    done
    rm -f /tmp/mailvec-redeploy-health.$$

    case "$status" in
        200) echo "MCP healthy (HTTP 200)." ;;
        503) echo "MCP up but degraded (HTTP 503): $body" ;;
        *)   echo "MCP did not respond on $MCP_HEALTH_URL within ${HEALTH_TIMEOUT}s." >&2
             echo "Check $LOG_DIR/mailvec-mcp-*.log" >&2
             exit 1 ;;
    esac
fi

echo
echo "Done. Tail logs with:"
for svc in "${SERVICES[@]}"; do
    echo "  tail -F $LOG_DIR/mailvec-$svc-\$(date +%Y%m%d).log"
done
