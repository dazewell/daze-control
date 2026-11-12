# Shared helpers for scripts/pr-*.ps1 (D-122). Dot-source it: . "$PSScriptRoot/pr-lib.ps1"
# Pure functions (ConvertTo-PrData, Get-Verdict, Format-*, Get-FailedTests ...) are covered by scripts/tests/pr-lib.tests.ps1.
# Output rule for every pr-* script: one short line per fact, bodies cut to 300 chars, never JSON.

$script:PrQuery = @'
query($owner:String!,$name:String!,$n:Int!){repository(owner:$owner,name:$name){pullRequest(number:$n){
 number title state isDraft headRefOid mergeStateStatus mergeable
 files(first:100){totalCount nodes{path additions}}
 reviewThreads(first:100){totalCount nodes{id isResolved isOutdated path line originalLine comments(first:20){nodes{author{login} body}}}}
 reviews(last:30){nodes{id state body submittedAt author{login} commit{oid}}}
 timelineItems(last:30,itemTypes:[REVIEW_REQUESTED_EVENT]){nodes{... on ReviewRequestedEvent{createdAt requestedReviewer{... on User{login} ... on Bot{login}}}}}
 commits(last:1){nodes{commit{statusCheckRollup{contexts(first:100){nodes{__typename
   ... on CheckRun{name status conclusion} ... on StatusContext{context state}}}}}}}
}}}
'@

function Get-RepoSlug {
  $s = gh repo view --json nameWithOwner -q .nameWithOwner 2>$null
  if (-not $s) { throw 'cannot resolve the repo with gh (run from a checkout, gh logged in)' }
  $s.Trim()
}

function Format-Text([string]$Text, [int]$Max = 300) {
  $t = ($Text -replace '\s+', ' ').Trim()
  if ($t.Length -gt $Max) { $t.Substring(0, $Max) + '… (-Full for all)' } else { $t }
}

function Test-CodePath([string]$Path) {
  # "non-test code" for the Copilot-review rule: not docs, not tests (repo-workflow section 6)
  -not ($Path -match '^docs/' -or $Path -match '\.md$' -or $Path -match '^tests/' -or $Path -match '\.tests\.ps1$')
}

function ConvertTo-PrData($Raw) {
  $p = $Raw.data.repository.pullRequest
  if (-not $p) { throw 'PR not found' }
  $threads = @($p.reviewThreads.nodes | ForEach-Object {
      [pscustomobject]@{
        Id = $_.id; Resolved = [bool]$_.isResolved; Outdated = [bool]$_.isOutdated; Path = $_.path
        Line = $(if ($_.line) { $_.line } else { $_.originalLine })
        Comments = @($_.comments.nodes | ForEach-Object { [pscustomobject]@{ Author = $_.author.login; Body = $_.body } })
      } })
  $reviews = @($p.reviews.nodes | ForEach-Object {
      [pscustomobject]@{ Id = $_.id; Author = $_.author.login; State = $_.state; Body = $_.body; At = $_.submittedAt; Commit = $_.commit.oid } })
  $copRev = @($reviews | Where-Object { $_.Author -like 'copilot*' })
  $copReq = @($p.timelineItems.nodes | Where-Object { $_.requestedReviewer.login -like 'copilot*' } | ForEach-Object { $_.createdAt } | Sort-Object)
  $lastReq = if ($copReq) { $copReq[-1] } else { $null }
  $lastRev = if ($copRev) { ($copRev | Sort-Object At)[-1].At } else { $null }
  # pending = asked after the last review landed (GitHub empties reviewRequests once Copilot starts, so the timeline is the truth)
  $pending = [bool]($lastReq -and (-not $lastRev -or [datetime]$lastReq -gt [datetime]$lastRev))

  $ctx = @($p.commits.nodes[0].commit.statusCheckRollup.contexts.nodes | Where-Object { $_ })
  $failed = @(); $running = 0
  foreach ($c in $ctx) {
    if ($c.__typename -eq 'CheckRun') {
      if ($c.status -ne 'COMPLETED') { $running++ }
      elseif ($c.conclusion -in 'FAILURE', 'TIMED_OUT', 'CANCELLED', 'ACTION_REQUIRED', 'STARTUP_FAILURE') { $failed += $c.name }
    } else {
      if ($c.state -in 'PENDING', 'EXPECTED') { $running++ } elseif ($c.state -in 'FAILURE', 'ERROR') { $failed += $c.context }
    }
  }
  $ci = if ($ctx.Count -eq 0) { 'none' } elseif ($failed) { 'fail' } elseif ($running) { 'pending' } else { 'pass' }
  $adds = 0; $codeFiles = 0
  foreach ($f in $p.files.nodes) { if (Test-CodePath $f.path) { $adds += $f.additions; $codeFiles++ } }

  [pscustomobject]@{
    Number = $p.number; Title = $p.title; State = $p.state; IsDraft = [bool]$p.isDraft; Head = $p.headRefOid
    MergeState = $p.mergeStateStatus; Mergeable = $p.mergeable
    Threads = $threads; Reviews = $reviews; CopilotReviews = $copRev; CopilotPending = $pending
    Ci = $ci; CiFailed = $failed; CiRunning = $running; CiTotal = $ctx.Count
    CodeAdds = $adds; CodeFiles = $codeFiles; FilesTruncated = ($p.files.totalCount -gt 100)
  }
}

