<#
Blocks until PR(s) are merged or closed, then prints one line per PR and exits (0 = all merged, 1 = any closed unmerged).
Run it with the Bash/PowerShell tool's run_in_background: the agent is re-invoked when it exits, so waiting costs no
model calls (D-079). Usage: pwsh scripts/wait-pr.ps1 60,61 [-IntervalSeconds 60] [-TimeoutMinutes 120]
#>
param([Parameter(Mandatory, Position = 0)][string]$Pr, [int]$IntervalSeconds = 60, [int]$TimeoutMinutes = 120)
$end = (Get-Date).AddMinutes($TimeoutMinutes)
$open = [System.Collections.Generic.HashSet[int]]::new([int[]]($Pr -split '[,\s]+' | Where-Object { $_ })); $bad = $false
while ($open.Count -gt 0 -and (Get-Date) -lt $end) {
  foreach ($n in @($open)) {
    $s = gh pr view $n --repo dazewell/daze-control --json state -q .state 2>$null
    if ($s -eq 'MERGED') { "#$n merged"; [void]$open.Remove($n) }
    elseif ($s -eq 'CLOSED') { "#$n closed without merging"; [void]$open.Remove($n); $bad = $true }
  }
  if ($open.Count -gt 0) { Start-Sleep -Seconds $IntervalSeconds }
}
foreach ($n in $open) { "#$n still open after $TimeoutMinutes min"; $bad = $true }
exit ($bad ? 1 : 0)
