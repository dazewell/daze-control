---
name: resume
description: Pick up Daze Control work exactly where the last (possibly cut-off) agent session stopped, and stop cleanly before a usage cap or context limit. Use at the start of every session in this repo, whenever the user says "continue", "resume", "where are we", "what's next", after a session was interrupted, and whenever you are close to a usage cap or the context is getting long — even if the task looks fresh.
---

# Resume

Usage caps cut sessions often (D-018). The Resume block at the top of `docs/PLAN.md` is the hand-over
between agents; it is only useful if every agent reads it first and rewrites it at every checkpoint.

## At session start

1. Read the **Resume block** (the SessionStart hook already printed it; else top of `docs/PLAN.md`).
2. `git status` and `git log --oneline -5`. Leftovers from a cut session:
   build + test them; green → commit as `wip(...)` and keep going; red → `git stash push -m "cut session <date>"`
   and note the stash in the Resume block.
3. `gh pr list --state open` (when a remote exists). If the Resume block names a PR, check its review
   comments (`gh pr view <n> --comments`) before new work. Open PRs' branches are the truth, not `main`.
4. Build + tests (`build` skill). Red before you started → fix that first, it's the real next action.
5. Continue at **Next action**. Read only GOALS, the task's files, and grep DECISIONS/DEADENDS for the area —
   don't re-read whole docs.

## Checkpoint (after every green sub-step, ≤ ~20 min of work)

1. Build + tests green.
2. Rewrite the Resume block (format below) — in the same commit as the work.
3. Tick the task in `docs/PLAN.md` (`[~]` in progress, `[x]` done; add branch/PR after the task).
4. Commit (`wip(<area>): …` allowed on branches), then `git push` when a remote exists.

Never hold uncommitted work across a long step. A cut session must lose at most one sub-step.

## Near the cap or context limit

Stop at the next green point. Commit. Write the Resume block with the **exact** next action (test name,
file, method) and any half-formed reasoning the next agent would otherwise have to rediscover. Push.
Better a small clean stop than a big dirty one.

## Usage guard (5-hour limit, user 2026-11-01)

The user wants work to pause at **97 %** of the 5-hour limit and continue by itself after the reset.

- Read the limit with `mcp__ccd_session_mgmt__get_usage` (load it with ToolSearch if deferred): the
  `plan.windows` entry labelled "5-hour limit" has `percentUsed` and `resetsAt` (UTC).
- Check it at session start, after every checkpoint commit, and before starting any sub-step. At ≥ 90 %
  keep sub-steps small (one test + its code) so the next check comes soon.
- At **≥ 97 %**: do the "Near the cap" stop below (commit, Resume block, push). Then create **one**
  scheduled task with `mcp__scheduled-tasks__create_scheduled_task`:
  - `taskId` `daze-control-resume` (if taken, list scheduled tasks: an enabled one already pending → don't
    add another; a finished one → use the free id the error names)
  - `fireAt` = `resetsAt` + 5 minutes, as ISO 8601 with offset (e.g. `2026-11-01T09:05:00Z`)
  - `prompt`: "Continue Daze Control work. Work in `C:\Projects\scd-rewamp` (cd there first). Use the
    `resume` skill, then `repo-workflow`, and carry on from the Resume block in `docs/PLAN.md` with its
    checkpoint rule and this usage guard. Use Opus at medium effort. Open PRs as usual; never merge them.
    The user merges."
  Then say in one line that work is paused and when it continues, and end the turn. Start no new work.
- `get_usage` reports `unavailable` / `not_applicable` → keep the normal checkpoint rhythm and don't
  schedule anything.

## Resume block format

```
## Resume
Current task: T2.2 — failure policies (branch feat/m2-runner, PR #7, stacked on #6)
Last green checkpoint: 3f2a1c9 "wip(runtime): retries with TimeProvider"
Next action: FlowRunnerTests.IgnoredFailureRunsDependents, then implement in Scheduler.Complete
Tree state: builds, 41/41 tests green · Uncommitted: none
Notes for next agent: skip propagation must ignore `always` edges (see D-003)
```

- "Last green checkpoint" is the commit *before* the one you are writing (a commit can't name itself).
- "Next action" is one concrete step, not a task title.
- Notes: only what changes the next agent's next hour (blocked on the user, a trap you just found). Durable
  knowledge goes to DECISIONS/DEADENDS/ARCHITECTURE (`knowledge-log` skill), not here.
