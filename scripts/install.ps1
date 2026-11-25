#requires -Version 7.0
<#
.SYNOPSIS
    Install ClrVoyant as a .NET global tool (`clrvoyant` on PATH) and optionally
    register it in a coding-agent client's MCP config.

.DESCRIPTION
    Packs the server and installs/updates the `ClrVoyant` global tool, so the client
    config points at the command `clrvoyant` rather than a build folder. With
    -Client it also writes the entry into that client's config (preserving other
    servers); the default just prints the snippet. Before an existing config file
    is rewritten, a timestamped copy `<config>.bak.<yyyyMMddHHmmss>` is written next
    to it; the file is then updated in place, so its permissions are preserved.

.PARAMETER Client
    print (default) | claude-code | claude-desktop | cursor | windsurf | vscode

.PARAMETER ServerName
    Name of the MCP server entry (default: clrvoyant).

.PARAMETER NoToolInstall
    Skip pack + tool install (assume `clrvoyant` is already installed) and only
    print/register the config.

.PARAMETER Help
    Show this help (alias: -h).

.EXAMPLE
    ./scripts/install.ps1 -Client cursor
.EXAMPLE
    ./scripts/install.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('print', 'claude-code', 'claude-desktop', 'cursor', 'windsurf', 'vscode')]
    [string]$Client = 'print',
    [string]$ServerName = 'clrvoyant',
    [switch]$NoToolInstall,
    [Alias('h')]
    [switch]$Help
)

$ErrorActionPreference = 'Stop'

if ($Help) {
    Get-Help $PSCommandPath -Detailed
    return
}

$repoRoot = Split-Path -Parent $PSScriptRoot

# The client invokes the tool by name (the global tools dir is on PATH).
$entry = @{ command = 'clrvoyant' }

# Resolve the target config before doing anything slow or stateful, so a problem
# here does not leave the tool installed but unregistered.
$path = $null
$key = $null
if ($Client -ne 'print') {
    # $env:APPDATA exists only on Windows; elsewhere use the same location as install.sh.
    $claudeDesktopDir = if ($IsWindows) { Join-Path $env:APPDATA 'Claude' } else { Join-Path $HOME '.config/Claude' }
    $presets = @{
        'claude-code'    = @{ Path = (Join-Path $HOME '.claude.json');                           Key = 'mcpServers' }
        'claude-desktop' = @{ Path = (Join-Path $claudeDesktopDir 'claude_desktop_config.json'); Key = 'mcpServers' }
        'cursor'         = @{ Path = (Join-Path $HOME '.cursor/mcp.json');                       Key = 'mcpServers' }
        'windsurf'       = @{ Path = (Join-Path $HOME '.codeium/windsurf/mcp_config.json');      Key = 'mcpServers' }
        'vscode'         = @{ Path = (Join-Path $repoRoot '.vscode/mcp.json');                   Key = 'servers'    }
    }
    $path = $presets[$Client].Path
    $key = $presets[$Client].Key
}

if (-not $NoToolInstall) {
    $nupkg = Join-Path ([System.IO.Path]::GetTempPath()) "clrvoyant-nupkg-$([System.Guid]::NewGuid().ToString('N'))"
    try {
        Write-Host "Packing + installing the 'clrvoyant' global tool..." -ForegroundColor Cyan
        dotnet pack (Join-Path $repoRoot 'src/ClrVoyant.Server/ClrVoyant.Server.csproj') -c Release -o $nupkg --nologo
        if ($LASTEXITCODE -ne 0) { throw "pack failed" }
        # `tool update` installs if missing, updates if present.
        dotnet tool update --global ClrVoyant --add-source $nupkg
        if ($LASTEXITCODE -ne 0) { throw "tool install failed" }
    }
    finally {
        Remove-Item $nupkg -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Host "Installed. Command: clrvoyant" -ForegroundColor Green
}

if ($Client -eq 'print') {
    [ordered]@{ mcpServers = [ordered]@{ $ServerName = $entry } } | ConvertTo-Json -Depth 10
    Write-Host "`n(VS Code's mcp.json uses the key 'servers' instead of 'mcpServers'.)" -ForegroundColor DarkGray
    return
}

$config = @{}
$existed = (Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Length -gt 0)
if ($existed) {
    $raw = Get-Content -Raw -LiteralPath $path
    if (-not [string]::IsNullOrWhiteSpace($raw)) {
        # Throws on invalid JSON before anything has been written.
        $config = $raw | ConvertFrom-Json -AsHashtable
    }
}
if (-not $config.ContainsKey($key) -or $null -eq $config[$key]) { $config[$key] = @{} }
$config[$key][$ServerName] = $entry

# -Depth 100 (the maximum): the default would silently truncate deeply nested
# existing content (~/.claude.json holds a lot of nested state).
$json = $config | ConvertTo-Json -Depth 100

$dir = Split-Path -Parent $path
if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

if ($existed) {
    $backup = "$path.bak.$(Get-Date -Format 'yyyyMMddHHmmss')"
    Copy-Item -LiteralPath $path -Destination $backup
    Write-Host "Backed up the existing config to: $backup"
}

# Overwrites in place (not delete + create), so the file keeps its permissions.
Set-Content -LiteralPath $path -Value $json -Encoding utf8

Write-Host "Registered '$ServerName' -> command 'clrvoyant' in $Client config:" -ForegroundColor Green
Write-Host "  $path"
Write-Host "Restart $Client (or reload its MCP servers) to pick it up." -ForegroundColor DarkGray
