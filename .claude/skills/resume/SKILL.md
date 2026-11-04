---
name: resume
description: Pick up Daze Control work exactly where the last (possibly cut-off) agent session stopped, and stop cleanly before a usage cap or context limit. Use at the start of every session in this repo, whenever the user says "continue", "resume", "where are we", "what's next", after a session was interrupted, and whenever you are close to a usage cap or the context is getting long — even if the task looks fresh.
---

# Resume

Usage caps cut sessions often (D-018). Two hand-overs, so parallel tasks never conflict on one block (D-079):
- **PLAN.md's Resume block** (top of `docs/PLAN.md`): what's next overall and standing notes. Only the
  coordinator or a session working directly for the user rewrites it; task PRs leave it alone.
- **Your task's PR description**, section `## Resume` (format below): the live state of one task. Open a
  draft PR at your first checkpoint (`gh pr create --draft`) and rewrite that section at every checkpoint
  with `gh pr edit <n> --body-file <file>`.

## At session start

1. Read the **Resume block** (the SessionStart hook already printed it; else top of `docs/PLAN.md`).
2. `git status` and `git log --oneline -5`. Leftovers from a cut session:
   build + test them; green → commit as `wip(...)` and keep going; red → `git stash push -m "cut session <date>"`
   and note the stash in the PR's Resume section.
3. `gh pr list --state open` (when a remote exists). If your task has a PR, read its body's `## Resume` and its
   review comments (`gh pr view <n> --comments`) before new work. Open PRs' branches are the truth, not `main`.
4. Build + tests (`build` skill). Red before you started → fix that first, it's the real next action.
5. Continue at **Next action**. Read only GOALS, the task's files, and grep DECISIONS/DEADENDS for the area —
   don't re-read whole docs.

## Checkpoint (after every green sub-step, ≤ ~20 min of work)

1. Build + tests green (run only the affected test project mid-task; the full suite before the PR is ready).
2. Commit (`wip(<area>): …` allowed on branches), then `git push`.
3. Rewrite the PR's `## Resume` section (format below; open the draft PR first if there is none).
4. When the task is done, tick its own line in `docs/PLAN.md` (`[x]`, branch/PR after it).

Never hold uncommitted work across a long step. A cut session must lose at most one sub-step.

## Near the cap or context limit

Stop at the next green point. Commit and push. Write the PR's Resume section with the **exact** next action
(test name, file, method) and any half-formed reasoning the next agent would otherwise have to rediscover.
Better a small clean stop than a big dirty one.

## Usage guard (5-hour limit, user 2026-11-01, changed 2026-11-02)

The user wants work to pause at **97 %** of the 5-hour limit and continue after the reset.

- Read the limit with `mcp__ccd_session_mgmt__get_usage` (load it with ToolSearch if deferred): the
  `plan.windows` entry labelled "5-hour limit" has `percentUsed` and `resetsAt` (UTC).
- Check it at session start, after every checkpoint commit, and before starting any sub-step. At ≥ 90 %
  keep sub-steps small (one test + its code) so the next check comes soon.
- At **≥ 97 %**: do the "Near the cap" stop above (commit, push, PR Resume section). Then end the turn with one
  line: work is paused, plus the exact `resetsAt` (UTC). Start no new work.
- **Never create or re-arm scheduled tasks** (`daze-control-resume` stays disabled). Scheduled runs start
  with default permissions and stalled on their first prompt twice (2026-11-01/02). The session that
  started you reads your note and wakes you after the reset; if nobody does, the user restarts by hand.
- `get_usage` reports `unavailable` / `not_applicable` → keep the normal checkpoint rhythm.

## Resume section format (PR description; PLAN.md's block uses the same lines, shorter)

```
## Resume
Current task: T2.2 — failure policies (branch feat/m2-runner, stacked on #6)
Last green checkpoint: 3f2a1c9 "wip(runtime): retries with TimeProvider"
Next action: FlowRunnerTests.IgnoredFailureRunsDependents, then implement in Scheduler.Complete
Tree state: builds, 41/41 tests green · Uncommitted: none
Notes for next agent: skip propagation must ignore `always` edges (see D-003)
```

- "Last green checkpoint" is the commit you just pushed.
- "Next action" is one concrete step, not a task title.
- Notes: only what changes the next agent's next hour (blocked on the user, a trap you just found). Durable
  knowledge goes to DECISIONS/DEADENDS/ARCHITECTURE (`knowledge-log` skill), not here.
- PLAN.md's block stays under ~15 lines: no history of finished tasks (the ticks and git have it).
