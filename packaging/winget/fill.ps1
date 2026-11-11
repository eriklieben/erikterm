# Fills the winget manifest templates next to this script for one release and writes them into OutDir, which then
# holds exactly the three manifests (wingetcreate submit reads every file in the folder).
# Used by release.yml (job winget):
#   pwsh packaging/winget/fill.ps1 -Version 1.0.0 -InstallerSha256 <sha256 of erikterm-win-x64-setup.exe> -ReleaseDate 2026-11-12 -OutDir out

param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $InstallerSha256,
    [Parameter(Mandatory)] [string] $ReleaseDate,
    [Parameter(Mandatory)] [string] $OutDir
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Not a release version (x.y.z, no v, no prerelease suffix): $Version" }
if ($InstallerSha256 -notmatch '^[0-9A-Fa-f]{64}$') { throw "Not a SHA-256: $InstallerSha256" }
if ($ReleaseDate -notmatch '^\d{4}-\d{2}-\d{2}$') { throw "Not a date (yyyy-MM-dd): $ReleaseDate" }

$values = @{
    '__VERSION__'          = $Version
    '__INSTALLER_SHA256__' = $InstallerSha256.ToUpperInvariant()
    '__RELEASE_DATE__'     = $ReleaseDate
}

$templates = Get-ChildItem -Path $PSScriptRoot -Filter 'ErikLieben.Terminal*.yaml'
if ($templates.Count -ne 3) { throw "Expected 3 manifest templates in $PSScriptRoot, found $($templates.Count)" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Get-ChildItem -Path $OutDir -File | Remove-Item

foreach ($template in $templates) {
    $text = Get-Content -Raw -Encoding utf8 -Path $template.FullName
    foreach ($key in $values.Keys) { $text = $text.Replace($key, $values[$key]) }
    $left = [regex]::Matches($text, '__[A-Z0-9_]+__') | ForEach-Object Value | Select-Object -Unique
    if ($left) { throw "Placeholder left in $($template.Name): $($left -join ', ')" }
    # UTF-8 without a BOM and LF line endings, like the manifests in winget-pkgs.
    $text = $text -replace "`r`n", "`n"
    [System.IO.File]::WriteAllText((Join-Path $OutDir $template.Name), $text, [System.Text.UTF8Encoding]::new($false))
}

Get-ChildItem -Path $OutDir -File | ForEach-Object { $_.FullName }
