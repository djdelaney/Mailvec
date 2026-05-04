#!/usr/bin/env bash
# Phase 4 installer for the four launchd-managed services.
#
# Publishes Mailvec.Indexer, Mailvec.Embedder, and Mailvec.Mcp to
# ~/.local/share/mailvec/<service>/, renders the plist templates in
# ops/launchd/ with site-specific paths, drops them in ~/Library/LaunchAgents,
# bootstraps them, and verifies the MCP server's /health endpoint.
#
# Usage:
#   ops/install.sh             install or reinstall (idempotent)
#   ops/install.sh --uninstall remove agents + plists (preserves data)
#
# Design notes:
# - We use framework-dependent publish (no --self-contained). The plist's
#   __DOTNET__ resolves to whichever dotnet is on PATH; bundling the runtime
#   is only needed for distributable artifacts (the MCPB).
# - Per-service publish dirs (indexer/, embedder/, mcp/) keep the published
#   deps for each project from stomping each other. publish-mcp-stdio.sh
#   uses the same mcp/ subdir.
# - Three config knobs (maildir, db path, ollama url) are surfaced as plist
#   env vars rather than baked into appsettings.json so changing them later
#   is a plist edit + reload, not a republish.
set -euo pipefail

trap 'echo "install.sh: failed at line $LINENO" >&2' ERR

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TEMPLATE_DIR="$REPO_ROOT/ops/launchd"

PREFIX="$HOME/.local/share/mailvec"
LOG_DIR="$HOME/Library/Logs/Mailvec"
LAUNCH_AGENTS="$HOME/Library/LaunchAgents"

LABELS=(com.mailvec.mbsync com.mailvec.indexer com.mailvec.embedder com.mailvec.mcp)
SERVICES=(indexer embedder mcp)

MCP_HEALTH_URL="http://127.0.0.1:3333/health"
HEALTH_TIMEOUT=15

usage() {
    cat <<EOF
Usage: $0 [--uninstall]

  (no args)    install or reinstall the four Mailvec launchd agents
  --uninstall  bootout the agents and remove the plists; preserves the
               published binaries, the database, and the log directory
EOF
}

expand_tilde() {
    local p="$1"
    case "$p" in
        '~')   printf '%s' "$HOME" ;;
        '~/'*) printf '%s/%s' "$HOME" "${p#'~/'}" ;;
        *)     printf '%s' "$p" ;;
    esac
}

prompt_with_default() {
    # prompt_with_default <prompt> <default> <var-name>
    local message="$1" default="$2" var="$3" reply
    read -r -p "$message [$default]: " reply
    reply="${reply:-$default}"
    printf -v "$var" '%s' "$reply"
}

bootout_label() {
    # Idempotent: bootout returns nonzero (typically 113 or 36) when the
    # service isn't loaded. We treat that as fine.
    local label="$1"
    launchctl bootout "gui/$UID/$label" >/dev/null 2>&1 || true
}

uninstall() {
    echo "Booting out agents..."
    for label in "${LABELS[@]}"; do
        bootout_label "$label"
    done
    echo "Removing plists from $LAUNCH_AGENTS..."
    for label in "${LABELS[@]}"; do
        rm -f "$LAUNCH_AGENTS/$label.plist"
    done
    echo
    echo "Done. Preserved (delete manually if you want a clean wipe):"
    echo "  $PREFIX"
    echo "  $LOG_DIR"
    echo "  the SQLite database (location depends on your config)"
}

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
    usage
    exit 0
fi

if [[ "${1:-}" == "--uninstall" ]]; then
    uninstall
    exit 0
fi

# ---------------------------------------------------------------------------
# 1. Preflight
# ---------------------------------------------------------------------------
if [[ "$(uname)" != "Darwin" ]]; then
    echo "install.sh: macOS only (launchd plists)" >&2
    exit 1
fi

DOTNET_BIN="$(command -v dotnet || true)"
if [[ -z "$DOTNET_BIN" ]]; then
    echo "install.sh: dotnet not on PATH. Install .NET 10 SDK first." >&2
    exit 1
fi

