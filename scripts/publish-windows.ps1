<#
.SYNOPSIS
    Native AOT-publishes erikterm for Windows and puts SDL3.dll and freetype.dll next to erikterm.exe.

.DESCRIPTION
    Run this on Windows: Native AOT can't cross-compile from Linux. Needs the .NET 11 SDK and the
    MSVC linker (Visual Studio 2022+ or "Build Tools for Visual Studio" with the
    "Desktop development with C++" workload).

    The native DLLs are not downloaded by this script. Put them in deps\<rid>\ first:
        deps\win-x64\SDL3.dll
        deps\win-x64\freetype.dll      (libfreetype-6.dll from MSYS2 also works)
        deps\win-x64\harfbuzz.dll      (optional, for ligatures; libharfbuzz-0.dll from MSYS2 also works)
    plus any DLLs those depend on (e.g. zlib1.dll / libpng16.dll for a FreeType built with them).
    Every *.dll in that folder is copied.

    Microsoft's console host (conpty.dll and OpenConsole.exe, the one Windows Terminal ships) is the one
    thing this script does download: a pinned release, checked against its hash and cached in deps\.
    Windows only, since Linux and macOS have no ConPTY. See src\ErikTerm.Pty\ConPty.cs.

.EXAMPLE
    scripts\publish-windows.ps1
    scripts\publish-windows.ps1 -Runtime win-arm64 -Output artifacts\app-arm64
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [string]$Output = 'artifacts\app-win',
    [string]$Deps
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (-not $Deps) { $Deps = Join-Path 'deps' $Runtime }

    $required = @('SDL3.dll')
    foreach ($dll in $required) {
        if (-not (Test-Path (Join-Path $Deps $dll))) { throw "missing $Deps\$dll (see the header of this script)" }
    }
    if (-not (Test-Path (Join-Path $Deps 'freetype.dll')) -and -not (Test-Path (Join-Path $Deps 'libfreetype-6.dll'))) {
        throw "missing $Deps\freetype.dll (see the header of this script)"
    }

    dotnet publish src/ErikTerm.App -c $Configuration -r $Runtime -o $Output -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

    Get-ChildItem -Path $Deps -Filter *.dll | ForEach-Object {
        Copy-Item $_.FullName -Destination $Output -Force
        Write-Host "copied $($_.Name)"
    }

    # The console host, from the Windows Terminal release its package was published with.
    $conptyTag = 'v1.25.2733.0'
    $conptyVersion = '1.25.260930003'
    $conptySha256 = '6d6f8b008c655d814c498d8205d4012dcd4d0b6dfc1096461d0a185d1e3caf03'
    $cache = Join-Path 'deps' "conpty-$conptyVersion"
    if (-not (Test-Path (Join-Path $cache 'runtimes'))) {
        New-Item -ItemType Directory -Force $cache | Out-Null
        $package = Join-Path $cache 'conpty.nupkg'
        Invoke-WebRequest -UseBasicParsing -OutFile $package `
            -Uri "https://github.com/microsoft/terminal/releases/download/$conptyTag/Microsoft.Windows.Console.ConPTY.$conptyVersion.nupkg"
        if ((Get-FileHash $package -Algorithm SHA256).Hash -ne $conptySha256) {
            Remove-Item $package
            throw "the ConPTY package does not match its expected hash"
        }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path $package).Path, (Resolve-Path $cache).Path)
    }
    $arch = $Runtime -replace '^win-', ''
    Copy-Item (Join-Path $cache "runtimes\win-$arch\native\conpty.dll") -Destination $Output -Force
    Copy-Item (Join-Path $cache "build\native\runtimes\$arch\OpenConsole.exe") -Destination $Output -Force
    New-Item -ItemType Directory -Force (Join-Path $Output 'licenses') | Out-Null
    Copy-Item 'packaging\windows\conpty-LICENSE.txt' -Destination (Join-Path $Output 'licenses\conpty.txt') -Force
    Write-Host "copied conpty.dll and OpenConsole.exe ($conptyVersion)"

    Write-Host "done: $(Join-Path $Output 'erikterm.exe')"
}
finally {
    Pop-Location
}
