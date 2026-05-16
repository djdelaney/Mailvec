#!/usr/bin/env bash
# Install Mailvec.Tray.app into /Applications and launch it.
#
# This builds the app via ops/build-tray.sh first if no fresh artifact exists.
# We deliberately put the bundle in /Applications (not ~/Applications) so
# launch-at-login via SMAppService finds it — SMAppService.mainApp.register()
# requires the host bundle to live under /Applications.
#
# Usage:
#   ops/install-tray.sh              # build + install + launch
#   ops/install-tray.sh --no-launch  # build + install, don't open
set -euo pipefail

trap 'echo "install-tray.sh: failed at line $LINENO" >&2' ERR

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUILD_DIR="$REPO_ROOT/build"
APP_SRC="$BUILD_DIR/Mailvec.Tray.app"
APP_DST="/Applications/Mailvec.Tray.app"

LAUNCH=1
if [[ "${1:-}" == "--no-launch" ]]; then
  LAUNCH=0
fi

if [[ ! -d "$APP_SRC" ]]; then
  echo "==> No build artifact at $APP_SRC — building first"
  "$REPO_ROOT/ops/build-tray.sh"
fi

# Quit any running instance so the copy succeeds. Best-effort; ignore if
# the app wasn't running.
echo "==> Quitting existing Mailvec.Tray (if running)"
osascript -e 'tell application "Mailvec.Tray" to quit' >/dev/null 2>&1 || true
sleep 1

echo "==> Installing to $APP_DST"
rm -rf "$APP_DST"
cp -R "$APP_SRC" "$APP_DST"
# Strip macOS quarantine xattr so Gatekeeper doesn't first-launch-prompt.
# Safe because this is a local build, not a download.
xattr -dr com.apple.quarantine "$APP_DST" 2>/dev/null || true

if [[ "$LAUNCH" == "1" ]]; then
  echo "==> Launching Mailvec.Tray"
  open "$APP_DST"
fi

echo
echo "Installed. The icon appears in the menu bar (top-right)."
echo "Configure launch-at-login under Mailvec.Tray → Preferences → General."
