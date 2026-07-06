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
# - User config (DB path, Maildir root, Ollama URL, Fastmail account id)
#   lives in a single shared file at
#   ~/Library/Application Support/Mailvec/appsettings.Local.json. Both the
#   launchd-installed services AND the MCPB-bundled MCP read from it. On
#   reinstall we migrate values out of any pre-existing launchd plist so
#   no one re-enters the same settings in two places.
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
Usage: $0 [--uninstall] [--defaults]

  (no args)    install or reinstall the four Mailvec launchd agents
  --defaults   answer every prompt with its default (on a reinstall the
               defaults are your existing configured values, so this is
               also the unattended-upgrade mode). Implied when stdin is
               not a terminal.
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
    # --defaults, or stdin isn't a terminal (CI, `curl | bash`, automation):
    # take the default instead of prompting. Previously `read` hit EOF under
    # `set -e` and the ERR trap printed only "failed at line NN" — a dead end
    # with no hint that the script wanted a terminal.
    if [[ "${NONINTERACTIVE:-0}" -eq 1 || ! -t 0 ]]; then
        echo "$message [$default]  (using default)"
        printf -v "$var" '%s' "$default"
        return 0
    fi
    read -r -p "$message [$default]: " reply
    reply="${reply:-$default}"
    printf -v "$var" '%s' "$reply"
}

# Read a value from the shared appsettings file if it exists.
# Args: <Section> <Key>. Prints the value or empty string.
read_shared_config() {
    local section="$1" key="$2"
    local path="$HOME/Library/Application Support/Mailvec/appsettings.Local.json"
    [[ -f "$path" ]] || return 0
    python3 - "$path" "$section" "$key" <<'PY' 2>/dev/null || true
import json, sys
try:
    with open(sys.argv[1]) as f:
        data = json.load(f)
    val = data.get(sys.argv[2], {}).get(sys.argv[3], "")
    print(val)
except Exception:
    pass
PY
}

# Read an env-var value from an existing launchd plist's EnvironmentVariables
# dict. Used to migrate forward from the pre-shared-config layout where these
# keys lived in the plist. Args: <plist-name> <env-var-name>.
read_plist_env() {
    local plist="$LAUNCH_AGENTS/$1.plist" var="$2"
    [[ -f "$plist" ]] || return 0
    /usr/bin/plutil -extract "EnvironmentVariables.$var" raw -o - "$plist" 2>/dev/null || true
}

# Resolve the default for a prompt by trying (in order): shared file, legacy
# plist, then the hard-coded fallback. Args: <Section> <Key> <plist-name>
# <plist-env-var> <hardcoded-default>.
detect_default() {
    local v
    v="$(read_shared_config "$1" "$2")"
    [[ -n "$v" ]] && { printf '%s' "$v"; return; }
    v="$(read_plist_env "$3" "$4")"
    [[ -n "$v" ]] && { printf '%s' "$v"; return; }
    printf '%s' "$5"
}

bootout_label() {
    # bootout's exit code is unreliable (typically 113 or 36 when not loaded,
    # which we ignore) AND the unload itself is asynchronous — the call can
    # return before launchd has finished tearing the service down. Without a
    # poll, a fast subsequent `bootstrap` hits "5: Input/output error" because
    # the old instance is still registered in the domain.
    local label="$1"
    launchctl bootout "gui/$UID/$label" >/dev/null 2>&1 || true
    local deadline=$(( $(date +%s) + 5 ))
    while launchctl print "gui/$UID/$label" >/dev/null 2>&1; do
        if (( $(date +%s) >= deadline )); then
            echo "warning: $label still loaded after 5s; bootstrap may fail" >&2
            return 0
        fi
        sleep 0.2
    done
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

NONINTERACTIVE=0
for arg in "$@"; do
    case "$arg" in
        --help|-h) usage; exit 0 ;;
        --uninstall) uninstall; exit 0 ;;
        --defaults) NONINTERACTIVE=1 ;;
        *)
            echo "install.sh: unknown argument '$arg'" >&2
            usage
            exit 2
            ;;
    esac
done

# ---------------------------------------------------------------------------
# 1. Preflight
# ---------------------------------------------------------------------------
if [[ "$(uname)" != "Darwin" ]]; then
    echo "install.sh: macOS only (launchd plists)" >&2
    exit 1
fi

if [[ "$(uname -m)" != "arm64" ]]; then
    echo "install.sh: Apple Silicon required — Intel Macs are not supported." >&2
    echo "(macOS is dropping Intel support in its next release; Mailvec targets arm64 only.)" >&2
    exit 1
fi

