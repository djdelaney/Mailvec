#!/usr/bin/env bash
# Build a Claude Desktop MCP bundle (.mcpb) for the Mailvec stdio server.
#
# Output: dist/mailvec-<version>.mcpb — drag onto Claude Desktop or open with it
# to install. Replaces the old publish-mcp-stdio.sh + ~/.local/bin launcher +
# claude_desktop_config.json edit dance.
#
# What's in the bundle:
#   manifest.json
#   server/Mailvec.Mcp                                  (self-contained native exe)
#   server/runtimes/osx-arm64/native/vec0.dylib         (sqlite-vec extension)
#   server/appsettings.json + everything else from publish output
#
# The bundle extracts to ~/Library/Application Support/Claude/Claude Extensions/<id>/
# (older Claude Desktop builds: Connectors/) at install time, which avoids the
# ~/Documents TCC restriction we hit before.
#
# Usage:
#   ops/fetch-sqlite-vec.sh        # one-time, ensures the dylib is present
#   ops/build-mcpb.sh              # produces dist/mailvec-<version>.mcpb
#   ops/build-mcpb.sh --bump       # patch-bump manifest.json + Directory.Build.props
#                                    <Version> + project.yml MARKETING_VERSION,
#                                    build, open the result
#                                    (Claude Desktop ignores re-installs of the same
#                                     version, so a bump is needed for any rebuild
#                                     you want to install)

set -euo pipefail

BUMP=0
for arg in "$@"; do
    case "$arg" in
        --bump) BUMP=1 ;;
        -h|--help)
            sed -n '2,/^$/p' "$0" | sed 's/^# \?//'
            exit 0
            ;;
        *)
            echo "Unknown argument: $arg" >&2
            echo "Usage: $0 [--bump]" >&2
            exit 2
            ;;
    esac
done

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

# Patch-bump THE Mailvec version in all three places that carry it:
# manifest.json, the repo-wide <Version> in Directory.Build.props (stamps every
# .NET binary, surfaces in MCP initialize.serverInfo), and the tray's
# MARKETING_VERSION in project.yml (so "what version are you running?" has one
# answer across the whole system). Regex-targeted at the version fields only so
# we don't reformat the rest of the files (json.dump would lose key ordering /
# trailing newlines, and an XML serializer would reflow whitespace).
if [[ $BUMP -eq 1 ]]; then
    python3 - <<'PY'
import pathlib, re, sys

manifest = pathlib.Path("manifest.json")
text = manifest.read_text()
m = re.search(r'("version"\s*:\s*")(\d+)\.(\d+)\.(\d+)(")', text)
if not m:
    print("ERROR: could not find version field in manifest.json", file=sys.stderr)
    sys.exit(1)
major, minor, patch = int(m.group(2)), int(m.group(3)), int(m.group(4))
old = f"{major}.{minor}.{patch}"
new = f"{major}.{minor}.{patch + 1}"
manifest.write_text(text[:m.start(2)] + new + text[m.end(4):])
print(f"→ Bumped manifest.json: {old} → {new}")

props = pathlib.Path("Directory.Build.props")
ptext = props.read_text()
pm = re.search(r"(<Version>)(\d+\.\d+\.\d+)(</Version>)", ptext)
if not pm:
    print("ERROR: could not find <Version> in Directory.Build.props", file=sys.stderr)
    sys.exit(1)
props.write_text(ptext[:pm.start(2)] + new + ptext[pm.end(2):])
print(f"→ Bumped Directory.Build.props <Version>: {pm.group(2)} → {new}")

tray = pathlib.Path("src/Mailvec.Tray/project.yml")
ttext = tray.read_text()
tm = re.search(r'(MARKETING_VERSION:\s*")(\d+\.\d+\.\d+)(")', ttext)
if not tm:
    print("ERROR: could not find MARKETING_VERSION in src/Mailvec.Tray/project.yml", file=sys.stderr)
    sys.exit(1)
tray.write_text(ttext[:tm.start(2)] + new + ttext[tm.end(2):])
print(f"→ Bumped project.yml MARKETING_VERSION: {tm.group(2)} → {new}")
print(f"→ After committing, tag the release: git tag v{new}")
PY
fi

