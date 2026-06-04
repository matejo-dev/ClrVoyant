#!/usr/bin/env bash
# Install ClrVoyant as a .NET global tool (`clrvoyant` on PATH) and optionally
# register it in a coding-agent client's MCP config. Bash counterpart of install.ps1.
#
#   ./scripts/install.sh                 # installs the tool, prints the snippet
#   ./scripts/install.sh --client cursor # installs + registers (needs jq to merge)
#
# Clients: claude-code | claude-desktop | cursor | windsurf | vscode
set -euo pipefail

CLIENT="print"
SERVER_NAME="clrvoyant"
NO_TOOL_INSTALL=0

while [ $# -gt 0 ]; do
  case "$1" in
    --client)         CLIENT="$2"; shift 2;;
    --server-name)    SERVER_NAME="$2"; shift 2;;
    --no-tool-install) NO_TOOL_INSTALL=1; shift;;
    *) echo "unknown arg: $1" >&2; exit 2;;
  esac
done

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

if [ "$NO_TOOL_INSTALL" -eq 0 ]; then
  NUPKG="$(mktemp -d)"
  echo "Packing + installing the 'clrvoyant' global tool..."
  dotnet pack "$REPO_ROOT/src/ClrVoyant.Server/ClrVoyant.Server.csproj" -c Release -o "$NUPKG" --nologo
  dotnet tool update --global ClrVoyant --add-source "$NUPKG"   # install-or-update
  rm -rf "$NUPKG"
  echo "Installed. Command: clrvoyant"
  echo "(ensure ~/.dotnet/tools is on PATH so 'clrvoyant' resolves)"
fi

if [ "$CLIENT" = "print" ]; then
  cat <<EOF

Add this to your MCP client config:

{
  "mcpServers": {
    "$SERVER_NAME": { "command": "clrvoyant" }
  }
}

(VS Code's mcp.json uses the key "servers" instead of "mcpServers".)
EOF
  exit 0
fi

command -v jq >/dev/null 2>&1 || { echo "jq is required to merge config (apt install jq), or use print mode." >&2; exit 1; }

case "$CLIENT" in
  claude-code)    CONFIG="$HOME/.claude.json";                              KEY="mcpServers";;
  claude-desktop) CONFIG="$HOME/.config/Claude/claude_desktop_config.json"; KEY="mcpServers";;
  cursor)         CONFIG="$HOME/.cursor/mcp.json";                          KEY="mcpServers";;
  windsurf)       CONFIG="$HOME/.codeium/windsurf/mcp_config.json";         KEY="mcpServers";;
  vscode)         CONFIG="$REPO_ROOT/.vscode/mcp.json";                     KEY="servers";;
  *) echo "unknown client: $CLIENT" >&2; exit 2;;
esac

mkdir -p "$(dirname "$CONFIG")"
[ -f "$CONFIG" ] || echo '{}' > "$CONFIG"
tmp="$(mktemp)"
jq --arg k "$KEY" --arg n "$SERVER_NAME" \
   '.[$k] = ((.[$k] // {}) + { ($n): { "command": "clrvoyant" } })' \
   "$CONFIG" > "$tmp" && mv "$tmp" "$CONFIG"

echo "Registered '$SERVER_NAME' -> command 'clrvoyant' in $CLIENT config: $CONFIG"
echo "Restart $CLIENT (or reload its MCP servers) to pick it up."
