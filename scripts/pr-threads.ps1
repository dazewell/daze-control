<#
Lists unresolved review threads (id, file:line, author, body cut to 300 chars) and top-level review bodies. Read-only.
Usage: pwsh scripts/pr-threads.ps1 <pr> [-All] [-Full <thread-or-review-id>]
  -All    also resolved threads (marked)   -Full <id>  every comment of that thread / the whole review body
Exit: 0 (unless the PR can't be read).
#>
param([Parameter(Mandatory, Position = 0)][int]$Pr, [switch]$All, [string]$Full)
. "$PSScriptRoot/pr-lib.ps1"
$ErrorActionPreference = 'Stop'
$d = Get-PrData $Pr
if ($Full) {
  $t = $d.Threads | Where-Object { $_.Id.StartsWith($Full) } | Select-Object -First 1
  if ($t) { "$($t.Id) $($t.Path):$($t.Line) resolved=$($t.Resolved)"; foreach ($c in $t.Comments) { "--- @$($c.Author)"; $c.Body }
    if ($t.CommentsTotal -gt $t.Comments.Count) { "(truncated: showing $($t.Comments.Count) of $($t.CommentsTotal) comments)" }; exit 0 }
  $r = $d.Reviews | Where-Object { $_.Id.StartsWith($Full) } | Select-Object -First 1
  if ($r) { "review $($r.Id) @$($r.Author) $($r.State)"; $r.Body; exit 0 }
  "no thread or review with id '$Full'"; exit 0
}
"#$Pr threads: $(@(Get-Unresolved $d).Count) unresolved of $($d.Threads.Count)"
foreach ($t in $d.Threads) { if ($All -or -not $t.Resolved) { "$(if ($t.Resolved) { '[resolved] ' })$(Format-Thread $t)" } }
$tops = @($d.Reviews | Where-Object { $_.Body -and $_.Body.Trim() })
if ($tops) { "reviews with a body: $($tops.Count)"; foreach ($r in $tops) { Format-Review $r } }
