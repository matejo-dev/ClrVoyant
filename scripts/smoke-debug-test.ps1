# End-to-end smoke for debug_test: one-step "run + suspend + attach" of a unit test.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src/ClrVoyant.Server/bin/Debug/net8.0/ClrVoyant.Server.exe"
$proj = Join-Path $root "tests/ClrVoyant.Tests"
$bpFile = Join-Path $root "src/ClrVoyant.Core/BreakpointLocator.cs"
$env:CLRVOYANT_NETCOREDBG = $null

. (Join-Path $PSScriptRoot "mcp-driver.ps1") -ServerExe $exe
try {
    Handshake

    # Filter to a single trivial test so only it runs after we attach.
    $info = Call "debug_test" @{ testProject = $proj; testName = "FullyQualifiedName~Exact_trimmed_match_wins"; timeoutMs = 180000 }
    Write-Host "[ok] debug_test attached session=$($info.sessionId) pid=$($info.pid) state=$($info.state)" -ForegroundColor Green

    # Set a breakpoint on a line the filtered test will execute.
    $bp = Call "set_breakpoint" @{ file = $bpFile; content = "string target = content.Trim();" }
    Write-Host "[ok] breakpoint at line $($bp.line) verified=$($bp.verified)"

    # The test host resumed on attach; the breakpoint hit arrives as a stop.
    $stop = Call "wait_for_any_stop" @{ timeoutMs = 60000 }
    if ($stop.timedOut) { throw "breakpoint was not hit (timed out)" }
    Write-Host "[ok] hit breakpoint in $($stop.stop.file):$($stop.stop.line)" -ForegroundColor Green

    # Prove introspection works in the test host.
    $frames = @(Call "get_callstack" @{})
    $scopes = @(Call "get_scopes" @{ frameId = $frames[0].id })
    $vars = @(Call "get_variables" @{ variablesReference = $scopes[0].variablesReference })
    Write-Host "[ok] read $($vars.Count) local(s) in frame '$($frames[0].function)'"

    Call "debug_stop" @{} | Out-Null
    Write-Host "`nDEBUG_TEST SMOKE PASSED" -ForegroundColor Green
}
finally {
    Stop-Driver
}
