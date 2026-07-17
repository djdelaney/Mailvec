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
#
# Flow: run this, push/merge to main, wait for green CI, then run the printed
# tag commands. Only tag commits that already passed CI on main.

set -euo pipefail

PART="patch"
COMMIT=1
for arg in "$@"; do
    case "$arg" in
        --patch) PART="patch" ;;
        --minor) PART="minor" ;;
        --major) PART="major" ;;
        --no-commit) COMMIT=0 ;;
        -h|--help)
            sed -n '2,/^$/p' "$0" | sed 's/^# \?//'
            exit 0
            ;;
        *)
            echo "Unknown argument: $arg" >&2
            echo "Usage: $0 [--patch|--minor|--major] [--no-commit]" >&2
            exit 2
            ;;
    esac
done

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
    echo
    echo "Next: push/merge this commit to main, wait for green CI, then cut the release:"
else
    echo "✓ Bumped to ${NEW_VERSION} (not committed)"
    echo
    echo "Next: commit the bump, push/merge to main, wait for green CI, then cut the release:"
fi
echo
echo "    git tag -a v${NEW_VERSION} -m \"Mailvec ${NEW_VERSION}\""
echo "    git push origin v${NEW_VERSION}"
echo
echo "The tag push publishes ghcr.io/<owner>/mailvec:v${NEW_VERSION} + mailvec-mbsync:v${NEW_VERSION}."
echo "Deploy: pin both image vars in the VM's .env, backup first, then"
echo "'docker compose pull && docker compose up -d' (docs/deploy-docker.md)."
