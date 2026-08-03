#!/usr/bin/env bash
# Downloads the prebuilt sqlite-vec loadable extension into
# runtimes/<rid>/native/. Run once after cloning, and again to bump.
#
# Usage: fetch-sqlite-vec.sh [rid]
#   rid ∈ osx-arm64 | linux-x64 | linux-arm64; defaults to the current
#   platform. The explicit arg exists for the Docker build, where the
#   Dockerfile maps BuildKit's TARGETARCH to a RID so a cross-platform
#   `docker build --platform ...` fetches the right library.
#
# Why not the NuGet package? The only published wrapper (sqlite-vec
# 0.1.7-alpha.2.1) has been a prerelease for over a year and lags
# upstream. Fetching the GitHub release directly keeps us current.
set -euo pipefail

VERSION="${SQLITE_VEC_VERSION:-0.1.9}"
RID="${1:-}"

if [[ -z "${RID}" ]]; then
    case "$(uname -s)-$(uname -m)" in
        Darwin-arm64)  RID="osx-arm64" ;;
        Linux-x86_64)  RID="linux-x64" ;;
        Linux-aarch64) RID="linux-arm64" ;;
        Darwin-x86_64)
            echo "fetch-sqlite-vec.sh: Intel Macs are not supported — Mailvec requires Apple Silicon." >&2
            echo "(macOS is dropping Intel support in its next release; Mailvec targets arm64 only.)" >&2
            exit 1 ;;
        *) echo "Unsupported platform: $(uname -s)-$(uname -m)" >&2 ; exit 1 ;;
    esac
fi

case "${RID}" in
    osx-arm64)   ASSET="sqlite-vec-${VERSION}-loadable-macos-aarch64.tar.gz" ; LIB="vec0.dylib" ;;
    linux-x64)   ASSET="sqlite-vec-${VERSION}-loadable-linux-x86_64.tar.gz"  ; LIB="vec0.so" ;;
    linux-arm64) ASSET="sqlite-vec-${VERSION}-loadable-linux-aarch64.tar.gz" ; LIB="vec0.so" ;;
    *) echo "Unsupported RID: ${RID} (expected osx-arm64 | linux-x64 | linux-arm64)" >&2 ; exit 1 ;;
esac

DEST_DIR="runtimes/${RID}/native"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
URL="https://github.com/asg017/sqlite-vec/releases/download/v${VERSION}/${ASSET}"

# Pinned SHA-256 of each release asset. This script installs a native library
# that ConnectionFactory loads into EVERY Mailvec process via SQLite's
# extension API — it is arbitrary code execution by design, running with the
# services' full access to the mailbox and database. A git tag can be moved and
# a GitHub release asset can be replaced after the fact, so "we requested
# v0.1.9" is not the same claim as "we got the v0.1.9 that was reviewed". The
# pin is what makes it the same claim.
#
# A case statement rather than an associative array: macOS still ships bash 3.2
# as /bin/bash, and this script runs there.
#
# BUMPING: set SQLITE_VEC_VERSION and run once per RID. Each run fails with the
# SHA-256 it computed; confirm that against the upstream release page, then
# paste it in here. An unrecorded version fails closed instead of quietly
# installing unverified — so forgetting to record one is loud, which is the
# whole point.
expected_sha() {
    case "$1 $2" in
        "0.1.9 osx-arm64")   echo "8282126333399ddfe98bbbcc7a1936e7252625aac49df056a98be602e46bfd29" ;;
        "0.1.9 linux-x64")   echo "b959baa1d8dc88861b1edb337b8587178cdcb12d60b4998f9d10b6a82052d5d7" ;;
        "0.1.9 linux-arm64") echo "ea03d39541e478fab5974253c461e1cb5d77742f69e40cf96e3fad5bc309a37c" ;;
        *) echo "" ;;
    esac
}

# macOS ships `shasum` (perl), Debian-based images ship `sha256sum` (coreutils),
# and the Docker build stage runs this script on the latter. Take whichever is
# present rather than assuming the host is the dev Mac.
sha256_of() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | awk '{print $1}'
    elif command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$1" | awk '{print $1}'
    else
        echo "fetch-sqlite-vec.sh: no sha256sum or shasum available — cannot verify the download." >&2
        exit 1
    fi
}

mkdir -p "${REPO_ROOT}/${DEST_DIR}"
TMPDIR="$(mktemp -d)"
trap 'rm -rf "${TMPDIR}"' EXIT

echo "Fetching ${URL}"
curl -fsSL "${URL}" -o "${TMPDIR}/${ASSET}"

# Verify BEFORE extracting. tar is itself a parser being handed bytes off the
# network, so the check goes ahead of it — not after, and not after install.
EXPECTED="$(expected_sha "${VERSION}" "${RID}")"
ACTUAL="$(sha256_of "${TMPDIR}/${ASSET}")"

if [[ -z "${EXPECTED}" ]]; then
    echo "fetch-sqlite-vec.sh: no pinned checksum recorded for sqlite-vec ${VERSION} / ${RID}." >&2
    echo "  Downloaded asset SHA-256: ${ACTUAL}" >&2
    echo "  Verify that against https://github.com/asg017/sqlite-vec/releases/tag/v${VERSION}" >&2
    echo "  then add this line to expected_sha() in $0:" >&2
    echo "        \"${VERSION} ${RID}\") echo \"${ACTUAL}\" ;;" >&2
    exit 1
fi

if [[ "${ACTUAL}" != "${EXPECTED}" ]]; then
    echo "fetch-sqlite-vec.sh: CHECKSUM MISMATCH for ${ASSET} — refusing to install." >&2
    echo "  expected: ${EXPECTED}" >&2
    echo "  actual:   ${ACTUAL}" >&2
    echo "  This library gets loaded into every Mailvec process. Do not bypass this." >&2
    echo "  Either the release asset was replaced upstream, or the download was tampered with." >&2
    exit 1
fi
echo "Verified SHA-256 ${ACTUAL}"

tar -xzf "${TMPDIR}/${ASSET}" -C "${TMPDIR}"

mv "${TMPDIR}/${LIB}" "${REPO_ROOT}/${DEST_DIR}/${LIB}"
# Sidecar file that TraySystemService.TryReadVecVersion reads to render
# the "sqlite-vec extension" row in the Advanced prefs tab. The library
# can't be introspected for its version (it's loaded via SQLite's
# extension API, before any internal version function is callable), so
# we cooperate at install time and persist it here.
echo "v${VERSION}" > "${REPO_ROOT}/${DEST_DIR}/VERSION"
echo "Installed: ${DEST_DIR}/${LIB} (sqlite-vec ${VERSION})"
