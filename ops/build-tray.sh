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
# Signing identity (see docs/contributing/tray.md "Build chain"):
#   By default the script auto-detects a "Developer ID Application"
#   certificate in your keychain and signs with it. A *stable* identity is
#   what stops macOS from re-prompting for Automation/Notification
#   permissions on every rebuild — TCC keys those grants off the code
#   signature, and ad-hoc signing produces a fresh hash every build.
#   Falls back to ad-hoc ("-") when no Developer ID cert is present, so the
#   build still works on contributor machines without a paid Apple account.
#   Override with MAILVEC_SIGN_IDENTITY (a cert name or "-" to force ad-hoc).
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

# Resolve the signing identity. Precedence: MAILVEC_SIGN_IDENTITY override,
# then the first "Developer ID Application" cert in the keychain, then ad-hoc.
SIGN_IDENTITY="${MAILVEC_SIGN_IDENTITY:-}"
DEVELOPMENT_TEAM=""
if [[ -z "$SIGN_IDENTITY" ]]; then
  SIGN_IDENTITY="$(security find-identity -v -p codesigning 2>/dev/null \
    | grep "Developer ID Application" | head -1 \
    | sed -E 's/.*"(.*)".*/\1/' || true)"
  SIGN_IDENTITY="${SIGN_IDENTITY:--}"
fi

if [[ "$SIGN_IDENTITY" == "-" ]]; then
  # Ad-hoc signing (not "no signing at all") — UNUserNotificationCenter
  # .requestAuthorization rejects fully-unsigned bundles with UNError code 1
  # ("notifications not allowed"), and Gatekeeper trusts an ad-hoc-signed app
  # more than an unsigned one. No Developer Team needed; purely local.
  # Downside: the signature hash changes every build, so macOS re-prompts for
  # Automation/Notification permissions each reinstall. Create a Developer ID
  # Application cert to get a stable signature (see header comment).
  echo "==> Signing: ad-hoc (no Developer ID cert found — permissions will re-prompt on rebuild)"
else
  # Stable Developer ID signature: TCC grants persist across rebuilds.
  # Pull the Team ID out of the cert name "... (TEAMID)" for hardened-runtime
  # manual signing.
  DEVELOPMENT_TEAM="$(sed -E 's/.*\(([A-Z0-9]+)\)$/\1/' <<<"$SIGN_IDENTITY")"
  [[ "$DEVELOPMENT_TEAM" == "$SIGN_IDENTITY" ]] && DEVELOPMENT_TEAM=""
  echo "==> Signing: $SIGN_IDENTITY${DEVELOPMENT_TEAM:+ (team $DEVELOPMENT_TEAM)}"
fi

echo "==> Generating Xcode project"
(cd "$TRAY_DIR" && xcodegen generate --quiet)

echo "==> Archiving Mailvec.Tray ($CONFIG)"
mkdir -p "$BUILD_DIR"
ARCHIVE_PATH="$BUILD_DIR/Mailvec.Tray.xcarchive"
xcodebuild \
  -project "$TRAY_DIR/Mailvec.Tray.xcodeproj" \
  -scheme "Mailvec.Tray" \
  -configuration "$CONFIG" \
  -destination "generic/platform=macOS" \
  -archivePath "$ARCHIVE_PATH" \
  CODE_SIGN_IDENTITY="$SIGN_IDENTITY" \
  CODE_SIGN_STYLE=Manual \
  CODE_SIGNING_REQUIRED=YES \
  CODE_SIGNING_ALLOWED=YES \
  DEVELOPMENT_TEAM="$DEVELOPMENT_TEAM" \
  archive 1>/dev/null

APP_SRC="$ARCHIVE_PATH/Products/Applications/Mailvec.Tray.app"
APP_DST="$BUILD_DIR/Mailvec.Tray.app"

rm -rf "$APP_DST"
cp -R "$APP_SRC" "$APP_DST"

echo "==> Built $APP_DST"
codesign -dvv "$APP_DST" 2>&1 | grep -E "^(Signature|TeamIdentifier)=" | sed 's/^/    /' || true
echo "    Install:    ops/install-tray.sh"
echo "    Run direct: open '$APP_DST'"
