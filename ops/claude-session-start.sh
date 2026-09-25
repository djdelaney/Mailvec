#!/usr/bin/env bash
# SessionStart hook (.claude/settings.json) for Claude Code cloud sessions:
# installs the sqlite-vec loadable into runtimes/<rid>/native/ so
# `dotnet build` / `dotnet test` work in a fresh checkout.
#
# Why a hook and not the environment's setup script: the setup script runs
# before the repository is cloned (and is then cached for ~a week), while
# runtimes/ is a gitignored directory INSIDE the checkout. The hook runs after
# the clone, on every startup and resume. It delegates to
# ops/fetch-sqlite-vec.sh, so the SHA-256 pin lives in exactly one place.
#
# Local sessions (the dev Mac) exit immediately: CLAUDE_CODE_REMOTE is "true"
# only on the cloud VM. Nothing here touches the database, the Maildir or
# launchd — but the frozen corpus is reason enough to do nothing locally.
#
# Plain-text stdout on exit 0 becomes context for the session's agent, so it
# is kept short. Any other exit is a non-blocking hook error in the transcript;
# a missing vec0 then also fails every SQLite-backed test loudly.
set -euo pipefail

[[ "${CLAUDE_CODE_REMOTE:-}" == "true" ]] || exit 0

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FETCH="${REPO_ROOT}/ops/fetch-sqlite-vec.sh"

case "$(uname -s)-$(uname -m)" in
    Linux-x86_64)  RID="linux-x64" ;;
    Linux-aarch64) RID="linux-arm64" ;;
    *) echo "claude-session-start.sh: unexpected platform $(uname -s)-$(uname -m)" >&2; exit 1 ;;
esac
NATIVE="${REPO_ROOT}/runtimes/${RID}/native"

# The version fetch-sqlite-vec.sh would install, read from its default. If
# that line ever changes shape the parse comes back empty and we simply
# re-fetch (a second or two) — never "already installed" on a guess. CI runs
# this hook after the fetch and requires "already installed", which is what
# notices the parse breaking.
WANT="${SQLITE_VEC_VERSION:-$(sed -n 's/^VERSION="\${SQLITE_VEC_VERSION:-\([^}]*\)}"$/\1/p' "$FETCH")}"
HAVE="$(cat "${NATIVE}/VERSION" 2>/dev/null || true)"

if [[ -n "$WANT" && -f "${NATIVE}/vec0.so" && "$HAVE" == "v${WANT}" ]]; then
    STATE="already installed"
else
    # The fetch script's progress lines would otherwise land in the agent's
    # context; keep them in a log and surface them only on failure.
    LOG="$(mktemp)"
    if ! (cd "$REPO_ROOT" && bash "$FETCH" "$RID") >"$LOG" 2>&1; then
        echo "claude-session-start.sh: ops/fetch-sqlite-vec.sh failed — builds will lack vec0.so:" >&2
        cat "$LOG" >&2
        rm -f "$LOG"
        exit 1
    fi
    rm -f "$LOG"
    STATE="installed $(cat "${NATIVE}/VERSION")"
fi

cat <<EOF
Claude cloud session: sqlite-vec ${STATE} at runtimes/${RID}/native/vec0.so.
This VM has no mail archive, Maildir, launchd or Ollama. The frozen-corpus block in CLAUDE.md describes the author's Mac and does not apply here. See docs/contributing/cloud-development.md.
EOF
