<#
.SYNOPSIS
    Prints a content\*.vt file through the terminal you run it in (real escape bytes, through ConPTY),
    e.g. to compare with the scripted reference scene, which feeds the same text straight into the parser.

.EXAMPLE
    bench\testing\scenes\print-vt.ps1 bench\testing\scenes\content\text.vt

.NOTES
    Use PowerShell 7 (pwsh): it writes UTF-8 and VT sequences as is. ConPTY re-renders what programs print,
    so a difference between this and the scripted scene on Windows usually means ConPTY dropped or rewrote
    a sequence (e.g. images, OSC 8, OSC 7501, underline styles); write down which.
#>
param([Parameter(Mandatory = $true)][string]$Path)
$utf8 = New-Object System.Text.UTF8Encoding $false
$lines = [IO.File]::ReadAllLines((Resolve-Path $Path).Path, $utf8)
[Console]::OutputEncoding = $utf8
[Console]::Out.Write([regex]::Unescape($lines -join '\r\n') + "`r`n")
