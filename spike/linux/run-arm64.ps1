# ARM64 coexistence + server-smoke spike, the arm64 counterpart of run.ps1.
#
# Runs the SAME spike (run.sh + run-server-smoke.sh) inside a linux/arm64
# dotnet/sdk:8.0 container to prove netcoredbg + ClrMD coexistence on arm64 (the
# Tasks view while the process is held at a breakpoint). On an x64 host this runs
# under QEMU emulation (Docker binfmt): a PASS is a strong positive signal, but
# the authoritative check is the arm64 CI leg on real hardware. The source is
# copied into a clean /work inside the container (excluding bin/obj) so host x64
# build artifacts are neither used nor clobbered.
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

$inner = @'
set -e
echo "container arch: $(uname -m)"
mkdir -p /work
cd /src
tar --exclude='*/bin' --exclude='*/obj' --exclude='./.git' \
    --exclude='./tools/netcoredbg' --exclude='*/.netcoredbg' --exclude='*/.server' \
    -cf - . | (cd /work && tar -xf -)
bash /work/spike/linux/run.sh
echo
bash /work/spike/linux/run-server-smoke.sh
'@ -replace "`r`n", "`n"

docker run --rm `
    --platform linux/arm64 `
    --cap-add=SYS_PTRACE `
    --security-opt seccomp=unconfined `
    -v "${repo}:/src:ro" `
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 `
    -e DOTNET_NOLOGO=1 `
    mcr.microsoft.com/dotnet/sdk:8.0 `
    bash -c $inner
