<#
BLOCKS until the Copilot review has landed (if one is pending) and CI has finished, then prints the result and exits.
Run with run_in_background so the agent is re-invoked on exit and the wait costs no model turns (D-079, D-122).
Usage: pwsh scripts/pr-wait.ps1 <pr> [-IntervalSeconds 30] [-TimeoutMinutes 30] [-NoReview] [-NoCi]
Exit: 0 review in (or none pending) and CI green; 1 CI failed; 2 timed out; 3 PR closed/merged or no review was requested.
#>
param([Parameter(Mandatory, Position = 0)][int]$Pr, [int]$IntervalSeconds = 30, [int]$TimeoutMinutes = 30, [switch]$NoReview, [switch]$NoCi)
. "$PSScriptRoot/pr-lib.ps1"
$ErrorActionPreference = 'Stop'
$repo = Get-RepoSlug; $end = (Get-Date).AddMinutes($TimeoutMinutes)
$d = Get-PrData $Pr $repo
if (-not $NoReview -and -not $d.CopilotPending -and $d.CopilotReviews.Count -eq 0 -and (Test-CopilotRequired $d)) {
  "#$Pr no copilot review requested: run pr-request-review.ps1 first"; exit 3
}
while ($true) {
  if ($d.State -ne 'OPEN') { "#$Pr is $($d.State)"; exit 3 }
  $reviewDone = $NoReview -or -not $d.CopilotPending
  $ciDone = $NoCi -or $d.Ci -in 'pass', 'fail'
  if ($reviewDone -and $ciDone) { break }
  if ((Get-Date) -ge $end) { "#$Pr timed out after $TimeoutMinutes min (review pending: $($d.CopilotPending), ci: $($d.Ci))"; exit 2 }
  Start-Sleep -Seconds $IntervalSeconds
  $d = Get-PrData $Pr $repo
}
if ($d.CopilotReviews.Count) {
  $r = $d.CopilotReviews[-1]
  "review: copilot $($r.State) on $($r.Commit.Substring(0,7)), $(@(Get-Unresolved $d).Count) unresolved thread(s)"
} else { 'review: none' }
switch ($d.Ci) {
  'pass' { "ci: pass ($($d.CiTotal) checks)" }
  'fail' { "ci: FAIL $($d.CiFailed -join ', ')" }
  default { "ci: $($d.Ci)" }
}
exit $(if ($d.Ci -eq 'fail') { 1 } else { 0 })
