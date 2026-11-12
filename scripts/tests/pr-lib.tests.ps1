# Plain-script tests for scripts/pr-lib.ps1 pure functions (no Pester in this repo). Run: pwsh scripts/tests/pr-lib.tests.ps1
# Exit 0 = all passed. CI runs it (ci.yml).
. "$PSScriptRoot/../pr-lib.ps1"
$ErrorActionPreference = 'Stop'
$script:fails = 0
function Check([string]$Name, $Actual, $Expected) {
  if ("$Actual" -ne "$Expected") { Write-Host "FAIL $Name`n  expected: $Expected`n  actual:   $Actual"; $script:fails++ } else { Write-Host "ok   $Name" }
}

function New-Raw([hashtable]$o = @{}) {
  $files = $o.files ?? @(@{ path = 'src/A.cs'; additions = 150 }, @{ path = 'tests/A.Tests/ATests.cs'; additions = 400 }, @{ path = 'docs/X.md'; additions = 50 })
  $threads = $o.threads ?? @()
  $reviews = $o.reviews ?? @()
  $req = $o.req ?? @()
  $checks = $o.checks ?? @(@{ __typename = 'CheckRun'; name = 'build'; status = 'COMPLETED'; conclusion = 'SUCCESS' })
  @{ data = @{ repository = @{ pullRequest = @{
          number = 7; title = 'feat: x'; state = ($o.state ?? 'OPEN'); isDraft = $false; headRefOid = 'abcdef1234567890'
          mergeStateStatus = ($o.merge ?? 'CLEAN'); mergeable = ($o.mergeable ?? 'MERGEABLE')
          files = @{ totalCount = $files.Count; nodes = $files }
          reviewThreads = @{ totalCount = $threads.Count; nodes = $threads }
          reviews = @{ nodes = $reviews }
          timelineItems = @{ nodes = $req }
          commits = @{ nodes = @(@{ commit = @{ statusCheckRollup = @{ contexts = @{ nodes = $checks } } } }) }
        } } } } | ConvertTo-Json -Depth 20 | ConvertFrom-Json
}
$copRev = @{ id = 'PRR_1'; state = 'COMMENTED'; body = 'overview'; submittedAt = '2026-11-12T10:00:00Z'; author = @{ login = 'copilot-pull-request-reviewer' }; commit = @{ oid = 'abcdef1234567890' } }
$copReq = @{ createdAt = '2026-11-12T09:00:00Z'; requestedReviewer = @{ login = 'copilot-pull-request-reviewer' } }
$thread = @{ id = 'PRRT_aaa111'; isResolved = $false; isOutdated = $false; path = 'src/A.cs'; line = 12; originalLine = 12
  comments = @{ nodes = @(@{ author = @{ login = 'copilot-pull-request-reviewer' }; body = ('x' * 400) }) } }

# code-line counting: docs and tests excluded
$d = ConvertTo-PrData (New-Raw)
Check 'code adds exclude tests/docs' $d.CodeAdds 150
Check 'copilot required over 100' (Test-CopilotRequired $d) $true
Check 'tests path is not code' (Test-CodePath 'tests/X/Y.cs') $false
Check 'md is not code' (Test-CodePath 'README.md') $false
Check 'script is code' (Test-CodePath 'scripts/pr-lib.ps1') $true
Check 'script tests are not code' (Test-CodePath 'scripts/tests/pr-lib.tests.ps1') $false