function Get-PrData([int]$Pr, [string]$Repo) {
  if (-not $Repo) { $Repo = Get-RepoSlug }
  $o, $n = $Repo -split '/'
  $json = gh api graphql -f query=$script:PrQuery -f owner=$o -f name=$n -F n=$Pr 2>&1
  if ($LASTEXITCODE -ne 0) { throw "gh graphql failed: $json" }
  ConvertTo-PrData ($json | ConvertFrom-Json)
}

function Get-Unresolved($D) { @($D.Threads | Where-Object { -not $_.Resolved }) }

function Test-CopilotRequired($D) { $D.CodeAdds -gt 100 }

# Returns @{ Ready; Blockers[]; Warnings[] }. $ConflictCheck: scriptblock returning 'conflict'|'clean'|'unknown' (git merge-tree), only called when GitHub reports a conflict.
function Get-Verdict($D, [scriptblock]$ConflictCheck = { 'unknown' }) {
  $b = [System.Collections.Generic.List[string]]::new(); $w = [System.Collections.Generic.List[string]]::new()
  if ($D.State -ne 'OPEN') { $b.Add("PR is $($D.State)") }
  if ($D.IsDraft) { $b.Add('PR is a draft (gh pr ready <n>)') }
  switch ($D.Ci) {
    'fail' { $b.Add("CI failed: $($D.CiFailed -join ', ') (pr-rerun.ps1 if flaky)") }
    'pending' { $b.Add("CI still running ($($D.CiRunning) of $($D.CiTotal))") }
    'none' { $b.Add('no CI checks yet (just pushed? pr-wait.ps1)') }
  }
  $un = Get-Unresolved $D
  if ($un.Count) { $b.Add("$($un.Count) unresolved thread(s) (pr-threads.ps1, then pr-reply.ps1)") }
  if (Test-CopilotRequired $D) {
    if ($D.CopilotPending) { $b.Add('Copilot review requested but not in yet (pr-wait.ps1)') }
    elseif ($D.CopilotReviews.Count -eq 0) { $b.Add("Copilot review required ($($D.CodeAdds) code lines added) and not requested (pr-request-review.ps1)") }
    elseif ($D.CopilotReviews[-1].Commit -ne $D.Head) { $w.Add('Copilot reviewed an older commit than the head (fine unless you pushed big changes since)') }
  } elseif ($D.CopilotPending) { $w.Add('a Copilot review is pending; merging now skips it') }
  if ($D.FilesTruncated) { $w.Add('more than 100 files: code-line count is a lower bound') }
  if ($D.MergeState -eq 'DIRTY' -or $D.Mergeable -eq 'CONFLICTING') {
    switch (& $ConflictCheck) {
      'conflict' { $b.Add('merge conflict with main (git merge origin/main, resolve, push)') }
      'clean' { $w.Add('GitHub reports a conflict but git merge-tree finds none: stale notice, recheck in a minute') }
      default { $b.Add('GitHub reports a merge conflict (could not verify locally; notices can be stale)') }
    }
  } elseif ($D.MergeState -eq 'UNKNOWN' -or $D.Mergeable -eq 'UNKNOWN') { $b.Add('GitHub is still computing mergeability: retry in a few seconds') }
  elseif ($D.MergeState -eq 'BEHIND') { $w.Add('branch is behind main (merge commit still fine unless the ruleset needs it up to date)') }
  elseif ($D.MergeState -eq 'BLOCKED' -and $b.Count -eq 0) { $b.Add('mergeStateStatus BLOCKED by a ruleset/required check not listed above (approval? required check name?)') }
  [pscustomobject]@{ Ready = ($b.Count -eq 0); Blockers = @($b); Warnings = @($w) }
}

