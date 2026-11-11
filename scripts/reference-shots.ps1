<#
.SYNOPSIS
    Renders the reference scenes (bench\testing\scenes\*.txt) with an erikterm binary and saves one PNG per
    screenshot in bench\testing\reference\windows\, for side-by-side comparison with the Linux references.

.DESCRIPTION
    The Windows twin of scripts/reference-shots.sh. Works in Windows PowerShell 5.1 and PowerShell 7.

    Each scene runs in its own erikterm process (--new-process --no-restore, window class
    erikterm-refshots, so its session file is session-erikterm-refshots.json and never your session.json),
    driven by ERIKTERM_DEBUG_SCRIPT_FILE. The process is ended once its screenshots exist.

    PNG conversion uses ImageMagick (magick.exe on PATH) when installed, else System.Drawing. System.Drawing
    drops the alpha channel, so transparency.png is only faithful with ImageMagick
    (winget install ImageMagick.ImageMagick).

.PARAMETER Erikterm
    Path to erikterm.exe (e.g. artifacts\app-win\erikterm.exe or the unzipped CI artifact).

.PARAMETER Scene
    Scene names to run (file names without .txt, e.g. 03-rendering). Default: all.

.PARAMETER Out
    Output directory. Default: bench\testing\reference\windows.

.PARAMETER Keep
    Keep the temporary directory (generated scripts, BMPs, logs) and print its path.