# verdicts
$v = Get-Verdict $d
Check 'required review missing blocks' $v.Ready $false
Check 'missing review message' ($v.Blockers -join '|' -match 'not requested') $true
$d = ConvertTo-PrData (New-Raw @{ reviews = @($copRev); req = @($copReq) })
Check 'review landed, CI green, no threads: ready' (Get-Verdict $d).Ready $true
Check 'pending false once review is after request' $d.CopilotPending $false
$d = ConvertTo-PrData (New-Raw @{ req = @($copReq) })
Check 'requested, no review: pending' $d.CopilotPending $true
Check 'pending blocks' (Get-Verdict $d).Ready $false
$d = ConvertTo-PrData (New-Raw @{ reviews = @($copRev); req = @($copReq); threads = @($thread) })
$v = Get-Verdict $d
Check 'unresolved thread blocks' $v.Ready $false
Check 'unresolved count' @(Get-Unresolved $d).Count 1
$d = ConvertTo-PrData (New-Raw @{ files = @(@{ path = 'docs/a.md'; additions = 900 }) })
Check 'docs-only needs no review' (Get-Verdict $d).Ready $true
$d = ConvertTo-PrData (New-Raw @{ files = @(@{ path = 'src/A.cs'; additions = 20 }) })
Check 'small change needs no review' (Get-Verdict $d).Ready $true
$d = ConvertTo-PrData (New-Raw @{ checks = @(@{ __typename = 'CheckRun'; name = 'build'; status = 'IN_PROGRESS'; conclusion = $null }); files = @(@{ path = 'src/A.cs'; additions = 5 }) })
Check 'running CI blocks' (Get-Verdict $d).Ready $false
Check 'ci pending' $d.Ci 'pending'
$d = ConvertTo-PrData (New-Raw @{ checks = @(@{ __typename = 'CheckRun'; name = 'build'; status = 'COMPLETED'; conclusion = 'FAILURE' }) })
Check 'ci fail' $d.Ci 'fail'
Check 'ci failed names' ($d.CiFailed -join ',') 'build'
$d = ConvertTo-PrData (New-Raw @{ checks = @() })
Check 'no checks' $d.Ci 'none'
$d = ConvertTo-PrData (New-Raw @{ merge = 'DIRTY'; mergeable = 'CONFLICTING'; files = @(@{ path = 'src/A.cs'; additions = 5 }) })
Check 'conflict confirmed blocks' (Get-Verdict $d { 'conflict' }).Ready $false
$v = Get-Verdict $d { 'clean' }
Check 'stale conflict notice is a warning only' $v.Ready $true
Check 'stale warning text' ($v.Warnings -join '|' -match 'stale') $true
Check 'unverifiable conflict blocks' (Get-Verdict $d { 'unknown' }).Ready $false
$d = ConvertTo-PrData (New-Raw @{ state = 'MERGED' })
Check 'merged PR is not ready' (Get-Verdict $d).Ready $false
$d = ConvertTo-PrData (New-Raw @{ merge = 'BLOCKED'; files = @(@{ path = 'src/A.cs'; additions = 5 }) })
Check 'BLOCKED with nothing else listed blocks' (Get-Verdict $d).Ready $false

# formatting
$d = ConvertTo-PrData (New-Raw @{ threads = @($thread) })
$f = Format-Thread $d.Threads[0]
Check 'thread line has id, location, author' ($f -match '^PRRT_aaa111 src/A.cs:12 @copilot-pull-request-reviewer') $true
Check 'thread body truncated' ($f.Length -lt 400) $true
Check 'Format-Text collapses whitespace' (Format-Text "a`n  b`t c") 'a b c'
Check 'Format-Text no truncation when short' (Format-Text 'abc' 5) 'abc'

# reply pairs
$p = @(ConvertFrom-ReplyPairs @('PRRT_a=Fixed in abc', 'PRRT_b=x=y is kept'))
Check 'pair id' $p[0].Id 'PRRT_a'
Check 'pair message' $p[0].Message 'Fixed in abc'
Check 'message keeps =' $p[1].Message 'x=y is kept'
$threw = $false; try { ConvertFrom-ReplyPairs @('novalue') | Out-Null } catch { $threw = $true }
Check 'bad pair throws' $threw $true
Check 'prefix resolves' (Resolve-ThreadId $d 'PRRT_aa').Id 'PRRT_aaa111'
$threw = $false; try { Resolve-ThreadId $d 'nope' | Out-Null } catch { $threw = $true }
Check 'unknown thread throws' $threw $true

# failed-test parsing and flake rule
$log = @(
  "build`tTest`t2026-11-12T01:00:00.1234567Z   failed TriggerServiceTests.Fires_once (12ms)",
  "build`tTest`t2026-11-12T01:00:00.1234567Z   failed TriggerServiceTests.Fires_once (12ms)",
  "build`tTest`t2026-11-12T01:00:01.0000000Z   [FAIL] Foo.BarTests.Baz",
  "build`tTest`t2026-11-12T01:00:02.0000000Z   passed Other (1ms)")
$t = Get-FailedTests $log
Check 'failed tests parsed and deduped' ($t -join ',') 'Foo.BarTests.Baz,TriggerServiceTests.Fires_once'
Check 'TriggerServiceTests is flaky' (Test-Flaky 'TriggerServiceTests.Fires_once') $true
Check 'other tests are not flaky' (Test-Flaky 'Foo.BarTests.Baz') $false

if ($script:fails) { Write-Host "$script:fails failed"; exit 1 } else { Write-Host 'all passed' }