DOTNET_BIN="$(command -v dotnet || true)"
if [[ -z "$DOTNET_BIN" ]]; then
    echo "install.sh: dotnet not on PATH. Install .NET 10 SDK first." >&2
    exit 1
fi

# The installer itself is a python3 consumer (config read/write, model
# check). On a truly fresh macOS the /usr/bin/python3 stub pops the CLT
# install dialog mid-script — check up front with a named fix instead.
if ! command -v python3 >/dev/null 2>&1; then
    echo "install.sh: python3 not found. Install the Xcode Command Line Tools (xcode-select --install)." >&2
    exit 1
fi

# `command -v dotnet` alone accepted a machine with only .NET 8/9, which
# then died mid-publish with NETSDK1045 — loud, but not "install .NET 10".
if ! dotnet --list-sdks 2>/dev/null | grep -q "^10\."; then
    echo "install.sh: no .NET 10 SDK found (dotnet is $DOTNET_BIN). Install it: brew install --cask dotnet-sdk" >&2
    exit 1
fi

# Warn-only, matching doctor's stance: Mailvec never invokes mbsync at
# request time — it reads the Maildir mbsync writes. A user with an
# existing Maildir (or the dev IMAP fetcher) shouldn't be blocked.
MBSYNC_BIN="$(command -v mbsync || true)"
if [[ -z "$MBSYNC_BIN" ]]; then
    echo "warning: mbsync not on PATH (brew install isync). Continuing — the mbsync"
    echo "         launchd agent will be installed but can't run until it's present."
    MBSYNC_BIN="/opt/homebrew/bin/mbsync"   # plist placeholder; fixed on reinstall
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

# Defaults are detected in priority order: shared file → legacy plist env var → hard-coded.
# This means reinstalling on a machine that already has the values configured
# (either via the new shared file or the older per-plist env vars) doesn't make
# you re-type them — hit Enter to accept what we found.
DEFAULT_MAILDIR="$(detect_default Ingest MaildirRoot com.mailvec.indexer Ingest__MaildirRoot "$HOME/Mail/Fastmail")"
DEFAULT_DB="$(detect_default Archive DatabasePath com.mailvec.indexer Archive__DatabasePath "$HOME/Library/Application Support/Mailvec/archive.sqlite")"
DEFAULT_OLLAMA="$(detect_default Ollama BaseUrl com.mailvec.embedder Ollama__BaseUrl "http://localhost:11434")"
DEFAULT_FASTMAIL="$(detect_default Fastmail AccountId com.mailvec.mcp Fastmail__AccountId "")"

prompt_with_default "Maildir root" "$DEFAULT_MAILDIR" MAILDIR_ROOT
prompt_with_default "Database path" "$DEFAULT_DB" DB_PATH
prompt_with_default "Ollama base URL" "$DEFAULT_OLLAMA" OLLAMA_URL
prompt_with_default "mbsync config" "$HOME/.mbsyncrc" MBSYNCRC
prompt_with_default "Fastmail account ID (optional, blank to skip)" "$DEFAULT_FASTMAIL" FASTMAIL_ACCOUNT_ID

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

# Is Ollama local (loopback) or a remote server on the network? The guidance
# below differs: for a local install we can nudge the user to install/autostart
# the app; for a remote server none of that applies (autostart, the cask, and
# brew services are all the remote host's concern, not this machine's).
OLLAMA_HOST="$(printf '%s' "$OLLAMA_URL" | sed -E 's#^[a-zA-Z]+://##; s#[:/].*$##')"
case "$OLLAMA_HOST" in
    localhost|127.0.0.1|::1|"") OLLAMA_IS_LOCAL=1 ;;
    *)                          OLLAMA_IS_LOCAL=0 ;;
esac

OLLAMA_TAGS_JSON="$(curl -fsS --max-time 2 "$OLLAMA_URL/api/tags" 2>/dev/null || true)"
if [[ -z "$OLLAMA_TAGS_JSON" ]]; then
    echo "warning: Ollama not reachable at $OLLAMA_URL (start it later; embedder will retry)."
    if [[ "$OLLAMA_IS_LOCAL" == 1 ]]; then
        echo "         Install the cask if you haven't: brew install --cask ollama-app && open -a Ollama"
    else
        echo "         This is a remote server ($OLLAMA_HOST). Confirm it's running and"
        echo "         that it listens on all interfaces (OLLAMA_HOST=0.0.0.0 on that host),"
        echo "         and that no firewall blocks its port. The embedder will keep retrying."
    fi
