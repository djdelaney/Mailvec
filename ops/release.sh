#!/usr/bin/env bash
# Bump THE Mailvec version and stage a release.
#
# The sanctioned bump path (extracted from ops/build-mcpb.sh --bump, which now
# delegates here). Bumps the three version carriers in lockstep — manifest.json,
# the repo-wide <Version> in Directory.Build.props (stamps all four .NET
# binaries and initialize.serverInfo.version), and the tray's MARKETING_VERSION
# in src/Mailvec.Tray/project.yml — commits the bump, and prints the tag
# commands that cut the release. The v<version> tag push publishes the durable
# GHCR images the homelab pins (docs/deploy-docker.md "Release tags"); the
# publish workflow refuses a v* tag that doesn't match <Version> at the tagged
# commit.
#
# Version semantics:
#   --patch (default)  anything
#   --minor            MCP tool-surface change, or a schema migration (a new
#                      image runs SchemaMigrator against the seeded archive in
#                      place, and the downgrade guard makes that one-way — the
#                      minor bump is the "back up first" flag in the tag name)
#   --major            reserved; nothing has earned it yet
#
# Usage:
#   ops/release.sh [--patch|--minor|--major] [--no-commit]
#   ops/release.sh [--patch|--minor|--major] --ship [--yes]
#
# Flow (default): run this, push/merge to main, wait for green CI, then run the
# printed tag commands. Only tag commits that already passed CI on main.
#
# Flow (--ship): automates that same discipline end to end — push the bump to
# main, poll the CI run for THIS commit until it completes, and tag + push
# v<version> ONLY if it went green. A red/cancelled/timed-out run aborts before
# tagging (the bump commit stays on main, just untagged, ready to retry once CI
# is green). Requires the `gh` CLI authenticated, and the current branch to be
# main. --yes skips the confirmation prompt (for unattended use).

set -euo pipefail

PART="patch"
COMMIT=1
SHIP=0
ASSUME_YES=0
for arg in "$@"; do
    case "$arg" in
        --patch) PART="patch" ;;
        --minor) PART="minor" ;;
        --major) PART="major" ;;
        --no-commit) COMMIT=0 ;;
        --ship) SHIP=1 ;;
        --yes|-y) ASSUME_YES=1 ;;
        -h|--help)
            sed -n '2,/^$/p' "$0" | sed 's/^# \?//'
            exit 0
            ;;
        *)
            echo "Unknown argument: $arg" >&2
            echo "Usage: $0 [--patch|--minor|--major] [--no-commit]" >&2
            echo "       $0 [--patch|--minor|--major] --ship [--yes]" >&2
            exit 2
            ;;
    esac
done

# --ship tags THIS commit, so there must be one; and it must be on main (the
# only branch the publish workflow_run gates on, and where push triggers CI).
if [[ $SHIP -eq 1 ]]; then
    if [[ $COMMIT -eq 0 ]]; then
        echo "ERROR: --ship needs a commit to push and tag; drop --no-commit." >&2
        exit 2
    fi
    if ! command -v gh >/dev/null 2>&1; then
        echo "ERROR: --ship needs the GitHub CLI (gh). Install it or run without --ship." >&2
        exit 2
    fi
    if ! gh auth status >/dev/null 2>&1; then
        echo "ERROR: gh is not authenticated (run 'gh auth login')." >&2
        exit 2
    fi
    branch="$(git rev-parse --abbrev-ref HEAD)"
    if [[ "$branch" != "main" ]]; then
        echo "ERROR: --ship must run on main (on '$branch'). Releases are cut from main." >&2
        exit 2
    fi
fi

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

CARRIERS=(manifest.json Directory.Build.props src/Mailvec.Tray/project.yml)

# Refuse to fold unrelated uncommitted edits to the carrier files into the
# bump commit (or to bump on top of them with --no-commit).
if ! git diff --quiet HEAD -- "${CARRIERS[@]}"; then
    echo "ERROR: uncommitted changes in ${CARRIERS[*]} — commit or stash them first." >&2
    exit 1
fi

NEW_VERSION="$(BUMP_PART="$PART" python3 - <<'PY'
import json, os, pathlib, re, sys

part = os.environ["BUMP_PART"]

# Verify the three carriers are in lockstep before touching anything — a
# pre-existing drift means a hand-edited version, and bumping on top of it
# would ship the drift half-applied.
manifest_path = pathlib.Path("manifest.json")
props_path = pathlib.Path("Directory.Build.props")
tray_path = pathlib.Path("src/Mailvec.Tray/project.yml")

manifest_ver = json.load(open(manifest_path))["version"]
props_ver = re.search(r"<Version>(\d+\.\d+\.\d+)</Version>", props_path.read_text()).group(1)
tray_ver = re.search(r'MARKETING_VERSION:\s*"(\d+\.\d+\.\d+)"', tray_path.read_text()).group(1)
if not (manifest_ver == props_ver == tray_ver):
    print(f"ERROR: version drift — manifest.json={manifest_ver} "
          f"Directory.Build.props={props_ver} project.yml={tray_ver}. "
          "Re-align them by hand before bumping.", file=sys.stderr)
    sys.exit(1)

major, minor, patch = (int(x) for x in manifest_ver.split("."))
if part == "major":
    new = f"{major + 1}.0.0"
elif part == "minor":
    new = f"{major}.{minor + 1}.0"