# Local second opinion on a reported conflict; fetches main + the PR head, merge-tree on the exact head SHA.
function Test-LocalConflict([int]$Pr, [string]$Head) {
  git fetch -q origin main "pull/$Pr/head" 2>$null
  if ($LASTEXITCODE -ne 0) { return 'unknown' }
  git merge-tree --write-tree --quiet origin/main $Head 2>$null | Out-Null
  if ($LASTEXITCODE -eq 0) { 'clean' } elseif ($LASTEXITCODE -eq 1) { 'conflict' } else { 'unknown' }
}

function Write-Verdict($V, [string]$Sha) {
  foreach ($w in $V.Warnings) { "warn: $w" }
  if ($V.Ready) { "READY (head $($Sha.Substring(0,7)))" } else { "BLOCKED (head $($Sha.Substring(0,7)))"; foreach ($x in $V.Blockers) { "  - $x" } }
}

function Format-Thread($T, [int]$Max = 300) {
  $c = $T.Comments[0]
  $more = if ($T.Comments.Count -gt 1) { " (+$($T.Comments.Count - 1) replies)" } else { '' }
  $flag = if ($T.Outdated) { ' [outdated]' } else { '' }
  $loc = if ($T.Line) { "$($T.Path):$($T.Line)" } else { $T.Path }
  "$($T.Id) $loc @$($c.Author)$flag$more`n    $(Format-Text $c.Body $Max)"
}

function Format-Review($R, [int]$Max = 300) {
  "review $($R.Id) @$($R.Author) $($R.State) @$($R.Commit.Substring(0,7))`n    $(Format-Text $R.Body $Max)"
}

# "id=message" pairs; id may be a unique prefix, message keeps any further '='.
function ConvertFrom-ReplyPairs([string[]]$Pairs) {
  foreach ($p in $Pairs) {
    $i = $p.IndexOf('=')
    if ($i -lt 1 -or $i -eq $p.Length - 1) { throw "bad pair '$p': expected id=message" }
    [pscustomobject]@{ Id = $p.Substring(0, $i).Trim(); Message = $p.Substring($i + 1).Trim() }
  }
}

function Resolve-ThreadId($D, [string]$IdOrPrefix) {
  $m = @($D.Threads | Where-Object { $_.Id -eq $IdOrPrefix })
  if (-not $m) { $m = @($D.Threads | Where-Object { $_.Id.StartsWith($IdOrPrefix) }) }
  if ($m.Count -ne 1) { throw "thread '$IdOrPrefix' matches $($m.Count) threads of this PR" }
  $m[0]
}

# Known flaky tests: rerun the failed jobs instead of investigating (docs/DEADENDS.md X-012).
$script:KnownFlaky = @('TriggerServiceTests')

# Failed test names from `gh run view --log-failed` text (dotnet test / MTP print "failed <Name> (<time>)" or "[FAIL]").
function Get-FailedTests([string[]]$Log) {
  $names = foreach ($l in $Log) {
    $t = $l -replace '^.*?\d{4}-\d\d-\d\dT[\d:.]+Z\s?', ''
    if ($t -match '^\s*(?:failed|Failed)\s+(\S+)') { $Matches[1] }
    elseif ($t -match '^\s*\[FAIL\]\s+(\S+)') { $Matches[1] }
  }
  @($names | Where-Object { $_ } | Sort-Object -Unique)
}

function Test-Flaky([string]$Name) { [bool]($script:KnownFlaky | Where-Object { $Name -like "*$_*" }) }
