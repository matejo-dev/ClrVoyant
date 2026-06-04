# End-to-end smoke for the new tools (content breakpoint, restart, clear, instructions).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src/ClrVoyant.Server/bin/Debug/net8.0/ClrVoyant.Server.exe"
$dll = Join-Path $root "samples/SampleApp/bin/Debug/net8.0/SampleApp.dll"
$src = Join-Path $root "samples/SampleApp/Program.cs"
$env:CLRVOYANT_NETCOREDBG = $null  # use the bundled engine

. (Join-Path $PSScriptRoot "mcp-driver.ps1") -ServerExe $exe
try {
    Handshake

    $instr = Call "get_debug_instructions" @{}
    if ($instr -notmatch "TYPICAL LOOP") { throw "instructions missing playbook" }
    Write-Host "[ok] get_debug_instructions returned playbook" -ForegroundColor Green

    $launch = Call "debug_launch" @{ program = $dll }
    Write-Host "[ok] launched $($launch.sessionId) pid=$($launch.pid)"

    # Content-matched breakpoint: no line number, matched by source text.
    $bp = Call "set_breakpoint" @{ file = $src; content = "int offset = squared + 7;" }
    if ($bp.line -ne 37) { throw "expected content match at line 37, got $($bp.line)" }
    Write-Host "[ok] content breakpoint resolved to line $($bp.line), verified=$($bp.verified)" -ForegroundColor Green

    $stop = Call "continue" @{ timeoutMs = 15000 }
    if ($stop.stop.line -ne 37) { throw "expected stop at 37, got $($stop.stop.line)" }
    Write-Host "[ok] stopped at line $($stop.stop.line)"

    # Restart: same session id, breakpoint preserved, hits again.
    $re = Call "restart_debugging" @{}
    if ($re.sessionId -ne $launch.sessionId) { throw "restart changed session id" }
    Write-Host "[ok] restarted, same session id $($re.sessionId)" -ForegroundColor Green
    $bps = Call "list_breakpoints" @{}
    if (-not $bps) { throw "breakpoints lost across restart" }
    Write-Host "[ok] breakpoint survived restart (count=$(@($bps).Count))" -ForegroundColor Green

    $stop2 = Call "continue" @{ timeoutMs = 15000 }
    if ($stop2.stop.line -ne 37) { throw "post-restart stop not at 37, got $($stop2.stop.line)" }
    Write-Host "[ok] post-restart breakpoint hit at line $($stop2.stop.line)" -ForegroundColor Green

    $cleared = Call "clear_all_breakpoints" @{}
    Write-Host "[ok] clear_all_breakpoints -> '$cleared'" -ForegroundColor Green
    $bps2 = Call "list_breakpoints" @{}
    if (@($bps2).Count -ne 0) { throw "breakpoints not cleared" }
    Write-Host "[ok] no breakpoints remain" -ForegroundColor Green

    Call "debug_stop" @{} | Out-Null
    Write-Host "`nSMOKE PASSED" -ForegroundColor Green
}
finally {
    Stop-Driver
}