MBSYNC_BIN="$(command -v mbsync || true)"
if [[ -z "$MBSYNC_BIN" ]]; then
    echo "install.sh: mbsync not on PATH. brew install isync." >&2
    exit 1
fi

VEC_DYLIB="$REPO_ROOT/runtimes/osx-arm64/native/vec0.dylib"
if [[ ! -f "$VEC_DYLIB" ]]; then
    echo "install.sh: $VEC_DYLIB missing. Run ops/fetch-sqlite-vec.sh first." >&2
    exit 1
fi

# ---------------------------------------------------------------------------
# 2. Prompts
# ---------------------------------------------------------------------------
echo "Mailvec installer"
echo "================="
echo "Press Enter to accept defaults."
echo

prompt_with_default "Maildir root" "$HOME/Mail/Fastmail" MAILDIR_ROOT
prompt_with_default "Database path" "$HOME/Library/Application Support/Mailvec/archive.sqlite" DB_PATH
prompt_with_default "Ollama base URL" "http://localhost:11434" OLLAMA_URL
prompt_with_default "mbsync config" "$HOME/.mbsyncrc" MBSYNCRC
prompt_with_default "Fastmail account ID (optional, blank to skip)" "" FASTMAIL_ACCOUNT_ID

MAILDIR_ROOT="$(expand_tilde "$MAILDIR_ROOT")"
DB_PATH="$(expand_tilde "$DB_PATH")"
MBSYNCRC="$(expand_tilde "$MBSYNCRC")"

echo
echo "Will install with:"
echo "  PREFIX            $PREFIX"
echo "  LOG_DIR           $LOG_DIR"
echo "  DOTNET            $DOTNET_BIN"
echo "  MBSYNC            $MBSYNC_BIN"
echo "  Maildir           $MAILDIR_ROOT"
echo "  DB                $DB_PATH"
echo "  Ollama            $OLLAMA_URL"
echo "  mbsync config     $MBSYNCRC"
if [[ -n "$FASTMAIL_ACCOUNT_ID" ]]; then
    echo "  Fastmail acct     $FASTMAIL_ACCOUNT_ID"
fi
echo

# Sanity warnings (non-fatal; user can fix later).
[[ -d "$MAILDIR_ROOT" ]] || echo "warning: Maildir root '$MAILDIR_ROOT' does not exist yet."
[[ -f "$MBSYNCRC" ]]     || echo "warning: mbsync config '$MBSYNCRC' does not exist yet."
if ! curl -fsS --max-time 2 "$OLLAMA_URL/api/tags" >/dev/null 2>&1; then
    echo "warning: Ollama not reachable at $OLLAMA_URL (start it later; embedder will retry)."
    echo "         If installed via Homebrew: brew services start ollama"
fi
# Reachable now ≠ reachable after reboot. brew services manages the launchd
# agent that auto-starts Ollama at login; without it, a fresh boot leaves
# Ollama down and the embedder/MCP /health degraded until you run it manually.
if command -v brew >/dev/null 2>&1 \
   && command -v ollama >/dev/null 2>&1 \
   && [[ "$(command -v ollama)" == "$(brew --prefix)/bin/ollama" ]] \
   && ! brew services list 2>/dev/null | awk '$1=="ollama"{print $2}' | grep -qx started; then
    echo "warning: Homebrew-installed Ollama is not registered with brew services."
    echo "         To make it survive reboot: brew services start ollama"
fi

# ---------------------------------------------------------------------------
# 3. Create directories
# ---------------------------------------------------------------------------
mkdir -p "$PREFIX" "$LOG_DIR" "$LAUNCH_AGENTS"
mkdir -p "$(dirname "$DB_PATH")"

# ---------------------------------------------------------------------------
# 4. Publish each .NET service
# ---------------------------------------------------------------------------
echo
echo "Publishing services..."
for svc in "${SERVICES[@]}"; do
    case "$svc" in
        indexer)  proj=src/Mailvec.Indexer/Mailvec.Indexer.csproj ;;
        embedder) proj=src/Mailvec.Embedder/Mailvec.Embedder.csproj ;;
        mcp)      proj=src/Mailvec.Mcp/Mailvec.Mcp.csproj ;;
    esac
    echo "  -> $svc"
    dotnet publish "$REPO_ROOT/$proj" -c Release -o "$PREFIX/$svc" --nologo -v quiet
