# Windows-side launcher for the Linux coexistence spike. Runs the repo inside a
# dotnet/sdk:8.0 Linux container with the ptrace capability granted (so we test
# the netcoredbg<->ClrMD coexistence, not the container's default ptrace policy —
# SYS_PTRACE/shareProcessNamespace is a separate, known deployment requirement).
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

docker run --rm `
    --cap-add=SYS_PTRACE `
    --security-opt seccomp=unconfined `
    -v "${repo}:/work" `
    -w /work `
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 `
    -e DOTNET_NOLOGO=1 `
    mcr.microsoft.com/dotnet/sdk:8.0 `
    bash -c "bash /work/spike/linux/run.sh && echo && bash /work/spike/linux/run-server-smoke.sh"
