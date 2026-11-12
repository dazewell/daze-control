<#
Reruns the failed jobs of the PR head's latest failed CI run and prints the failed test names, so nobody investigates known flakes
(TriggerServiceTests, docs/DEADENDS.md X-012). Reruns only when every failed test is a known flake; a real failing test is printed
and NOT rerun, and neither is a failure with no parsed test (check the log tail). -Force reruns anyway.
Usage: pwsh scripts/pr-rerun.ps1 <pr> [-DryRun] [-Force]
Exit: 0 rerun started (or nothing failed), 1 real failure (fix it) or error.
#>
param([Parameter(Mandatory, Position = 0)][int]$Pr, [switch]$DryRun, [switch]$Force)
. "$PSScriptRoot/pr-lib.ps1"
$ErrorActionPreference = 'Stop'
$d = Get-PrData $Pr
$runs = gh run list --commit $d.Head --json databaseId,conclusion,status,workflowName -L 10 | ConvertFrom-Json
$bad = @($runs | Where-Object { $_.status -eq 'completed' -and $_.conclusion -in 'failure', 'timed_out' } | Select-Object -First 1)
if (-not $bad) { "#$Pr no failed run on head $($d.Head.Substring(0,7)) (ci: $($d.Ci))"; exit 0 }
$run = $bad[0].databaseId
$attempt = [int](gh run view $run --json attempt -q .attempt 2>$null)
$log = @(gh run view $run --log-failed 2>&1)
$tests = Get-FailedTests $log
"run $run ($($bad[0].workflowName)) failed tests: $(if ($tests) { $tests -join ', ' } else { 'none parsed (build/infra failure)' })"
$real = @($tests | Where-Object { -not (Test-Flaky $_) })
if ($real -and -not $Force) { "REAL failure, not rerunning: $($real -join ', ')"; exit 1 }
if (-not $tests -and -not $Force) {
  $log | Select-Object -Last 6 | ForEach-Object { Format-Text $_ 160 }
  'no failing test parsed: check the log tail above; -Force to rerun anyway'; exit 1
}
if ($attempt -ge 2 -and -not $Force) { "run $run already rerun (attempt $attempt) and failed again: not a flake, investigate (X-012); -Force to rerun"; exit 1 }
if ($DryRun) { "dry-run: would run gh run rerun $run --failed"; exit 0 }
gh run rerun $run --failed 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { 'rerun failed to start'; exit 1 }
"rerun started for run $run; pr-wait.ps1 $Pr -NoReview to wait"
exit 0