else
    # Ollama answered — verify the embedding model is actually pulled.
    # Skipping `ollama pull` is the easiest Quickstart step to miss, and
    # without this check the symptom is a permanently degraded /health whose
    # "unreachable"-flavoured hints point at the wrong fix.
    EMBED_MODEL="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["Ollama"]["EmbeddingModel"])' \
        "$REPO_ROOT/src/Mailvec.Embedder/appsettings.json" 2>/dev/null || echo "mxbai-embed-large")"
    if ! printf '%s' "$OLLAMA_TAGS_JSON" | python3 -c '
import json, sys
want = sys.argv[1].lower()
models = [(m.get("name") or "").lower() for m in json.load(sys.stdin).get("models", [])]
sys.exit(0 if any(n == want or n.startswith(want + ":") for n in models) else 1)
' "$EMBED_MODEL" 2>/dev/null; then
        echo "warning: embedding model '$EMBED_MODEL' is not pulled on $OLLAMA_URL —"
        echo "         embedding and semantic search won't work until it is."
        if [[ "$OLLAMA_IS_LOCAL" == 1 ]]; then
            echo "         Run: ollama pull $EMBED_MODEL"
        else
            echo "         Run \`ollama pull $EMBED_MODEL\` on the remote host ($OLLAMA_HOST)."
        fi
    fi
fi

if [[ "$OLLAMA_IS_LOCAL" == 1 ]]; then
    # Reachable now ≠ reachable after reboot. The recommended cask (ollama-app)
    # auto-starts via its own Login Item, so /Applications/Ollama.app surviving
    # reboot is the app's job, not ours — skip the check entirely for cask installs.
    # Only the legacy `ollama` *formula* relies on brew services for autostart, so
    # the warning below is scoped to that case.
    if [[ ! -d /Applications/Ollama.app ]] \
       && command -v brew >/dev/null 2>&1 \
       && command -v ollama >/dev/null 2>&1 \
       && [[ "$(command -v ollama)" == "$(brew --prefix)/bin/ollama" ]] \
       && ! brew services list 2>/dev/null | awk '$1=="ollama"{print $2}' | grep -qx started; then
        echo "warning: formula-installed Ollama is not registered with brew services."
        echo "         Either switch to the cask (brew install --cask ollama-app) or, to"
        echo "         make the formula survive reboot: brew services start ollama"
    fi
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
# Stable Developer ID signing so TCC grants survive a republish; ad-hoc
# fallback when no cert. See ops/sign-publish.sh.
source "$REPO_ROOT/ops/sign-publish.sh"
for svc in "${SERVICES[@]}"; do
    case "$svc" in
        indexer)  proj=src/Mailvec.Indexer/Mailvec.Indexer.csproj; apphost=Mailvec.Indexer ;;
        embedder) proj=src/Mailvec.Embedder/Mailvec.Embedder.csproj; apphost=Mailvec.Embedder ;;
        mcp)      proj=src/Mailvec.Mcp/Mailvec.Mcp.csproj; apphost=Mailvec.Mcp ;;
    esac
    echo "  -> $svc"
    dotnet publish "$REPO_ROOT/$proj" -c Release -o "$PREFIX/$svc" --nologo -v quiet
    mailvec_sign_publish "$PREFIX/$svc" "$apphost" "com.mailvec.$svc"
done

# Publish the CLI to $PREFIX/cli/ and install a shim at ~/.local/bin/mailvec.
# Without this, every "Doctor / Reindex / Checkpoint / etc." button in the
# tray UI silently fails — the tray spawns `mailvec <args>` via AppleScript
# and there's no global mailvec binary on PATH by default. The shim resolves
# DOTNET_ROOT (Claude Desktop's spawned children get a sanitized PATH that
# excludes /usr/local/share/dotnet) before exec'ing the dll.
echo "  -> cli"
dotnet publish "$REPO_ROOT/src/Mailvec.Cli/Mailvec.Cli.csproj" -c Release -o "$PREFIX/cli" --nologo -v quiet
mailvec_sign_publish "$PREFIX/cli" "Mailvec.Cli" "com.mailvec.cli"

SHIM_DIR="$HOME/.local/bin"
SHIM_PATH="$SHIM_DIR/mailvec"
mkdir -p "$SHIM_DIR"
DOTNET_BIN="$(command -v dotnet || true)"
if [[ -z "$DOTNET_BIN" ]]; then
    DOTNET_BIN="/usr/local/share/dotnet/dotnet"
