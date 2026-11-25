#!/usr/bin/env bash
# Install ClrVoyant as a .NET global tool (`clrvoyant` on PATH) and optionally
# register it in a coding-agent client's MCP config. Bash counterpart of install.ps1.
#
#   ./scripts/install.sh                 # installs the tool, prints the snippet
#   ./scripts/install.sh --client cursor # installs + registers (needs jq to merge)
#
# Clients: claude-code | claude-desktop | cursor | windsurf | vscode
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: install.sh [--client <name>] [--server-name <name>] [--no-tool-install] [-h|--help]

Packs the server, installs/updates the `ClrVoyant` .NET global tool (command:
`clrvoyant`) and optionally registers it in a coding-agent client's MCP config.

Options:
  --client <name>      print (default) | claude-code | claude-desktop | cursor |
                       windsurf | vscode. With anything but `print`, the client's
                       config file is merged (needs `jq`); a timestamped backup
                       `<config>.bak.<yyyymmddhhmmss>` is written next to it first,
                       and the file's permissions are preserved.
  --server-name <name> Name of the MCP server entry (default: clrvoyant).
  --no-tool-install    Skip pack + tool install (assume `clrvoyant` is installed).
  -h, --help           Show this help.
USAGE
}

CLIENT="print"
SERVER_NAME="clrvoyant"
NO_TOOL_INSTALL=0

while [ $# -gt 0 ]; do
  case "$1" in
    -h|--help) usage; exit 0;;
    --client|--server-name)
      if [ $# -lt 2 ] || [ "${2#-}" != "$2" ]; then
        echo "error: $1 requires a value" >&2
        usage >&2
        exit 2
      fi
      if [ "$1" = "--client" ]; then CLIENT="$2"; else SERVER_NAME="$2"; fi
      shift 2;;
    --no-tool-install) NO_TOOL_INSTALL=1; shift;;
    *) echo "error: unknown argument: $1" >&2; usage >&2; exit 2;;
  esac
done

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# Resolve the target config before doing anything slow or stateful, so a typo in
# --client (or a missing jq) does not leave the tool installed but unregistered.
CONFIG=""
KEY=""
case "$CLIENT" in
  print)          ;;
  claude-code)    CONFIG="$HOME/.claude.json";                              KEY="mcpServers";;
  claude-desktop) CONFIG="$HOME/.config/Claude/claude_desktop_config.json"; KEY="mcpServers";;
  cursor)         CONFIG="$HOME/.cursor/mcp.json";                          KEY="mcpServers";;
  windsurf)       CONFIG="$HOME/.codeium/windsurf/mcp_config.json";         KEY="mcpServers";;
  vscode)         CONFIG="$REPO_ROOT/.vscode/mcp.json";                     KEY="servers";;
  *) echo "error: unknown client: $CLIENT" >&2; usage >&2; exit 2;;
esac

if [ "$CLIENT" != "print" ]; then
  command -v jq >/dev/null 2>&1 || { echo "error: jq is required to merge the config (apt install jq), or use print mode." >&2; exit 1; }
fi

NUPKG=""
TMP=""
cleanup() {
  [ -z "$NUPKG" ] || rm -rf "$NUPKG"
  [ -z "$TMP" ] || rm -f "$TMP"
}
trap cleanup EXIT

if [ "$NO_TOOL_INSTALL" -eq 0 ]; then
  NUPKG="$(mktemp -d)"
  echo "Packing + installing the 'clrvoyant' global tool..."
  dotnet pack "$REPO_ROOT/src/ClrVoyant.Server/ClrVoyant.Server.csproj" -c Release -o "$NUPKG" --nologo
  dotnet tool update --global ClrVoyant --add-source "$NUPKG"   # install-or-update
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

JQ_FILTER='.[$k] = ((.[$k] // {}) + { ($n): { "command": "clrvoyant" } })'

mkdir -p "$(dirname "$CONFIG")"
TMP="$(mktemp)"

# Merge into a temp file first: if the existing config is not valid JSON (or not an
# object) jq fails and the original is left untouched.
if [ -s "$CONFIG" ]; then
  if ! jq --arg k "$KEY" --arg n "$SERVER_NAME" "$JQ_FILTER" "$CONFIG" > "$TMP"; then
    echo "error: could not merge into $CONFIG (is it valid JSON with an object at the top level?). Nothing was changed." >&2
    exit 1
  fi
  BACKUP="$CONFIG.bak.$(date +%Y%m%d%H%M%S)"
  cp -p "$CONFIG" "$BACKUP"
  echo "Backed up the existing config to: $BACKUP"
else
  printf '{}\n' | jq --arg k "$KEY" --arg n "$SERVER_NAME" "$JQ_FILTER" > "$TMP"
fi

# Write through the existing file (not mv): keeps its permissions, owner and any
# symlink (e.g. a dotfile manager) instead of replacing it with a 0600 temp file.
cat "$TMP" > "$CONFIG"

echo "Registered '$SERVER_NAME' -> command 'clrvoyant' in $CLIENT config: $CONFIG"
echo "Restart $CLIENT (or reload its MCP servers) to pick it up."
