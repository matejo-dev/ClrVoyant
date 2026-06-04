# Reusable MCP stdio test driver for the ClrVoyant server.
# Spawns the server, performs the JSON-RPC handshake, and exposes Call().
param([string]$ServerExe)

$ErrorActionPreference = "Stop"
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $ServerExe
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$script:proc = [System.Diagnostics.Process]::Start($psi)
$script:si = $script:proc.StandardInput
$script:so = $script:proc.StandardOutput
$script:n = 0

function Send-Raw($obj) { $script:si.WriteLine(($obj | ConvertTo-Json -Compress -Depth 16)) }

function Read-Id($wanted) {
    while ($true) {
        $line = $script:so.ReadLine()
        if ($null -eq $line) { return $null }
        try { $o = $line | ConvertFrom-Json } catch { continue }
        if ($o.id -eq $wanted) { return $o }
    }
}

function Call($name, $cargs) {
    $script:n++; $myid = $script:n
    Send-Raw @{ jsonrpc = "2.0"; id = $myid; method = "tools/call"; params = @{ name = $name; arguments = $cargs } }
    $resp = Read-Id $myid
    if ($null -eq $resp) { throw "no response for $name" }
    if ($resp.error) { throw "tool '$name' error: $($resp.error | ConvertTo-Json -Compress)" }
    $txt = $resp.result.content[0].text
    if ($resp.result.isError) { throw "tool '$name' failed: $txt" }
    if ([string]::IsNullOrWhiteSpace($txt)) { return $null }
    try { return ($txt | ConvertFrom-Json) } catch { return $txt }  # non-JSON (e.g. "ok")
}

function Handshake() {
    $script:n++
    Send-Raw @{ jsonrpc = "2.0"; id = $script:n; method = "initialize"; params = @{ protocolVersion = "2024-11-05"; capabilities = @{}; clientInfo = @{ name = "driver"; version = "1" } } }
    Read-Id $script:n | Out-Null
    Send-Raw @{ jsonrpc = "2.0"; method = "notifications/initialized" }
}

function Stop-Driver() {
    try { $script:si.Close() } catch {}
    $script:proc.WaitForExit(3000) | Out-Null
    if (-not $script:proc.HasExited) { $script:proc.Kill() }
}
