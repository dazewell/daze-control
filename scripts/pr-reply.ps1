<#
Replies to review thread(s) and resolves them, then re-queries and prints "unresolved: N" (the check every session did by hand).
Usage: pwsh scripts/pr-reply.ps1 <pr> "<thread-id>=<message>" ["<id>=<message>" ...] [-NoResolve] [-DryRun]
       pwsh scripts/pr-reply.ps1 <pr> -All "<one message for every unresolved thread>"
Ids may be a unique prefix of the id pr-threads.ps1 prints. A message may contain '='.
Exit: 0 all done and unresolved is 0; 1 something failed or threads remain.
#>
param([Parameter(Mandatory, Position = 0)][int]$Pr, [Parameter(Position = 1, ValueFromRemainingArguments)][string[]]$Pairs, [switch]$All, [switch]$NoResolve, [switch]$DryRun)
. "$PSScriptRoot/pr-lib.ps1"
$ErrorActionPreference = 'Stop'
$d = Get-PrData $Pr
$work = if ($All) {
  if (-not $Pairs) { throw '-All needs one message' }
  foreach ($t in (Get-Unresolved $d)) { [pscustomobject]@{ Thread = $t; Message = ($Pairs -join ' ') } }
} else {
  foreach ($p in (ConvertFrom-ReplyPairs $Pairs)) { [pscustomobject]@{ Thread = (Resolve-ThreadId $d $p.Id); Message = $p.Message } }
}
$fail = 0
foreach ($w in $work) {
  $id = $w.Thread.Id; $short = "$($w.Thread.Path):$($w.Thread.Line)"
  if ($DryRun) { "dry-run: would reply to $id ($short)$(if (-not $NoResolve) { ' and resolve' })"; continue }
  $q = 'mutation($t:ID!,$b:String!){addPullRequestReviewThreadReply(input:{pullRequestReviewThreadId:$t,body:$b}){comment{id}}}'
  gh api graphql -f query=$q -f t=$id -f b=$w.Message 2>&1 | Out-Null
  if ($LASTEXITCODE -ne 0) { "FAILED reply $id"; $fail++; continue }
  if (-not $NoResolve) {
    $q = 'mutation($t:ID!){resolveReviewThread(input:{threadId:$t}){thread{isResolved}}}'
    gh api graphql -f query=$q -f t=$id 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { "FAILED resolve $id"; $fail++; continue }
  }
  "ok $id ($short)$(if (-not $NoResolve) { ' replied+resolved' } else { ' replied' })"
}
if ($DryRun) { exit 0 }
$left = @(Get-Unresolved (Get-PrData $Pr)).Count
"unresolved: $left"
exit $(if ($fail -or ($left -and -not $NoResolve)) { 1 } else { 0 })
