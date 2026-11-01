# SessionStart hook: prints the Resume block of docs/PLAN.md so every session starts where the last one stopped (D-018).
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$plan = Join-Path $PSScriptRoot '..\..\docs\PLAN.md'
if (-not (Test-Path $plan)) { exit 0 }
$lines = Get-Content $plan -Encoding utf8
$start = [Array]::IndexOf($lines, '## Resume')
if ($start -lt 0) { exit 0 }
$block = for ($i = $start; $i -lt $lines.Count; $i++) {
    if ($i -gt $start -and ($lines[$i] -match '^(## |---)')) { break }
    $lines[$i]
}
"Daze Control — use the resume skill first. Resume block from docs/PLAN.md:"
$block