.EXAMPLE
    scripts\reference-shots.ps1 artifacts\app-win\erikterm.exe
    scripts\reference-shots.ps1 C:\tools\erikterm\erikterm.exe -Scene 03-rendering,05-content
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Erikterm,
    [string[]]$Scene,
    [string]$Out,
    [string]$Class = 'erikterm-refshots',
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$scenesDir = Join-Path $root 'bench\testing\scenes'
$bin = (Resolve-Path $Erikterm).Path
if (-not $Out) { $Out = Join-Path $root 'bench\testing\reference\windows' }
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$utf8 = New-Object System.Text.UTF8Encoding $false

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("etshots-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$bmpDir = Join-Path $tmp 'bmp'
$cwd = Join-Path $tmp 'cwd'
New-Item -ItemType Directory -Force $bmpDir, $cwd | Out-Null

# The scene config, behind a program that prints nothing and waits (each pane runs it).
$config = Join-Path $tmp 'scene.toml'
$prog = 'default_prog = ["powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 600"]'
[IO.File]::WriteAllText($config, $prog + "`n" + [IO.File]::ReadAllText((Join-Path $scenesDir 'scene.toml'), $utf8), $utf8)

if ($Scene) { $scenes = $Scene | ForEach-Object { Join-Path $scenesDir (($_ -replace '\.txt$', '') + '.txt') } }
else { $scenes = Get-ChildItem (Join-Path $scenesDir '[0-9]*.txt') | Where-Object { $_.Name -notlike '12-*' -and $_.Name -notlike '13-*' -and $_.Name -notlike '14-*' } | Sort-Object Name | ForEach-Object { $_.FullName } }  # 12-worktree needs the fake agents reference-shots.sh sets up

function Quote([string]$s) {
    if ($s -notmatch '[\s"]') { return $s }
    return '"' + ($s -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

$magick = Get-Command magick -ErrorAction SilentlyContinue
if (-not $magick) { Add-Type -AssemblyName System.Drawing }
function Convert-Png([string]$bmp, [string]$png) {
    if ($magick) { & $magick.Source $bmp -strip -define png:compression-level=9 $png; return }
    $img = [System.Drawing.Image]::FromFile($bmp)
    try { $img.Save($png, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $img.Dispose() }
}

$failed = $false
$saved = @{}
foreach ($v in 'ERIKTERM_SOCKET', 'ERIKTERM_PANE', 'ERIKTERM_SCREENSHOT', 'ERIKTERM_DEBUG_SCRIPT', 'ERIKTERM_DEBUG_SCRIPT_FILE') {
    $saved[$v] = [Environment]::GetEnvironmentVariable($v)
    [Environment]::SetEnvironmentVariable($v, $null)
}
try {
    foreach ($scenePath in $scenes) {
        if (-not (Test-Path $scenePath)) { Write-Warning "no scene $scenePath"; $failed = $true; continue }
        $name = [IO.Path]::GetFileNameWithoutExtension($scenePath)
        $sets = New-Object System.Collections.Generic.List[string]
        $shots = New-Object System.Collections.Generic.List[string]
        $script = New-Object System.Collections.Generic.List[string]
        foreach ($line in [IO.File]::ReadAllLines($scenePath, $utf8)) {
            if ($line.StartsWith('# set: ')) { $sets.Add('--set'); $sets.Add($line.Substring(7)) }
            elseif ($line.StartsWith('feedfile:')) {
                # One feed: step with the file's lines joined by \r\n (C# escapes, as the debug script expects).
                $content = [IO.File]::ReadAllLines((Join-Path $scenesDir ('content\' + $line.Substring(9))), $utf8)
                $script.Add('feed:' + ($content -join '\r\n'))
            }
            else {
                $line = $line.Replace('{out}', $bmpDir)
                if ($line.StartsWith('shot:')) { $shots.Add($line.Substring(5)) }
                $script.Add($line)
            }
        }
        $scriptFile = Join-Path $tmp "$name.script"
        [IO.File]::WriteAllLines($scriptFile, $script, $utf8)

        Write-Host "${name}: $($shots.Count) screenshot(s)"
        $argList = @('--config-file', $config, '--no-restore', '--new-process', '--class', $Class) + $sets
        $env:ERIKTERM_DEBUG_SCRIPT_FILE = $scriptFile
        $p = Start-Process -FilePath $bin -ArgumentList (($argList | ForEach-Object { Quote $_ }) -join ' ') `
            -WorkingDirectory $cwd -PassThru `
            -RedirectStandardOutput (Join-Path $tmp "$name.out.log") -RedirectStandardError (Join-Path $tmp "$name.log")
        $env:ERIKTERM_DEBUG_SCRIPT_FILE = $null

        # Wait for every screenshot (or the process to exit, or 60 s), then end the process and its panes.
        $deadline = (Get-Date).AddSeconds(60)
        while ($true) {
            $missing = @($shots | Where-Object { -not ((Test-Path $_) -and (Get-Item $_).Length -gt 0) })
            if ($missing.Count -eq 0 -or $p.HasExited -or (Get-Date) -gt $deadline) { break }
            Start-Sleep -Milliseconds 200
        }
        Start-Sleep -Milliseconds 500 # the last BMP finishes writing
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($p.Id)" -ErrorAction SilentlyContinue)
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        foreach ($c in $children) { Stop-Process -Id $c.ProcessId -Force -ErrorAction SilentlyContinue }

        foreach ($s in $shots) {
            if ((Test-Path $s) -and (Get-Item $s).Length -gt 0) {
                $png = Join-Path $Out ([IO.Path]::GetFileNameWithoutExtension($s) + '.png')
                Convert-Png $s $png
                Write-Host "  $png"
            }
            else {
                Write-Warning "MISSING $([IO.Path]::GetFileNameWithoutExtension($s)); erikterm's log:"
                Get-Content (Join-Path $tmp "$name.log") -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "    $_" }
                $failed = $true
            }
        }
    }
}
finally {
    foreach ($v in $saved.Keys) { [Environment]::SetEnvironmentVariable($v, $saved[$v]) }
    # The scene windows' own session file (their --class keeps it apart from session.json).
    Remove-Item (Join-Path $env:LOCALAPPDATA "erikterm\session-$Class.json") -ErrorAction SilentlyContinue
    if ($Keep) { Write-Host "kept $tmp" } else { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
}
if ($failed) { exit 1 }
