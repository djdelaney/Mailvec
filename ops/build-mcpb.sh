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
# The bundle extracts to ~/Library/Application Support/Claude/extensions/<name>/
# at install time, which avoids the ~/Documents TCC restriction we hit before.
#
# Usage:
#   ops/fetch-sqlite-vec.sh        # one-time, ensures the dylib is present
#   ops/build-mcpb.sh              # produces dist/mailvec-<version>.mcpb
#   ops/build-mcpb.sh --bump       # patch-bump manifest.json, build, open the result
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

# Patch-bump manifest.json in place. Regex-targeted at the version field only so
# we don't reformat the rest of the file (json.dump would lose comments-as-keys
# ordering, trailing newline conventions, etc).
if [[ $BUMP -eq 1 ]]; then
    python3 - <<'PY'
import pathlib, re, sys
p = pathlib.Path("manifest.json")
text = p.read_text()
m = re.search(r'("version"\s*:\s*")(\d+)\.(\d+)\.(\d+)(")', text)
if not m:
    print("ERROR: could not find version field in manifest.json", file=sys.stderr)
    sys.exit(1)
major, minor, patch = int(m.group(2)), int(m.group(3)), int(m.group(4))
new = f"{major}.{minor}.{patch + 1}"
old = f"{major}.{minor}.{patch}"
text = text[:m.start(2)] + new + text[m.end(4):]
p.write_text(text)
print(f"→ Bumped manifest.json: {old} → {new}")
PY
fi

# Read version from manifest so the artifact filename matches.
VERSION="$(python3 -c 'import json; print(json.load(open("manifest.json"))["version"])')"
RID="osx-arm64"
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
