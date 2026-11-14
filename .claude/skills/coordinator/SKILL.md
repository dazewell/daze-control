---
name: coordinator
description: Coordinate explicitly requested parallel work in this repository, route tasks to workers, track PRs, and publish releases when requested. Use when the user asks for a coordinator or delegated work; ordinary coding requests use repo-workflow.
---

# Coordinator

Coordinate the user's requested work and preserve its scope. Read `resume` and `repo-workflow` first.
This skill does not require a particular agent app or model provider.

## Route work

1. Use the user's message as the brief. Resolve routine choices; ask only when missing information changes the result.
2. Discover the current environment's delegation tools before using them. For subtasks in the current chat,
   use subagents when authorized. Create a separate user-owned chat only when the user explicitly asks for one.
   If delegation is unavailable, do the work in the current chat.
3. Give each worker the problem, relevant files, done criteria, validation, and the standing rules below.
   Use isolated worktrees for workers that edit files. Inherit the current model unless the user requests otherwise.
4. Route follow-ups to the worker already doing that task. Batch small related fixes; keep independent features in separate PRs.
5. Wait through the environment's supported worker/terminal wait mechanism. Do not end the task merely because work was dispatched.

## Standing rules for workers

- Follow `CLAUDE.md`, `repo-workflow`, `build`, and `pr-review`; own branch, green relevant checks, no version bump.
- Merge only through `scripts/pr-merge.ps1`; never `--auto`, `--admin`, or `--squash`.
- Merge `origin/main` on conflicts; keep both tasks' changes and renumber colliding decision ids.
- Keep live state in the PR's `## Resume`. Report the PR, commit, checks, and anything not verified live.
- No live audio run (D-091); keep accessible names and avoid layout shifts.
- Restore any authorized audio, Bluetooth, or display state changes; no admin rights.
- Never push a release tag as part of an implementation task.

## Verify completion

A worker's report is not proof. Check `pwsh scripts/pr-status.ps1 <n>` and relevant evidence before merging
with `pr-merge.ps1`. Say what changed, whether it merged, what remains unverified, and whether it shipped.
PR monitoring is optional when the current environment does not expose it; do not claim it is enabled.

## Releases and limits

- Use `release` only when the user asks for publication in the active conversation. Wait for the script's final result.
- Use `resume` for usage checks and checkpoints. If usage tools are absent, keep the normal checkpoint rhythm.
- Do not create timers or scheduled tasks to watch workers, wait for CI, or resume after usage resets.
- Respect the current permissions. If an action is rejected, report its actual reason and use the available approval path;
  do not ask the user to disable safety checks or switch to an unrestricted mode.
- Change archiving, monitoring, and other app settings only within the user's authorized scope.
