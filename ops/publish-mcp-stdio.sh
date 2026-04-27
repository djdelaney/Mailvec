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
PUBLISH_DIR="$HOME/.local/share/mailvec"

cd "$REPO_ROOT"
dotnet publish src/Mailvec.Mcp/Mailvec.Mcp.csproj -c Release -o "$PUBLISH_DIR"
echo "Published to $PUBLISH_DIR"
