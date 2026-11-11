# erikterm shell integration for PowerShell (pwsh 7 and Windows PowerShell 5.1) — works with oh-my-posh,
# starship and plain prompts.
#
#   OSC 133 (semantic prompts): A prompt start · B command start · C output start · D;status end.
#     Enables ScrollToPrompt (Shift+Up/Down), Select/Copy Command Output, failed-command markers.
#   OSC 7: the working directory, so new splits open where you are (pwsh doesn't move its process
#     directory on cd, so without this erikterm can't follow it).
#
# erikterm loads it by itself (shell_integration = "auto": `-NoExit -Command ". 'erikterm.ps1'"`, after your
# profile). To load it by hand instead, at the end of $PROFILE, after your prompt is set up:
#
#   if ($env:TERM_PROGRAM -eq 'erikterm') { . /path/to/erikterm/contrib/shell/erikterm.ps1 }
#
# How: the prompt function is wrapped (marks around it, D and OSC 7 before it) and $? is handed on to it
# unchanged, so a prompt theme still sees whether the last command failed. The command line goes with C
# from PSReadLine's AddToHistoryHandler, which runs after Enter, before the command; an existing handler is
# called first, and a line it keeps out of history (or out of the history file) is marked without its text.
# A prompt that emits OSC 133 itself (oh-my-posh with shell_integration = true) keeps its own marks; then
# this script only reports the directory.

if ($Host.Name -ne 'ConsoleHost') { return }
if ($env:TMUX -and -not $env:TMUX.StartsWith("$env:ERIKTERM_SOCKET,")) { return }  # real tmux (not erikterm's emulation)
if ($global:__ErikTermIntegration) { return }
$global:__ErikTermIntegration = $true

$global:__ErikTermState = @{
    Esc            = [string][char]27
    LastHistoryId  = $null    # set by the first prompt: the -Command that loaded this script is in the history too
    OwnMarks       = $false   # the prompt marks itself
    OriginalPrompt = $function:prompt
}

# Percent-encodes a string (UTF-8) for a URL, keeping '/'.
function global:__ErikTermUrlEncode([string]$text) {
    ($text -split '/' | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
}

# OSC 7: file://host/path; a Windows path becomes /C:/src.
function global:__ErikTermOsc7 {
    $location = $ExecutionContext.SessionState.Path.CurrentLocation
    if ($location.Provider.Name -ne 'FileSystem') { return '' }
    $path = $location.ProviderPath -replace '\\', '/'
    if (-not $path.StartsWith('/')) { $path = '/' + $path }
    $s = $global:__ErikTermState
    "$($s.Esc)]7;file://$([Environment]::MachineName)$(__ErikTermUrlEncode $path)$($s.Esc)\"
}

function global:prompt {
    $ok = $?
    $exitCode = $global:LASTEXITCODE
    $s = $global:__ErikTermState
    $e = $s.Esc
    $out = ''

    # A command ran when PowerShell's own history moved on (an empty Enter doesn't).
    $last = Get-History -Count 1
    $lastId = if ($last) { $last.Id } else { 0 }
    if ($null -eq $s.LastHistoryId) { $s.LastHistoryId = $lastId }
    elseif ($lastId -ne $s.LastHistoryId) {
        $s.LastHistoryId = $lastId
        if (-not $s.OwnMarks) {
            $code = if ($ok) { 0 } elseif ($exitCode -is [int] -and $exitCode -ne 0) { $exitCode } else { 1 }
            $out += "$e]133;D;$code$e\"
        }
    }
    $out += __ErikTermOsc7

    # Hand $? and $LASTEXITCODE on to the user's prompt as they were.
    $global:LASTEXITCODE = $exitCode
    if (-not $ok) { Write-Error '' -ErrorAction Ignore }
    $inner = if ($s.OriginalPrompt) { & $s.OriginalPrompt } else { "PS $($ExecutionContext.SessionState.Path.CurrentLocation)$('>' * ($NestedPromptLevel + 1)) " }
    $inner = ($inner | ForEach-Object { "$_" }) -join ''
    $global:LASTEXITCODE = $exitCode

    if ($inner.Contains("$e]133;")) { $s.OwnMarks = $true }
    if ($s.OwnMarks) { return $out + $inner }
    $out + "$e]133;A$e\" + $inner + "$e]133;B$e\"
}

# Output starts: C, with the command line unless the history handler keeps the line out of the history file.
if (Get-Command Set-PSReadLineOption -ErrorAction Ignore) {
    $global:__ErikTermState.OriginalHistoryHandler = (Get-PSReadLineOption).AddToHistoryHandler
    Set-PSReadLineOption -AddToHistoryHandler {
        param([string]$line)
        $s = $global:__ErikTermState
        $result = $true
        $original = $s.OriginalHistoryHandler
        if ($original -is [scriptblock]) { $result = & $original $line }
        elseif ($original) { $result = $original.Invoke($line) }
        if (-not $s.OwnMarks) {
            $e = $s.Esc
            $private = ($result -eq $false) -or ("$result" -in 'SkipAdding', 'MemoryOnly')
            $text = if ($private -or $line.Contains("`n")) { '' } else { ";cmdline_url=$(__ErikTermUrlEncode ($line.Substring(0, [Math]::Min($line.Length, 2048))))" }
            [Console]::Write("$e]133;C$text$e\")
        }
        $result
    }
}
