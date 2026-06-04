#!/usr/bin/env bash
# Capstone: drive the REAL published ClrVoyant.Server on Linux end-to-end (launch
# SampleApp, content breakpoint, continue, list_tasks via the cross-OS ClrMD path).
# Runs INSIDE the dotnet/sdk:8.0 container with the repo at /work and SYS_PTRACE.
set -euo pipefail

REPO=/work
SPIKE=$REPO/spike
LX=$SPIKE/linux
DBGDIR=$LX/.netcoredbg

echo "=== [server-smoke 1/3] ensure netcoredbg (pinned, arch from uname) ==="
# Pinned + SHA-256 verified via the shared fetch (keep in sync with Directory.Build.props).
NCDBG_VERSION="3.1.3-1062"
case "$(uname -m)" in
  x86_64|amd64)  NCDBG_SHA256="3814341c028c81ff7eea03ac316ad92e9ad7d705d2a00e3e3df269cdc241c763" ;;
  aarch64|arm64) NCDBG_SHA256="fc9efb691a53932a7fac4b9f67af68ad0c2a4cbe59cb2c1a3c44c64959df2ba4" ;;
  *) echo "unsupported architecture '$(uname -m)'" >&2; exit 1 ;;
esac
bash "$REPO/tools/fetch-netcoredbg.sh" "$NCDBG_VERSION" "$NCDBG_SHA256" "$DBGDIR"
NETCOREDBG="$DBGDIR/netcoredbg"

echo "=== [server-smoke 2/3] publish server + build SampleApp + ServerSmoke ==="
dotnet publish "$REPO/src/ClrVoyant.Server/ClrVoyant.Server.csproj" -c Release -o "$LX/.server" -v q --nologo
dotnet build "$REPO/samples/SampleApp/SampleApp.csproj" -c Debug -v q --nologo
dotnet build "$LX/ServerSmoke/ServerSmoke.csproj" -c Debug -v q --nologo

SERVER_DLL="$LX/.server/ClrVoyant.Server.dll"
SAMPLE_DLL="$REPO/samples/SampleApp/bin/Debug/net8.0/SampleApp.dll"
SAMPLE_SRC="$REPO/samples/SampleApp/Program.cs"
SMOKE_DLL="$LX/ServerSmoke/bin/Debug/net8.0/ServerSmoke.dll"

echo "=== [server-smoke 3/3] drive the real server over MCP stdio ==="
dotnet "$SMOKE_DLL" "$SERVER_DLL" "$NETCOREDBG" "$SAMPLE_DLL" "$SAMPLE_SRC"
