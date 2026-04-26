#!/usr/bin/env bash
# Downloads the prebuilt sqlite-vec loadable extension into
# runtimes/osx-arm64/native/. Run once after cloning, and again to bump.
#
# Why not the NuGet package? The only published wrapper (sqlite-vec
# 0.1.7-alpha.2.1) has been a prerelease for over a year and lags
# upstream. Fetching the GitHub release directly keeps us current.
set -euo pipefail

VERSION="${SQLITE_VEC_VERSION:-0.1.9}"

case "$(uname -s)-$(uname -m)" in
    Darwin-arm64)  ASSET="sqlite-vec-${VERSION}-loadable-macos-aarch64.tar.gz" ; DEST_DIR="runtimes/osx-arm64/native" ;;
    Darwin-x86_64) ASSET="sqlite-vec-${VERSION}-loadable-macos-x86_64.tar.gz"  ; DEST_DIR="runtimes/osx-x64/native"   ;;
    *) echo "Unsupported platform: $(uname -s)-$(uname -m)" >&2 ; exit 1 ;;
esac

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
URL="https://github.com/asg017/sqlite-vec/releases/download/v${VERSION}/${ASSET}"

mkdir -p "${REPO_ROOT}/${DEST_DIR}"
TMPDIR="$(mktemp -d)"
trap 'rm -rf "${TMPDIR}"' EXIT

echo "Fetching ${URL}"
curl -fsSL "${URL}" -o "${TMPDIR}/${ASSET}"
tar -xzf "${TMPDIR}/${ASSET}" -C "${TMPDIR}"

mv "${TMPDIR}/vec0.dylib" "${REPO_ROOT}/${DEST_DIR}/vec0.dylib"
echo "Installed: ${DEST_DIR}/vec0.dylib (sqlite-vec ${VERSION})"
