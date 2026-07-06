#!/usr/bin/env bash
# Re-publish the stdio MCP binary that Claude Desktop loads.
#
# Why publish (not just build): macOS's TCC stops Claude Desktop's spawned
# children from open()-ing files under ~/Documents/ even with Full Disk Access
# (stat() works, open() doesn't). So the binary must live somewhere else;
# ~/.local/share/mailvec/ is fine. Run this script after touching anything in
# Mailvec.Mcp or Mailvec.Core that affects search behaviour, then quit and
# relaunch Claude Desktop.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# Per-service subdir so Mailvec.Indexer/Embedder/Mcp can coexist under one
# share dir without their published deps stomping each other. install.sh uses
# the same convention; keep them in sync.
PUBLISH_DIR="$HOME/.local/share/mailvec/mcp"

cd "$REPO_ROOT"
dotnet publish src/Mailvec.Mcp/Mailvec.Mcp.csproj -c Release -o "$PUBLISH_DIR"

# Stable-signature the publish output. Without this, every republish carries
# a fresh ad-hoc CDHash and Claude Desktop re-fires the TCC permission
# dialogs (TCC anchors grants to the code signature) — the exact problem
# ops/sign-publish.sh exists to solve, and every other publish path already
# goes through it.
# shellcheck source=sign-publish.sh
source "$REPO_ROOT/ops/sign-publish.sh"
mailvec_sign_publish "$PUBLISH_DIR" "Mailvec.Mcp" "com.mailvec.mcp"

echo "Published to $PUBLISH_DIR"
