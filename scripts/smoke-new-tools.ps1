# End-to-end smoke for the new tools (content breakpoint, restart, clear, instructions).
# -ServerPath: apphost or ClrVoyant.Server.dll (default: the built output for
# -Configuration / -Framework). -SampleDll defaults to the SampleApp build for the
# same configuration and framework.
param(
    [string]$ServerPath,
    [string]$SampleDll,
    [string]$Configuration = 'Debug',
    [string]$Framework = 'net10.0'
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "smoke-common.ps1")
$exe = Resolve-ServerPath -ServerPath $ServerPath -Configuration $Configuration -Framework $Framework
$dll = if ($SampleDll) { $SampleDll } else { Join-Path $root "samples/SampleApp/bin/$Configuration/$Framework/SampleApp.dll" }
$src = Join-Path $root "samples/SampleApp/Program.cs"
# The line marked BREAKPOINT-TARGET in the sample, found the way the tests do (TestPaths), so
# the script does not go stale when the sample changes. Its text, without the trailing comment,
# is what the content breakpoint is asked to match.
$srcLines = Get-Content -LiteralPath $src
$markerLine = 0
for ($i = 0; $i -lt $srcLines.Count; $i++) {
    if ($srcLines[$i].Contains("BREAKPOINT-TARGET")) { $markerLine = $i + 1; break }
}
if ($markerLine -eq 0) { throw "BREAKPOINT-TARGET marker not found in $src" }
$markerText = ($srcLines[$markerLine - 1] -replace '//.*$', '').Trim()
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
    $bp = Call "set_breakpoint" @{ file = $src; content = $markerText }
    if ($bp.line -ne $markerLine) { throw "expected content match at line $markerLine, got $($bp.line)" }
    Write-Host "[ok] content breakpoint resolved to line $($bp.line), verified=$($bp.verified)" -ForegroundColor Green

    $stop = Call "continue" @{ timeoutMs = 15000 }
    if ($stop.stop.line -ne $markerLine) { throw "expected stop at $markerLine, got $($stop.stop.line)" }
    Write-Host "[ok] stopped at line $($stop.stop.line)"

    # Restart: same session id, breakpoint preserved, hits again.
    $re = Call "restart_debugging" @{}
    if ($re.sessionId -ne $launch.sessionId) { throw "restart changed session id" }
    Write-Host "[ok] restarted, same session id $($re.sessionId)" -ForegroundColor Green
    $bps = Call "list_breakpoints" @{}
    if (-not $bps) { throw "breakpoints lost across restart" }
    Write-Host "[ok] breakpoint survived restart (count=$(@($bps).Count))" -ForegroundColor Green

    $stop2 = Call "continue" @{ timeoutMs = 15000 }
    if ($stop2.stop.line -ne $markerLine) { throw "post-restart stop not at $markerLine, got $($stop2.stop.line)" }
    Write-Host "[ok] post-restart breakpoint hit at line $($stop2.stop.line)" -ForegroundColor Green

    $cleared = Call "clear_all_breakpoints" @{}
    Write-Host "[ok] clear_all_breakpoints -> '$cleared'" -ForegroundColor Green
    $bps2 = @(Call "list_breakpoints" @{})
    if (@($bps2).Count -ne 0) { throw "breakpoints not cleared" }
    Write-Host "[ok] no breakpoints remain" -ForegroundColor Green

    Call "debug_stop" @{} | Out-Null
    Write-Host "`nSMOKE PASSED" -ForegroundColor Green
}
finally {
    Stop-Driver
}
