<#
.SYNOPSIS
    Fetch the bundled netcoredbg (Windows x64) into a destination folder — only if
    it is not already there. Verifies the download against a pinned SHA-256.
    Runs on Windows PowerShell 5.1 and PowerShell 7+ (no version-specific features).
.NOTES
    Invoked by the FetchNetcoredbg MSBuild target. netcoredbg is MIT-licensed
    (Samsung); see THIRD-PARTY-NOTICES.md. It is fetched at build time rather than
    committed to the repo (no large third-party binary in git history).
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Sha256,
    [Parameter(Mandatory)][string]$DestDir
)
$ErrorActionPreference = 'Stop'

$exe = Join-Path $DestDir 'netcoredbg.exe'
if (Test-Path $exe -PathType Leaf) { return }   # already present (file) — nothing to do

$asset = 'netcoredbg-win64.zip'
$url = "https://github.com/Samsung/netcoredbg/releases/download/$Version/$asset"
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "ncdbg-$Version-$([System.Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
try {
    $zip = Join-Path $tmp $asset
    Write-Host "fetch-netcoredbg: downloading $asset ($Version)..."
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $url -OutFile $zip

    # Hash + unzip via .NET APIs so this runs on stock Windows PowerShell 5.1 as
    # well as PowerShell 7 (no dependency on Get-FileHash / Expand-Archive modules).
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $hashBytes = $sha.ComputeHash([System.IO.File]::ReadAllBytes($zip)) } finally { $sha.Dispose() }
    $got = ([System.BitConverter]::ToString($hashBytes) -replace '-', '').ToLowerInvariant()
    if ($got -ne $Sha256.ToLowerInvariant()) {
        throw "netcoredbg checksum mismatch for $asset`n expected $Sha256`n got      $got"
    }

    $extract = Join-Path $tmp 'x'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $extract)
    $found = Get-ChildItem $extract -Recurse -Filter 'netcoredbg.exe' | Select-Object -First 1
    if (-not $found) { throw "netcoredbg.exe not found inside $asset" }

    New-Item -ItemType Directory -Force -Path $DestDir | Out-Null
    Copy-Item (Join-Path $found.Directory.FullName '*') -Destination $DestDir -Recurse -Force
    Write-Host "fetch-netcoredbg: installed to $DestDir"
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
