#!/usr/bin/env bash
# Sign a published .NET output directory with a stable Developer ID identity.
#
# Why: macOS TCC keys Automation / file-access grants (reading ~/Mail, sending
# Apple Events, Notifications) off the binary's *code signature*. .NET's
# build-time signing is ad-hoc, producing a fresh CDHash on every `dotnet
# publish` — so each redeploy makes the MCP server look like a brand-new app
# and Claude Desktop re-fires every TCC dialog. A stable Developer ID
# signature anchored to the Team ID gives a constant designated requirement,
# so the grants persist across rebuilds. Same idea as ops/build-tray.sh, but
# for the four .NET apphost binaries.
#
# Deliberately NOT hardened runtime and NOT notarized: neither is needed for a
# local-only tool (the install scripts strip the quarantine xattr), and
# hardened runtime would force CoreCLR JIT entitlements
# (allow-jit / allow-unsigned-executable-memory / disable-library-validation).
# We only need a stable signature + designated requirement, so we sign plain.
#
# Identity precedence (matches ops/build-tray.sh): MAILVEC_SIGN_IDENTITY env →
# first "Developer ID Application" cert in the keychain → ad-hoc ("-").
#
# Usage:
#   Standalone:  ops/sign-publish.sh <publish-dir> <apphost-filename> <identifier>
#   Sourced:     source ops/sign-publish.sh   # defines the two functions below

mailvec_resolve_identity() {
    local id="${MAILVEC_SIGN_IDENTITY:-}"
    if [[ -z "$id" ]]; then
        id="$(security find-identity -v -p codesigning 2>/dev/null \
            | grep "Developer ID Application" | head -1 \
            | sed -E 's/.*"(.*)".*/\1/' || true)"
        id="${id:--}"
    fi
    printf '%s' "$id"
}

# mailvec_sign_publish <publish-dir> <apphost-filename> <identifier>
# Signs every nested *.dylib first (inside-out), then the apphost executable
# with a stable, meaningful identifier that TCC anchors on (replacing .NET's
# generic "apphost" identifier).
mailvec_sign_publish() {
    local dir="$1" apphost="$2" identifier="$3"
    local identity; identity="$(mailvec_resolve_identity)"
    # Local-only: skip the Apple timestamp-server round-trip (only notarization
    # needs a secure timestamp, and we don't notarize). Keeps signing offline
    # and fast on every redeploy.
    local ts="--timestamp=none"

    if [[ "$identity" == "-" ]]; then
        echo "    sign: ad-hoc (no Developer ID cert — TCC dialogs will re-prompt on redeploy)"
    else
        echo "    sign: $identifier → $identity"
    fi

    # Nested native libs get their own per-file identifier (codesign derives it
    # from the filename); only the apphost carries the bundle-style identifier.
    local lib
    while IFS= read -r -d '' lib; do
        codesign --force $ts --sign "$identity" "$lib"
    done < <(find "$dir" -name '*.dylib' -type f -print0)

    if [[ ! -f "$dir/$apphost" ]]; then
        echo "sign-publish: apphost '$apphost' not found in $dir" >&2
        return 1
    fi
    codesign --force $ts --identifier "$identifier" --sign "$identity" "$dir/$apphost"
}

# Standalone invocation
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    set -euo pipefail
    if [[ $# -ne 3 ]]; then
        echo "Usage: $0 <publish-dir> <apphost-filename> <identifier>" >&2
        exit 2
    fi
    mailvec_sign_publish "$1" "$2" "$3"
fi
