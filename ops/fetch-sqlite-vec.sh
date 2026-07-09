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

mkdir -p "${REPO_ROOT}/${DEST_DIR}"
TMPDIR="$(mktemp -d)"
trap 'rm -rf "${TMPDIR}"' EXIT

echo "Fetching ${URL}"
curl -fsSL "${URL}" -o "${TMPDIR}/${ASSET}"
tar -xzf "${TMPDIR}/${ASSET}" -C "${TMPDIR}"

mv "${TMPDIR}/${LIB}" "${REPO_ROOT}/${DEST_DIR}/${LIB}"
# Sidecar file that TraySystemService.TryReadVecVersion reads to render
# the "sqlite-vec extension" row in the Advanced prefs tab. The library
# can't be introspected for its version (it's loaded via SQLite's
# extension API, before any internal version function is callable), so
# we cooperate at install time and persist it here.
echo "v${VERSION}" > "${REPO_ROOT}/${DEST_DIR}/VERSION"
echo "Installed: ${DEST_DIR}/${LIB} (sqlite-vec ${VERSION})"
