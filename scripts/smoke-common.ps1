# Shared helpers for the smoke scripts (dot-source this file). Cross-platform:
# works with pwsh 7 on Windows and Linux.

# Target framework of the server and of the sample debuggees the smokes launch.
$script:DefaultFramework = 'net10.0'

# Resolves the ClrVoyant server to launch.
#   -ServerPath  explicit path to the apphost (ClrVoyant.Server[.exe]) or to
#                ClrVoyant.Server.dll (then it is run with `dotnet`).
#   otherwise    <repo>/src/ClrVoyant.Server/bin/<Configuration>/<Framework>/, preferring
#                the apphost (".exe" only on Windows) and falling back to the .dll.
function Resolve-ServerPath {
    param(
        [string]$ServerPath,
        [string]$Configuration = 'Debug',
        [string]$Framework = $script:DefaultFramework
    )
    if ($ServerPath) {
        if (-not (Test-Path -LiteralPath $ServerPath)) { throw "Server not found: $ServerPath" }
        return (Resolve-Path -LiteralPath $ServerPath).Path
    }
    $root = Split-Path -Parent $PSScriptRoot
    $dir = Join-Path $root "src/ClrVoyant.Server/bin/$Configuration/$Framework"
    $hostName = if ($IsWindows) { 'ClrVoyant.Server.exe' } else { 'ClrVoyant.Server' }
    foreach ($name in @($hostName, 'ClrVoyant.Server.dll')) {
        $candidate = Join-Path $dir $name
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    throw "Server not built: looked for $hostName and ClrVoyant.Server.dll in $dir. Build first (dotnet build -c $Configuration) or pass -ServerPath."
}

# ProcessStartInfo for the server: the apphost directly, or `dotnet <dll>`.
function New-ServerStartInfo {
    param([Parameter(Mandatory)][string]$ServerPath)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    if ($ServerPath.EndsWith('.dll', [System.StringComparison]::OrdinalIgnoreCase)) {
        $psi.FileName = 'dotnet'
        $psi.ArgumentList.Add($ServerPath)
    }
    else {
        $psi.FileName = $ServerPath
    }
    $psi.UseShellExecute = $false
    return $psi
}
