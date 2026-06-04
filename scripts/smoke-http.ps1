# Smoke for the HTTP transport + bearer auth. Verifies: (1) it fails closed with
# no token, (2) rejects requests without/with a wrong bearer (401), (3) completes
# an MCP initialize handshake with the right bearer.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src/ClrVoyant.Server/bin/Debug/net8.0/ClrVoyant.Server.exe"
$port = 3017
$url = "http://127.0.0.1:$port"
$token = "test-secret-token"

function Start-Server($env) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    foreach ($k in $env.Keys) { $psi.Environment[$k] = $env[$k] }
    return [System.Diagnostics.Process]::Start($psi)
}

# (1) Fail closed: HTTP transport with no auth token must refuse to start.
Write-Host "[case 1] HTTP without CLRVOYANT_AUTH_TOKEN must fail closed"
$p = Start-Server @{ CLRVOYANT_TRANSPORT = "http"; CLRVOYANT_HTTP_URL = $url }
$p.WaitForExit(8000) | Out-Null
if (-not $p.HasExited) { $p.Kill($true); throw "FAIL: server stayed up without a token (RCE risk)" }
$err = $p.StandardError.ReadToEnd()
if ($err -notmatch "requires authentication") { throw "FAIL: expected auth error, got: $err" }
Write-Host "[ok] refused to start without a token" -ForegroundColor Green

# (2)+(3) Start authenticated server.
Write-Host "`n[case 2/3] starting authenticated HTTP server on $url"
$srv = Start-Server @{ CLRVOYANT_TRANSPORT = "http"; CLRVOYANT_HTTP_URL = $url; CLRVOYANT_AUTH_TOKEN = $token }
try {
    # Wait for it to listen.
    $up = $false
    for ($i = 0; $i -lt 40; $i++) {
        try { Invoke-WebRequest "$url/" -Method GET -SkipHttpErrorCheck -TimeoutSec 2 | Out-Null; $up = $true; break }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $up) { throw "server did not start listening" }

    # No bearer -> 401.
    $r = Invoke-WebRequest "$url/" -Method POST -SkipHttpErrorCheck -TimeoutSec 5 `
        -Headers @{ Accept = "application/json, text/event-stream" } -ContentType "application/json" -Body "{}"
    if ($r.StatusCode -ne 401) { throw "FAIL: expected 401 without bearer, got $($r.StatusCode)" }
    Write-Host "[ok] 401 without bearer" -ForegroundColor Green

    # Wrong bearer -> 401.
    $r = Invoke-WebRequest "$url/" -Method POST -SkipHttpErrorCheck -TimeoutSec 5 `
        -Headers @{ Authorization = "Bearer nope"; Accept = "application/json, text/event-stream" } `
        -ContentType "application/json" -Body "{}"
    if ($r.StatusCode -ne 401) { throw "FAIL: expected 401 with wrong bearer, got $($r.StatusCode)" }
    Write-Host "[ok] 401 with wrong bearer" -ForegroundColor Green

    # Correct bearer + MCP initialize -> not 401, and a session/result comes back.
    $init = @{
        jsonrpc = "2.0"; id = 1; method = "initialize"
        params  = @{ protocolVersion = "2024-11-05"; capabilities = @{}; clientInfo = @{ name = "smoke"; version = "1" } }
    } | ConvertTo-Json -Depth 8
    $r = Invoke-WebRequest "$url/" -Method POST -SkipHttpErrorCheck -TimeoutSec 10 `
        -Headers @{ Authorization = "Bearer $token"; Accept = "application/json, text/event-stream" } `
        -ContentType "application/json" -Body $init
    if ($r.StatusCode -eq 401) { throw "FAIL: correct bearer rejected" }
    $hasSession = $r.Headers["Mcp-Session-Id"] -or ($r.Content -match "serverInfo|protocolVersion")
    if (-not $hasSession) { throw "FAIL: initialize did not return a session/result (status $($r.StatusCode)): $($r.Content)" }
    Write-Host "[ok] MCP initialize accepted with bearer (status $($r.StatusCode))" -ForegroundColor Green

    Write-Host "`nHTTP SMOKE PASSED" -ForegroundColor Green
}
finally {
    if (-not $srv.HasExited) { $srv.Kill($true) }
}
