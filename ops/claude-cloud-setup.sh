#!/bin/bash
# Claude cloud environment setup for Mailvec: the .NET 10 SDK (+ sqlite3 CLI).
#
# THIS FILE IS THE MASTER COPY. The claude.ai environment's "Setup script"
# field holds a PASTE of it: the setup script runs before the repository is
# cloned, so it cannot call this path. Change it here, then re-paste.
# Guide: docs/contributing/cloud-development.md.
#
# Deliberately NOT here: sqlite-vec. It installs into the repo's gitignored
# runtimes/ (Directory.Build.props copies it into bin/ at build time) and the
# repo is not on disk yet, so ops/claude-session-start.sh — the SessionStart
# hook in .claude/settings.json — fetches it through ops/fetch-sqlite-vec.sh,
# reusing that script's SHA-256 pin instead of carrying a second copy.
#
# Output shows live AND persists to /var/log/mailvec-setup.log: a failed setup
# leaves no session to read a log from, and a successful one scrolls away.
exec > >(tee -a /var/log/mailvec-setup.log) 2>&1
set -euxo pipefail

# Fail closed. A non-zero exit fails the session start, which is the point: a
# setup that "succeeds" half-done is snapshotted as the environment cache and
# reused by every session for about a week.

# ── apt sources ──────────────────────────────────────────────────────────────
# The image ships two Launchpad PPAs (deadsnakes/Python, ondrej/PHP) that
# answered 403 / "no longer signed" from the sandbox (observed 2026-09-24, in
# Knapper's environment). Mailvec needs neither; dropping them lets
# `apt-get update` finish cleanly. The preinstalled Python/PHP stay installed.
rm -f /etc/apt/sources.list.d/*deadsnakes* /etc/apt/sources.list.d/*ondrej*

# `apt-get update` exits 100 if ANY configured source is unreachable, even
# when the Ubuntu archive that carries dotnet-sdk-10.0 updated fine. Tolerate
# it, then judge the outcome by what actually got installed.
apt-get update || echo "WARNING: apt-get update reported errors (exit $?); continuing"

# ── .NET 10 SDK ──────────────────────────────────────────────────────────────
# Ubuntu's own archive build. No system libraries beyond it are needed: SQLite
# is bundled (SQLitePCLRaw.bundle_e_sqlite3), PDFium comes through NuGet, and
# SkiaSharp is the NativeAssets.Linux.NoDependencies build.
#
# sqlite3 is for inspecting a dev database by hand. It is not a Mailvec
# dependency and cannot read the vec0 tables unless the extension is loaded.
apt-get install -y dotnet-sdk-10.0 sqlite3
dotnet --list-sdks
dotnet --list-sdks | grep -q '^10\.' \
  || { echo ".NET 10 SDK not installed" >&2; exit 1; }
command -v sqlite3

echo "setup complete"
