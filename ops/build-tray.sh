#!/usr/bin/env bash
# Build the Mailvec.Tray SwiftUI app into a self-contained .app bundle.
#
# Pipeline:
#   1. Run `xcodegen generate` to materialise the .xcodeproj from project.yml.
#      The generated project is gitignored — regeneration is deterministic
#      from the YAML, so we never commit the binary-ish .pbxproj.
#   2. `xcodebuild archive` produces an unsigned .xcarchive.
#   3. Copy the .app out of the archive into build/Mailvec.app.
#
# Usage:
#   ops/build-tray.sh                # Release build into build/Mailvec.app
#   ops/build-tray.sh --debug        # Debug build (faster, no optimisation)
#
# Requires: xcodegen (`brew install xcodegen`), Xcode 15+.
set -euo pipefail

trap 'echo "build-tray.sh: failed at line $LINENO" >&2' ERR

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TRAY_DIR="$REPO_ROOT/src/Mailvec.Tray"
BUILD_DIR="$REPO_ROOT/build"
CONFIG="Release"

if [[ "${1:-}" == "--debug" ]]; then
  CONFIG="Debug"
fi

if ! command -v xcodegen >/dev/null 2>&1; then
  echo "xcodegen is required. Install with: brew install xcodegen" >&2
  exit 1
fi

echo "==> Generating Xcode project"
(cd "$TRAY_DIR" && xcodegen generate --quiet)

echo "==> Archiving Mailvec.Tray ($CONFIG)"
mkdir -p "$BUILD_DIR"
ARCHIVE_PATH="$BUILD_DIR/Mailvec.Tray.xcarchive"
# Ad-hoc signing (CODE_SIGN_IDENTITY="-") rather than "no signing at all"
# — UNUserNotificationCenter.requestAuthorization rejects unsigned bundles
# with UNError code 1 ("notifications not allowed"), and Gatekeeper
# softlinks the app to be more trusted when it's at least ad-hoc signed.
# No Developer Team is required for this; it's purely local.
xcodebuild \
  -project "$TRAY_DIR/Mailvec.Tray.xcodeproj" \
  -scheme "Mailvec.Tray" \
  -configuration "$CONFIG" \
  -destination "generic/platform=macOS" \
  -archivePath "$ARCHIVE_PATH" \
  CODE_SIGN_IDENTITY="-" \
  CODE_SIGN_STYLE=Manual \
  CODE_SIGNING_REQUIRED=YES \
  CODE_SIGNING_ALLOWED=YES \
  DEVELOPMENT_TEAM="" \
  archive 1>/dev/null

APP_SRC="$ARCHIVE_PATH/Products/Applications/Mailvec.Tray.app"
APP_DST="$BUILD_DIR/Mailvec.Tray.app"

rm -rf "$APP_DST"
cp -R "$APP_SRC" "$APP_DST"

echo "==> Built $APP_DST"
echo "    Install:    ops/install-tray.sh"
echo "    Run direct: open '$APP_DST'"
