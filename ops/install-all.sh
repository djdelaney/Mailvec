#!/usr/bin/env bash
# Single-command bootstrap for a fresh machine. Runs each step in order:
#
#   1. ops/fetch-sqlite-vec.sh   — download vec0.dylib + VERSION sidecar
#   2. ops/install.sh            — publish .NET services to ~/.local/share/mailvec/,
#                                  install launchd plists, drop ~/.local/bin/mailvec
#                                  CLI shim, run mailvec doctor
#   3. ops/install-tray.sh       — generate the Xcode project via xcodegen,
#                                  ad-hoc sign + archive the .app, copy to
#                                  /Applications, launch
#
# Prerequisites NOT handled by this script:
# - .NET 10 SDK on PATH (`brew install --cask dotnet-sdk`)
# - Xcode + Command Line Tools (full Xcode, not just CLT — xcodebuild needs it)
# - xcodegen (`brew install xcodegen`)
# - mbsync (`brew install isync`)
# - Ollama, via the cask (`brew install --cask ollama-app`), NOT the `ollama`
#   formula — the formula bottle has shipped without the GGML llama-server, so
#   mxbai-embed-large fails to load. The cask auto-starts via its own Login Item.
# - ~/.mbsyncrc configured + IMAP password in Keychain — README "mbsync" section
#
# Why not bundle prereqs too? They all need user-specific config (Fastmail
# app password, Ollama model selection, etc.) and live longer than this app,
# so wrapping them would hide configuration the user needs to own. The
# README walks through each.
#
# Usage:
#   ops/install-all.sh             # full bootstrap, prompts for paths in install.sh
#   ops/install-all.sh --no-tray   # skip the SwiftUI tray (.NET services only)
#   ops/install-all.sh --no-fetch  # skip vec0.dylib fetch (already present)
#   ops/install-all.sh --defaults  # answer install.sh's prompts with defaults
#                                    (unattended reinstall/upgrade; also implied
#                                    when stdin isn't a terminal)
set -euo pipefail

trap 'echo "install-all.sh: failed at line $LINENO" >&2' ERR

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SKIP_FETCH=0
SKIP_TRAY=0
INSTALL_ARGS=()
for arg in "$@"; do
    case "$arg" in
        --no-fetch) SKIP_FETCH=1 ;;
        --no-tray)  SKIP_TRAY=1 ;;
        --defaults) INSTALL_ARGS+=("--defaults") ;;
        -h|--help)
            sed -n '2,31p' "$0"
            exit 0
            ;;
        *)
            echo "install-all.sh: unknown arg '$arg'" >&2
            exit 1
            ;;
    esac
done

echo "===================================================================="
echo "Mailvec full install"
echo "===================================================================="

if [[ "$(uname -s)-$(uname -m)" != "Darwin-arm64" ]]; then
    echo "install-all.sh: Apple Silicon Mac required — Intel Macs are not supported." >&2
    echo "(macOS is dropping Intel support in its next release; Mailvec targets arm64 only.)" >&2
    exit 1
fi

# 1. sqlite-vec dylib
if [[ $SKIP_FETCH -eq 0 ]]; then
    if [[ -f "$REPO_ROOT/runtimes/osx-arm64/native/vec0.dylib" ]]; then
        echo "==> [1/3] vec0.dylib already present — skipping fetch (use --no-fetch to silence)"
    else
        echo "==> [1/3] Fetching vec0.dylib"
        "$REPO_ROOT/ops/fetch-sqlite-vec.sh"
    fi
else
    echo "==> [1/3] Skipped vec0.dylib fetch (--no-fetch)"
fi
echo

# 2. .NET services + CLI shim. install.sh prompts for site-specific paths
# (Maildir, DB, Ollama URL, mbsyncrc, optional Fastmail account id).
echo "==> [2/3] Installing .NET services + CLI shim"
"$REPO_ROOT/ops/install.sh" ${INSTALL_ARGS[@]+"${INSTALL_ARGS[@]}"}
echo

# 3. SwiftUI tray app. Skip on machines without Xcode or where the user
# only wants the headless services.
if [[ $SKIP_TRAY -eq 0 ]]; then
    echo "==> [3/3] Building + installing tray app"
    if ! command -v xcodegen >/dev/null 2>&1; then
        echo "install-all.sh: xcodegen not on PATH — skipping tray." >&2
        echo "             Install with: brew install xcodegen" >&2
    elif ! xcode-select -p >/dev/null 2>&1; then
        echo "install-all.sh: Xcode not selected — skipping tray." >&2
        echo "             Install Xcode and run: sudo xcode-select -s /Applications/Xcode.app/Contents/Developer" >&2
    else
        "$REPO_ROOT/ops/install-tray.sh"
    fi
else
    echo "==> [3/3] Skipped tray app (--no-tray)"
fi

echo
echo "Done."