# Read version from manifest so the artifact filename matches, and verify the
# three version carriers are in lockstep — a drifted version means an
# installed bundle whose serverInfo (or tray About) lies about what it is.
VERSION="$(python3 - <<'PY'
import json, pathlib, re, sys
manifest = json.load(open("manifest.json"))["version"]
props = re.search(r"<Version>(\d+\.\d+\.\d+)</Version>", pathlib.Path("Directory.Build.props").read_text()).group(1)
tray = re.search(r'MARKETING_VERSION:\s*"(\d+\.\d+\.\d+)"', pathlib.Path("src/Mailvec.Tray/project.yml").read_text()).group(1)
if not (manifest == props == tray):
    print(f"ERROR: version drift — manifest.json={manifest} Directory.Build.props={props} project.yml={tray}. "
          "Re-align them (ops/build-mcpb.sh --bump keeps them in lockstep).", file=sys.stderr)
    sys.exit(1)
print(manifest)
PY
)"
RID="osx-arm64"   # Apple Silicon only — Intel is unsupported (see README Requirements)

if [[ "$(uname -s)-$(uname -m)" != "Darwin-arm64" ]]; then
    echo "ERROR: build-mcpb.sh must run on an Apple Silicon Mac — Intel is not supported." >&2
    exit 1
fi
STAGING="$(mktemp -d -t mailvec-mcpb.XXXXXX)"
DIST="$REPO_ROOT/dist"
OUTPUT="$DIST/mailvec-${VERSION}.mcpb"

trap 'rm -rf "$STAGING"' EXIT

echo "→ Staging at $STAGING"

# Sanity check: the dylib must exist before publish, or the bundle will be
# unusable. ConnectionFactory looks for it at runtime relative to the binary.
if [[ ! -f "$REPO_ROOT/runtimes/$RID/native/vec0.dylib" ]]; then
    echo "ERROR: runtimes/$RID/native/vec0.dylib not found. Run ops/fetch-sqlite-vec.sh first." >&2
    exit 1
fi

# Self-contained publish: bakes the .NET runtime into the output so the user
# doesn't need dotnet installed and we don't need DOTNET_ROOT env hacks.
# NOT SingleFile — keeps the dylib visibly co-located with the exe, which is
# easier to debug and matches ConnectionFactory's relative-path resolution.
echo "→ Publishing Mailvec.Mcp (self-contained, $RID)..."
dotnet publish src/Mailvec.Mcp/Mailvec.Mcp.csproj \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -o "$STAGING/server" \
    --nologo --verbosity quiet

# Belt-and-braces: confirm the dylib survived publish (Directory.Build.props
# adds it as <None CopyToOutputDirectory>; if the rule ever changes silently,
# we want to fail loud here rather than ship a broken bundle).
if [[ ! -f "$STAGING/server/runtimes/$RID/native/vec0.dylib" ]]; then
    echo "ERROR: vec0.dylib missing from publish output at server/runtimes/$RID/native/" >&2
    echo "       Check the <None Include> rule in Directory.Build.props." >&2
    exit 1
fi

# Stable Developer ID signature on the self-contained MCP binary (and its
# bundled CoreCLR / sqlite-vec dylibs) so Claude Desktop's spawned stdio child
# keeps its TCC grants (Mail access, Apple Events) across bundle upgrades
# instead of re-prompting on every install. Ad-hoc fallback when no cert.
# See ops/sign-publish.sh.
echo "→ Signing server binary..."
source "$REPO_ROOT/ops/sign-publish.sh"
mailvec_sign_publish "$STAGING/server" "Mailvec.Mcp" "com.mailvec.mcp"

cp "$REPO_ROOT/manifest.json" "$STAGING/manifest.json"

# Bundle icons referenced by manifest.json (light + dark variants). Paths in
# the manifest are resolved relative to the bundle root, so the directory
# layout under assets/ must mirror what's on disk.
cp -R "$REPO_ROOT/assets" "$STAGING/assets"
find "$STAGING/assets" -name '.DS_Store' -delete

# .mcpb is a zip with the manifest at the root. Use -X to strip extra macOS
# attributes that some MCPB validators reject.
mkdir -p "$DIST"
rm -f "$OUTPUT"
echo "→ Packaging $OUTPUT"
(cd "$STAGING" && zip -rqX "$OUTPUT" manifest.json server assets)

SIZE=$(du -h "$OUTPUT" | awk '{print $1}')
echo "✓ Built $OUTPUT ($SIZE)"

if [[ $BUMP -eq 1 ]]; then
    # `open` hands the file to Claude Desktop, which prompts to install/upgrade.
    # In Settings → Extensions, toggle Mailvec off before installing to keep
    # user_config values across the upgrade (uninstalling clears them).
    echo "→ Opening $OUTPUT for Claude Desktop..."
    open "$OUTPUT"
else
    echo
    echo "Install: open \"$OUTPUT\" or drag onto Claude Desktop."
    echo "Tip: re-run with --bump to patch-bump the version, build, and open in one step."
fi
