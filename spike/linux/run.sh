#!/usr/bin/env bash
# Linux coexistence spike, run INSIDE a dotnet/sdk:8.0 container with the repo
# mounted at /work. Proves whether ClrMD can enumerate Tasks while netcoredbg
# holds the target process stopped — the risk the Windows (PSS snapshot) spike
# never covered.
set -euo pipefail

REPO=/work
SPIKE=$REPO/spike
LX=$SPIKE/linux
DBGDIR=$LX/.netcoredbg

echo "=== [1/4] fetching netcoredbg (pinned, arch from uname) ==="
# Pinned + SHA-256 verified via the shared fetch script (keep in sync with
# Directory.Build.props, the single source of truth for the version/hashes).
NCDBG_VERSION="3.1.3-1062"
case "$(uname -m)" in
  x86_64|amd64)  NCDBG_SHA256="3814341c028c81ff7eea03ac316ad92e9ad7d705d2a00e3e3df269cdc241c763" ;;
  aarch64|arm64) NCDBG_SHA256="fc9efb691a53932a7fac4b9f67af68ad0c2a4cbe59cb2c1a3c44c64959df2ba4" ;;
  *) echo "unsupported architecture '$(uname -m)'" >&2; exit 1 ;;
esac
bash "$REPO/tools/fetch-netcoredbg.sh" "$NCDBG_VERSION" "$NCDBG_SHA256" "$DBGDIR"
NETCOREDBG="$DBGDIR/netcoredbg"
"$NETCOREDBG" --version | head -1

echo "=== [2/4] building SampleApp, CoexistDriver, ClrMdLinuxProbe ==="
dotnet build "$REPO/samples/SampleApp/SampleApp.csproj"      -c Debug -v q --nologo
dotnet build "$LX/CoexistDriver/CoexistDriver.csproj"        -c Debug -v q --nologo
dotnet build "$LX/ClrMdLinuxProbe/ClrMdLinuxProbe.csproj"    -c Debug -v q --nologo

TARGET_DLL="$REPO/samples/SampleApp/bin/Debug/net8.0/SampleApp.dll"
TARGET_SRC="$REPO/samples/SampleApp/Program.cs"
PROBE="$LX/ClrMdLinuxProbe/bin/Debug/net8.0/ClrMdLinuxProbe"
DRIVER_DLL="$LX/CoexistDriver/bin/Debug/net8.0/CoexistDriver.dll"
chmod +x "$PROBE" || true

# SampleApp parks async Tasks (AwaitForever -> WaitingForActivation, a Faulted
# task) at startup, then loops calling Compute(). Line 37 ('int offset = ...') is
# a SYNCHRONOUS loop-body statement, hit every iteration -> a reliable stop with
# Tasks live on the heap.
BPLINE=37

echo "=== [3/4] driving netcoredbg to a breakpoint, then probing the held PID ==="
echo "    (ptrace policy is taken out of the equation: container runs with SYS_PTRACE)"
set +e
dotnet "$DRIVER_DLL" "$NETCOREDBG" "$TARGET_DLL" "$TARGET_SRC" "$BPLINE" "$PROBE"
RC=$?
set -e

echo "=== [4/4] done (driver+probe exit code: $RC) ==="
exit $RC