fi
DOTNET_DIR="$(dirname "$DOTNET_BIN")"
cat > "$SHIM_PATH" <<SHIM
#!/usr/bin/env bash
# Generated by ops/install.sh — re-run the installer to refresh.
DOTNET_BIN="$DOTNET_BIN"
# The baked path can go stale (cask<->formula switch, brew cleanup).
# Fall back to PATH before giving up with a pointer at the fix.
if [[ ! -x "\$DOTNET_BIN" ]]; then
    DOTNET_BIN="\$(command -v dotnet || true)"
fi
if [[ -z "\$DOTNET_BIN" || ! -x "\$DOTNET_BIN" ]]; then
    echo "mailvec: dotnet runtime not found (installer baked $DOTNET_BIN, since removed)." >&2
    echo "mailvec: re-run ops/install.sh (or ops/redeploy.sh cli) after reinstalling the SDK." >&2
    exit 127
fi
export DOTNET_ROOT="\$(dirname "\$DOTNET_BIN")"
export PATH="\$DOTNET_ROOT:\$PATH"
exec "\$DOTNET_BIN" "$PREFIX/cli/Mailvec.Cli.dll" "\$@"
SHIM
chmod +x "$SHIM_PATH"
echo "  -> cli shim at $SHIM_PATH"

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
    # User config (DB / Maildir / Ollama / Fastmail) used to be substituted
    # in here too. It's now written to the shared appsettings.Local.json
    # below and the plists don't carry those keys at all.
    local name="$1"; shift
    local src="$TEMPLATE_DIR/$name.plist"
    local dst="$LAUNCH_AGENTS/$name.plist"
    sed \
        -e "s|__DOTNET__|$DOTNET_BIN|g" \
        -e "s|__MBSYNC__|$MBSYNC_BIN|g" \
        -e "s|__MBSYNCRC__|$MBSYNCRC|g" \
        -e "s|__LOG_DIR__|$LOG_DIR|g" \
        "$@" \
        "$src" > "$dst"
}

render_plist com.mailvec.mbsync
for svc in "${SERVICES[@]}"; do
    render_plist "com.mailvec.$svc" -e "s|__INSTALL_PREFIX__|$PREFIX/$svc|g"
done

# Write the shared appsettings.Local.json. This is the single source of
# truth for user-specific config — read by the launchd-installed indexer /
# embedder / mcp services, the CLI, AND the MCPB-bundled MCP that Claude
# Desktop runs. Created with parents if missing; existing files get
# overwritten with the just-prompted values.
SHARED_CONFIG="$HOME/Library/Application Support/Mailvec/appsettings.Local.json"
mkdir -p "$(dirname "$SHARED_CONFIG")"
# python3 produces correctly-escaped JSON — safer than building the file
# with shell quoting when paths might contain spaces or special chars.
python3 - "$SHARED_CONFIG" "$DB_PATH" "$MAILDIR_ROOT" "$OLLAMA_URL" "$FASTMAIL_ACCOUNT_ID" <<'PY'
import json, sys
out_path, db, maildir, ollama, fastmail = sys.argv[1:6]
doc = {
    "Archive":  {"DatabasePath": db},
    "Ingest":   {"MaildirRoot":  maildir},
    "Ollama":   {"BaseUrl":      ollama},
    "Fastmail": {"AccountId":    fastmail},
}
with open(out_path, "w") as f:
    json.dump(doc, f, indent=2)
    f.write("\n")
PY
echo "Wrote shared config: $SHARED_CONFIG"

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
    echo "warning: MCP did not respond on $MCP_HEALTH_URL within ${HEALTH_TIMEOUT}s." >&2
    echo "         A slow cold start can exceed this window — the doctor preflight below" >&2
    echo "         re-probes it. If that fails too, check ~/Library/Logs/Mailvec/mailvec-mcp-*.log" >&2
    echo "         and: launchctl print gui/$UID/com.mailvec.mcp" >&2
fi

# ---------------------------------------------------------------------------
# 9. Full preflight via `mailvec doctor`
# ---------------------------------------------------------------------------
# /health above is MCP-only. Doctor covers the whole stack: DB, schema, vec0,
# Maildir, launchd state for all four agents (indexer / embedder / mbsync /
# mcp), mbsync presence, Ollama, and re-probes MCP /health for consistency.
# Catches dead-on-arrival agents that don't have an HTTP endpoint to probe.
echo
echo "Running mailvec doctor preflight..."
if ! dotnet run --project "$REPO_ROOT/src/Mailvec.Cli" --no-launch-profile -v quiet -- doctor; then
    echo >&2
    echo "doctor reported one or more failures — see output above." >&2
    exit 1
fi

echo
echo "Installed agents:"
for label in "${LABELS[@]}"; do
    echo "  $label"
done
echo
echo "To uninstall: $0 --uninstall"
