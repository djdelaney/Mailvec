#!/usr/bin/env bash
# Install Mailvec.Tray.app into /Applications and launch it.
#
# Rebuilds via ops/build-tray.sh when the artifact is missing OR any build
# input (Sources/, Resources/, project.yml, Info.plist, entitlements) is
# newer than the compiled binary — so a plain `install-tray.sh` after a
# source edit actually ships that edit. Pass --no-build to skip the
# freshness check and deploy whatever is already in build/ (e.g. when you
# just ran build-tray.sh yourself).
#
# We deliberately put the bundle in /Applications (not ~/Applications) so
# launch-at-login via SMAppService finds it — SMAppService.mainApp.register()
# requires the host bundle to live under /Applications.
#
# Usage:
#   ops/install-tray.sh              # (re)build if stale + install + launch
#   ops/install-tray.sh --no-launch  # build + install, don't open
#   ops/install-tray.sh --no-build   # deploy existing build/ artifact as-is
set -euo pipefail

trap 'echo "install-tray.sh: failed at line $LINENO" >&2' ERR

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TRAY_DIR="$REPO_ROOT/src/Mailvec.Tray"
BUILD_DIR="$REPO_ROOT/build"
APP_SRC="$BUILD_DIR/Mailvec.Tray.app"
APP_BIN="$APP_SRC/Contents/MacOS/Mailvec.Tray"
APP_DST="/Applications/Mailvec.Tray.app"

LAUNCH=1
SKIP_BUILD=0
for arg in "$@"; do
  case "$arg" in
    --no-launch) LAUNCH=0 ;;
    --no-build)  SKIP_BUILD=1 ;;
    *) echo "install-tray.sh: unknown arg '$arg'" >&2; exit 2 ;;
  esac
done

# Rebuild when the compiled binary is missing, or any build input is newer
# than it. `find -newer` against the binary (not the .app dir, whose mtime
# the cp -R below would refresh) is the freshness signal. The generated
# .xcodeproj and Tests/ are intentionally excluded — neither changes the
# shipped bundle. Build-time inputs only.
needs_build=1
if [[ "$SKIP_BUILD" == "1" ]]; then
  needs_build=0
  [[ -f "$APP_BIN" ]] || { echo "install-tray.sh: --no-build but no artifact at $APP_SRC" >&2; exit 1; }
elif [[ -f "$APP_BIN" ]]; then
  stale="$(find "$TRAY_DIR/Sources" "$TRAY_DIR/Resources" \
                "$TRAY_DIR/project.yml" "$TRAY_DIR/Info.plist" \
                "$TRAY_DIR/Mailvec.Tray.entitlements" \
                -newer "$APP_BIN" -print -quit 2>/dev/null || true)"
  [[ -z "$stale" ]] && needs_build=0
fi

if [[ "$needs_build" == "1" ]]; then
  echo "==> Build artifact missing or stale — building"
  "$REPO_ROOT/ops/build-tray.sh"
else
  echo "==> Reusing up-to-date build artifact"
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
