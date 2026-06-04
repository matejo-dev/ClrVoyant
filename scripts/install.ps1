#requires -Version 7.0
<#
.SYNOPSIS
    Install ClrVoyant as a .NET global tool (`clrvoyant` on PATH) and optionally
    register it in a coding-agent client's MCP config.

.DESCRIPTION
    Packs the server and installs/updates the `ClrVoyant` global tool, so the client
    config points at the command `clrvoyant` rather than a build folder. With
    -Client it also writes the entry into that client's config (preserving other
    servers); the default just prints the snippet.

.PARAMETER Client
    print (default) | claude-code | claude-desktop | cursor | windsurf | vscode

.PARAMETER NoToolInstall
    Skip pack + tool install (assume `clrvoyant` is already installed) and only
    print/register the config.

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
    [switch]$NoToolInstall
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $NoToolInstall) {
    $nupkg = Join-Path ([System.IO.Path]::GetTempPath()) "clrvoyant-nupkg-$([System.Guid]::NewGuid().ToString('N'))"
    Write-Host "Packing + installing the 'clrvoyant' global tool..." -ForegroundColor Cyan
    dotnet pack (Join-Path $repoRoot 'src/ClrVoyant.Server/ClrVoyant.Server.csproj') -c Release -o $nupkg --nologo
    if ($LASTEXITCODE -ne 0) { throw "pack failed" }
    # `tool update` installs if missing, updates if present.
    dotnet tool update --global ClrVoyant --add-source $nupkg
    if ($LASTEXITCODE -ne 0) { throw "tool install failed" }
    Remove-Item $nupkg -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Installed. Command: clrvoyant" -ForegroundColor Green
}

# The client invokes the tool by name (the global tools dir is on PATH).
$entry = @{ command = 'clrvoyant' }

if ($Client -eq 'print') {
    [ordered]@{ mcpServers = [ordered]@{ $ServerName = $entry } } | ConvertTo-Json -Depth 10
    Write-Host "`n(VS Code's mcp.json uses the key 'servers' instead of 'mcpServers'.)" -ForegroundColor DarkGray
    return
}

$appData = $env:APPDATA
$presets = @{
    'claude-code'    = @{ Path = (Join-Path $HOME '.claude.json');                         Key = 'mcpServers' }
    'claude-desktop' = @{ Path = (Join-Path $appData 'Claude/claude_desktop_config.json');  Key = 'mcpServers' }
    'cursor'         = @{ Path = (Join-Path $HOME '.cursor/mcp.json');                       Key = 'mcpServers' }
    'windsurf'       = @{ Path = (Join-Path $HOME '.codeium/windsurf/mcp_config.json');      Key = 'mcpServers' }
    'vscode'         = @{ Path = (Join-Path $repoRoot '.vscode/mcp.json');                   Key = 'servers'    }
}
$preset = $presets[$Client]
$path = $preset.Path; $key = $preset.Key

$config = @{}
if (Test-Path $path) {
    $raw = Get-Content -Raw -Path $path
    if (-not [string]::IsNullOrWhiteSpace($raw)) { $config = $raw | ConvertFrom-Json -AsHashtable }
}
if (-not $config.ContainsKey($key) -or $null -eq $config[$key]) { $config[$key] = @{} }
$config[$key][$ServerName] = $entry

$dir = Split-Path -Parent $path
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$config | ConvertTo-Json -Depth 10 | Set-Content -Path $path -Encoding utf8

Write-Host "Registered '$ServerName' -> command 'clrvoyant' in $Client config:" -ForegroundColor Green
Write-Host "  $path"
Write-Host "Restart $Client (or reload its MCP servers) to pick it up." -ForegroundColor DarkGray
