<#
.SYNOPSIS
  Writes a short Markdown coverage summary from Cobertura files to the GitHub
  Actions job summary ($GITHUB_STEP_SUMMARY), or to stdout when run locally.

.DESCRIPTION
  Cross-platform (Windows PowerShell 5.1 and pwsh 7 on Linux/Windows). Informational
  only: it never fails the build and applies no threshold.

.PARAMETER ResultsDirectory
  Directory searched recursively for coverage.cobertura.xml (default: ./TestResults).

.PARAMETER Title
  Heading of the summary section.
#>
[CmdletBinding()]
param(
    [string] $ResultsDirectory = (Join-Path (Get-Location) 'TestResults'),
    [string] $Title = 'Coverage'
)

$ErrorActionPreference = 'Stop'
$inv = [System.Globalization.CultureInfo]::InvariantCulture

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("### $Title")
$lines.Add('')

$files = @()
if (Test-Path -LiteralPath $ResultsDirectory) {
    $files = @(Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -Filter 'coverage.cobertura.xml' -File)
}

if ($files.Count -eq 0) {
    $lines.Add("No ``coverage.cobertura.xml`` found under ``$ResultsDirectory`` (tests may have failed before producing coverage).")
}
else {
    $lines.Add('| Package | Line coverage | Branch coverage |')
    $lines.Add('| --- | ---: | ---: |')
    foreach ($file in $files) {
        [xml] $doc = Get-Content -LiteralPath $file.FullName -Raw
        $root = $doc.coverage
        foreach ($pkg in $root.packages.package) {
            $line = [double]::Parse($pkg.'line-rate', $inv) * 100
            $branch = [double]::Parse($pkg.'branch-rate', $inv) * 100
            $lines.Add(('| {0} | {1:N1}% | {2:N1}% |' -f $pkg.name, $line, $branch))
        }
        $total = [double]::Parse($root.'line-rate', $inv) * 100
        $totalBranch = [double]::Parse($root.'branch-rate', $inv) * 100
        $lines.Add(('| **Total** | **{0:N1}%** | **{1:N1}%** |' -f $total, $totalBranch))
        $lines.Add('')
    }
}

$text = ($lines -join "`n") + "`n"
if ($env:GITHUB_STEP_SUMMARY) {
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $text -Encoding utf8
}
else {
    Write-Output $text
}
