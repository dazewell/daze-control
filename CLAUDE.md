# Daze Control

Windows 11 tray engine that runs the user's flows (Bluetooth, audio, displays, Android devices, programs…) from
hotkeys, the tray menu or the `dctl` CLI, plus an on-demand visual flow editor. Successor to `scd`.
C# / .NET 10: Native AOT headless engine, WinUI 3 editor (D-001). Written entirely by AI agents.

## Start here: resume

Usage caps cut sessions often. **Always start with the `resume` skill**: it reads the Resume block at the
top of [docs/PLAN.md](docs/PLAN.md) and your task's PR, and tells you exactly where the last agent stopped.
Checkpoint rule: after every green sub-step build + test → commit → push → rewrite the PR's `## Resume` section.
Usage is scarce and every tool call re-reads the whole conversation: batch lookups, read sections not whole
docs, never wait on CI (auto-merge does); to wait for a merge run `scripts/wait-pr.ps1 <n>` in the background (D-079).

## The repo is the memory

No agent remembers the last session. Read before working, write before finishing:

- [docs/PLAN.md](docs/PLAN.md) — Resume block, workflow, milestones/tasks with status. Read first, always.
- [docs/GOALS.md](docs/GOALS.md) — user intent, scope, non-goals.
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — how it's built now, invariants, footprint, open questions.
- [docs/DECISIONS.md](docs/DECISIONS.md) — why / why not (D-xxx). Grep before re-deciding anything.
- [docs/DEADENDS.md](docs/DEADENDS.md) — what was tried and failed (X-xxx). Grep before trying anything clever.
- [docs/FEATURES.md](docs/FEATURES.md) — what the product does. [docs/UX.md](docs/UX.md) — design spec + acceptance checklist.

Keep these lean: every line must help the next agent decide better. Rules in the `knowledge-log` skill.
Don't re-read whole docs: GOALS + the task's files + grep DECISIONS/DEADENDS for the area.

## Skills

- `resume` — session start and near-cap stop. Use first in every session.
- `repo-workflow` — orient → branch → work → record → commit → definition of done. Use for every task.
- `build` — prerequisites, build/run/test/publish/measure commands, troubleshooting.
- `pr-review` — Copilot review, review threads, CI wait/rerun, ready check and merge via `scripts/pr-*.ps1` (D-122). Use for every PR.
- `release` — cut a release with `scripts/cut-release.ps1`; only when the user asks.
- `daze-coordinator` — run a coordinator session: route tasks to child sessions, track PRs, cut releases on request; use only in coordinator sessions.
- `knowledge-log` — what goes into which doc, entry formats, brevity rules.

If a skill or this file is wrong, fix it in the same change that found the problem.

## Hard rules

- Fail-soft: a bad flow / library item / provider init disables only itself; the engine always starts and
  keeps the last good version of each flow on a bad reload (D-021). No unobserved task (the runner awaits
  all; global handlers log).
- Every step has a timeout and honours cancellation; processes are killed (tree) on timeout.
- Idle = zero: no timers, no polling, no resident COM objects; waits are event-driven with a timeout; COM
  workers start lazily and stop after 60 s idle (D-009).
- Budgets (`scripts/measure.ps1`, gates in M0, M3, M10): engine idle ≤ 15 MB private / ≤ 35 MB working set,
  0 ms CPU over 10 idle minutes, back within +2 MB after a run. Editor closed = not running.
- Engine stays Native AOT with zero AOT/trim warnings (warnings are errors). No reflection-based serializers.
- No admin (D-008), no code injection, no focus stealing (the pop-up never activates; the editor activates
  only when opened).
- Secrets never in YAML or logs (D-014).
- User-facing text never says "node", "DAG" or "OSD" (UX.md).
- Don't contradict a recorded decision silently — follow it, or ask the user and record a superseding one.
