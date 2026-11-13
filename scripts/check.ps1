<#
.SYNOPSIS
    Runs the checks of CI's test, code quality and release scripts jobs on this machine, before you push.

.DESCRIPTION
    The same steps as scripts/check.sh (.github/workflows/build.yml):
      1. the tests:            dotnet test --project tests/ErikTerm.Tests
      2. the analyzers:        dotnet build src/ErikTerm.App -p:Quality=true (build/quality.globalconfig)
      3. method complexity:    scripts/check-complexity.py against build/complexity-baseline.txt
      4. the fuzz harness:     dotnet build fuzz/ErikTerm.Fuzz (it compiles; fuzzing is fuzz/run.sh, Linux)
      5. the release tooling:  node --test eng/ (skipped when node isn't installed)

    Every check runs, also after one fails. Exits 1 when any of them failed.

    Windows: the tests load SDL3, FreeType and HarfBuzz from their own output folder, so the DLLs in deps\win-x64\
    (see scripts/publish-windows.ps1) are copied there first.

.EXAMPLE
    scripts\check.ps1
#>
$ErrorActionPreference = 'Continue'
Set-Location (Split-Path -Parent $PSScriptRoot)

# Run inside erikterm, a shell has these. The tests and the tmux shim follow them to the running erikterm; CI has none.
foreach ($name in 'ERIKTERM_SOCKET', 'ERIKTERM_PANE', 'TMUX', 'TMUX_PANE') { Remove-Item "Env:$name" -ErrorAction Ignore }
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$results = [ordered]@{}
function Step([string]$Name, [scriptblock]$Run) {
    Write-Host "`n==> $Name"
    & $Run
    $results[$Name] = if ($LASTEXITCODE -eq 0) { 'ok' } else { 'FAILED' }
}

Step 'tests' {
    if ($IsWindows -and (Test-Path 'deps\win-x64')) {
        dotnet build tests/ErikTerm.Tests -nologo -v q
        if ($LASTEXITCODE -ne 0) { return }
        Copy-Item 'deps\win-x64\*.dll' 'tests\ErikTerm.Tests\bin\Debug\net11.0\'
        dotnet test --project tests/ErikTerm.Tests --no-build
    } else {
        dotnet test --project tests/ErikTerm.Tests
    }
}

$log = New-TemporaryFile
try {
    Step 'analyzers' {
        dotnet build src/ErikTerm.App -p:Quality=true --no-incremental -nologo -v q -clp:NoSummary | Tee-Object -FilePath $log
    }
    $python = if (Get-Command python3 -ErrorAction Ignore) { 'python3' } else { 'python' }
    Step 'method complexity' { & $python scripts/check-complexity.py $log build/complexity-baseline.txt }
} finally {
    Remove-Item $log -ErrorAction Ignore
}
Step 'fuzz harness' { dotnet build fuzz/ErikTerm.Fuzz -nologo -v q -clp:NoSummary }

if (Get-Command node -ErrorAction Ignore) {
    Step 'release scripts' { node --test eng/ }
} else {
    Write-Host "`n==> release scripts`nnode not found: skipped."
    $results['release scripts'] = 'skipped'
}

Write-Host ''
foreach ($entry in $results.GetEnumerator()) { Write-Host ('{0,-8} {1}' -f $entry.Value, $entry.Key) }
if ($results.Values -contains 'FAILED') { exit 1 }
exit 0