else:
    new = f"{major}.{minor}.{patch + 1}"

# Regex-targeted at the version fields only so we don't reformat the rest of
# the files (json.dump would lose key ordering / trailing newlines, and an
# XML serializer would reflow whitespace).
edits = [
    (manifest_path, r'("version"\s*:\s*")(\d+\.\d+\.\d+)(")'),
    (props_path, r"(<Version>)(\d+\.\d+\.\d+)(</Version>)"),
    (tray_path, r'(MARKETING_VERSION:\s*")(\d+\.\d+\.\d+)(")'),
]
for path, pattern in edits:
    text = path.read_text()
    m = re.search(pattern, text)
    path.write_text(text[:m.start(2)] + new + text[m.end(2):])
    print(f"→ Bumped {path}: {manifest_ver} → {new}", file=sys.stderr)

print(new)
PY
)"

echo
if [[ $COMMIT -eq 1 ]]; then
    git commit -q -m "Bump version to ${NEW_VERSION}" -- "${CARRIERS[@]}"
    echo "✓ Committed bump to ${NEW_VERSION}"
else
    echo "✓ Bumped to ${NEW_VERSION} (not committed)"
fi

TAG="v${NEW_VERSION}"

# --- deploy note, printed either way ---------------------------------------
deploy_note() {
    echo
    echo "The ${TAG} tag push publishes ghcr.io/<owner>/mailvec:${TAG} + mailvec-mbsync:${TAG}."
    echo "Deploy: pin both image vars in the VM's .env, backup first, then"
    echo "'docker compose pull && docker compose up -d' (docs/deploy-docker.md)."
}

if [[ $SHIP -eq 0 ]]; then
    echo
    if [[ $COMMIT -eq 1 ]]; then
        echo "Next: push/merge this commit to main, wait for green CI, then cut the release:"
    else
        echo "Next: commit the bump, push/merge to main, wait for green CI, then cut the release:"
    fi
    echo
    echo "    git tag -a ${TAG} -m \"Mailvec ${NEW_VERSION}\""
    echo "    git push origin ${TAG}"
    echo
    echo "(Or re-run with --ship to push, wait for green CI, and tag automatically.)"
    deploy_note
    exit 0
fi

# --- --ship: push, wait for green CI on THIS commit, then tag --------------
SHA="$(git rev-parse HEAD)"
echo
echo "About to:  push main → wait for CI on ${SHA:0:12} to go green → tag ${TAG} + push."
if git -c core.pager=cat status --porcelain | grep -q .; then
    echo "Note: working tree has uncommitted changes; only the committed HEAD is pushed/tagged."
fi
if [[ $ASSUME_YES -ne 1 ]]; then
    read -rp "Proceed? [y/N] " reply || reply=""
    case "$reply" in
        [yY]|[yY][eE][sS]) ;;
        *) echo "Aborted. The bump commit is local (unpushed) — push and tag by hand when ready."; exit 0 ;;
    esac
fi

echo "→ Pushing main…"
git push origin HEAD

# The CI run is created a few seconds after the push (webhook latency); wait for
# it to appear before watching it. `gh run list -c <sha>` filters to this exact
# commit, so we never watch someone else's run.
run_field() { gh run list -c "$SHA" -w CI -b main --json "$1" --limit 1 -q "$2" 2>/dev/null || true; }

echo "→ Waiting for the CI run to appear…"
appear_deadline=$(( $(date +%s) + 180 ))
while :; do
    count="$(run_field databaseId 'length')"
    [[ "$count" == "1" ]] && break
    if [[ "$(date +%s)" -ge "$appear_deadline" ]]; then
        echo "ERROR: no CI run appeared for ${SHA:0:12} within 3 min." >&2
        echo "       The commit is pushed. Check GitHub Actions, then tag by hand once green:" >&2
        echo "         git tag -a ${TAG} -m \"Mailvec ${NEW_VERSION}\" && git push origin ${TAG}" >&2
        exit 1
    fi
    sleep 5
done

RUN_URL="$(run_field url '.[0].url')"
echo "→ Watching CI: ${RUN_URL}"
watch_deadline=$(( $(date +%s) + 1800 ))
conclusion=""
while :; do
    # Read status + conclusion from ONE snapshot so the success check can't
    # race a transient gh failure between two separate fetches. Empty output
    # (gh hiccup) leaves both blank → the loop just polls again.
    read -r status conclusion <<< "$(run_field 'status,conclusion' '.[0] | "\(.status) \(.conclusion // "")"')"
    [[ "$status" == "completed" ]] && break
    if [[ "$(date +%s)" -ge "$watch_deadline" ]]; then
        echo "ERROR: CI still '${status:-unknown}' after 30 min — NOT tagging. See ${RUN_URL}." >&2
        exit 1
    fi
    echo "   CI ${status:-queued}…"
    sleep 30
done

if [[ "$conclusion" != "success" ]]; then
    echo "ERROR: CI concluded '${conclusion:-unknown}' — NOT tagging. See ${RUN_URL}." >&2
    echo "       Fix, push a green commit, and re-run ops/release.sh --ship (or tag by hand)." >&2
    exit 1
fi

echo "✓ CI green. Tagging ${TAG}…"
git tag -a "${TAG}" -m "Mailvec ${NEW_VERSION}"
git push origin "${TAG}"
echo "✓ Pushed ${TAG}."
deploy_note