done

# ---------------------------------------------------------------------------
# 5. Bootout existing agents (idempotent)
# ---------------------------------------------------------------------------
echo
echo "Booting out any existing agents..."
for label in "${LABELS[@]}"; do
    bootout_label "$label"
done

# ---------------------------------------------------------------------------
# 6. Render plists
# ---------------------------------------------------------------------------
# sed delimiter '|' chosen because it doesn't appear in any substituted value
# (paths can have spaces but not pipes; the URL is plain http(s); the Fastmail
# account ID is alphanumeric).
echo "Rendering plists..."
render_plist() {
    # render_plist <template-name> [extra-sed-arg...]
    local name="$1"; shift
    local src="$TEMPLATE_DIR/$name.plist"
    local dst="$LAUNCH_AGENTS/$name.plist"
    sed \
        -e "s|__DOTNET__|$DOTNET_BIN|g" \
        -e "s|__MBSYNC__|$MBSYNC_BIN|g" \
        -e "s|__MBSYNCRC__|$MBSYNCRC|g" \
        -e "s|__LOG_DIR__|$LOG_DIR|g" \
        -e "s|__DB_PATH__|$DB_PATH|g" \
        -e "s|__MAILDIR_ROOT__|$MAILDIR_ROOT|g" \
        -e "s|__OLLAMA_URL__|$OLLAMA_URL|g" \
        -e "s|__FASTMAIL_ACCOUNT_ID__|$FASTMAIL_ACCOUNT_ID|g" \
        "$@" \
        "$src" > "$dst"
}

render_plist com.mailvec.mbsync
for svc in "${SERVICES[@]}"; do
    render_plist "com.mailvec.$svc" -e "s|__INSTALL_PREFIX__|$PREFIX/$svc|g"
done

# ---------------------------------------------------------------------------
# 7. Bootstrap
# ---------------------------------------------------------------------------
echo "Bootstrapping agents..."
for label in "${LABELS[@]}"; do
    launchctl bootstrap "gui/$UID" "$LAUNCH_AGENTS/$label.plist"
done

# ---------------------------------------------------------------------------
# 8. Health verification
# ---------------------------------------------------------------------------
echo
echo "Waiting for MCP /health (up to ${HEALTH_TIMEOUT}s)..."
deadline=$(( $(date +%s) + HEALTH_TIMEOUT ))
status=""
body=""
while (( $(date +%s) < deadline )); do
    response="$(curl -sS -o /tmp/mailvec-install-health.$$ -w '%{http_code}' "$MCP_HEALTH_URL" 2>/dev/null || true)"
    if [[ "$response" == "200" || "$response" == "503" ]]; then
        status="$response"
        body="$(cat /tmp/mailvec-install-health.$$ 2>/dev/null || true)"
        break
    fi
    sleep 1
done
rm -f /tmp/mailvec-install-health.$$

if [[ "$status" == "200" ]]; then
    echo "MCP healthy (HTTP 200)."
elif [[ "$status" == "503" ]]; then
    echo "MCP up but degraded (HTTP 503). Body:"
    echo "$body"
    echo
    echo "Common cause: Ollama isn't running, or the schema's embedding model"
    echo "doesn't match Ollama:EmbeddingModel. The agents are loaded; the MCP"
    echo "will go green once the dependency is available."
else
    echo "MCP did not respond on $MCP_HEALTH_URL within ${HEALTH_TIMEOUT}s." >&2
    echo "Check ~/Library/Logs/Mailvec/mailvec-mcp-*.log and:" >&2
    echo "  launchctl print gui/$UID/com.mailvec.mcp" >&2
    exit 1
fi

echo
echo "Installed agents:"
for label in "${LABELS[@]}"; do
    echo "  $label"
done
echo
echo "To uninstall: $0 --uninstall"
