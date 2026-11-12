---
name: repo-workflow
description: How to start, carry out and finish any piece of work in the Daze Control repo — orienting from the Resume block and docs, branching, checkpoint commits, opening PRs, and the definition of done. Use this at the start of every coding, refactoring, bug-fix or docs task in this repo, whenever the user asks "what's next", "where are we", "continue", or to commit/push/open a PR, and before ending a task, even if the request looks small.
---

# Repo workflow

This repo is written entirely by AI agents across many sessions, often cut off by usage caps. No agent
remembers the previous one; the repo is the only memory. So every task starts by reading that memory and
ends by updating it — and in between, it checkpoints often (D-018).

## 1. Orient (before touching code)

0. `resume` skill: Resume block, `git status`, open PRs, build + tests. It tells you the next action.
1. `docs/PLAN.md` — the task you're on: its files and done-when. `docs/GOALS.md` — user intent and scope.
2. `docs/ARCHITECTURE.md` — the sections relevant to the task (components, invariants, open questions).
3. Grep `docs/DECISIONS.md` and `docs/DEADENDS.md` for the area you're touching (component, API, library).
   Also grep the OSD's (`C:\Projects\daze-media-osd\docs`) when porting its code.

**Usage is the scarce resource (D-079).** Every tool call re-reads the whole conversation, so the number of calls
is the cost. Batch: one `Grep` with `-C` context or one combined shell command instead of several single reads;
independent reads in parallel in one message; read the section you need (`Grep -n` then `Read` with
offset/limit), never a whole PLAN/UX/DECISIONS/ARCHITECTURE. Don't re-read what is already in the conversation.

If the user's request conflicts with a recorded decision, say so and ask — the user may have changed
their mind (then record a superseding decision) or may not remember the trade-off.

## 2. Work

- Branch from `main` (or the open PR you stack on): `feat/<m>-<topic>`, `fix/<topic>`, `docs/<topic>`,
  `chore/<topic>`, `spike/<topic>`. Spikes are throwaway; their *result* goes into the docs.
- One PR per task (PLAN.md). Build and test with the `build` skill. Keep the tree building at every commit.
- **Checkpoint after every green sub-step** (`resume` skill): commit + push + the PR's "## Resume" section.
- Adding a step type? Follow the checklist in `docs/steps-review/REVIEW.md` ("Adding a step"); `StepConventionTests` enforces its naming, group, order and id rules.
- Respect the hard rules in CLAUDE.md (fail-soft, timeouts, idle = zero, AOT-clean, no admin, no focus stealing).
- Ported code: copy from the OSD/scd and cite the source decision: `// Shell_NotifyIcon, dark menu (OSD D-009)`.
- Write a code comment only where the code can't explain itself — mostly interop quirks and Windows
  behaviour. Point to the decision instead of repeating it.

## 3. Record knowledge (as you go, not only at the end)

Use the `knowledge-log` skill whenever you: choose between real alternatives, reject an approach, hit a
dead end, learn a non-obvious Windows/AOT behaviour, change the architecture, or the user states a
preference or changes scope. Do it in the same commit as the code it explains.

## 4. Commit

Conventional Commits, imperative subject ≤ 72 chars. The body says **why**, not what (the diff shows what).
Reference decisions: `Refs: D-003`. `wip(<area>): …` is fine on branches. The PR title is a Conventional
Commit too (`feat(...)`, `fix(...)`, `docs(...)`): the release cutter reads it to pick the next version.

Never commit `bin/`, `obj/`, `artifacts/`, local settings, or anything with secrets.

Version: **PRs never touch `<Version>`** (D-079). The release tag is the version: the cutter picks the next number
from the PR titles since the last tag (any `feat` → minor, else patch); `<Version>` in `Directory.Build.props`
only labels local builds. To cut a release (only when the user asks): `release` skill.

## 5. Definition of done

- [ ] `build` skill: build + tests green; for engine/editor changes, the app was actually run and the
      change seen (or you say explicitly that it wasn't).
- [ ] Knowledge recorded (decisions / dead ends / architecture / open questions closed).
- [ ] `docs/PLAN.md` task ticked (its own line only; don't edit the Resume block, D-079); GOALS status current.
- [ ] If any instruction in `CLAUDE.md` or a skill turned out wrong or stale, fix it in this same change.
- [ ] Pushed and a PR opened with `gh pr create` (when a remote exists); merged per the merge policy
      below. Commit to `main` directly only if the user says so.

PR description: what changed, why (link D-xxx), how it was verified, what's left.

## 6. Merge policy (user, 2026-11-01: agents merge while the app is being scaffolded)

Agents merge their own PRs so work isn't blocked while the user sleeps. Everything about Copilot review, review
threads, CI wait/rerun, the ready check and the merge itself is in the **`pr-review` skill** (D-122): follow it, dont
hand-roll `gh` calls. In short: merge commits only via `scripts/pr-merge.ps1` (never `--auto` for children, `--squash`
or `--admin`); a Copilot review is required for PRs adding more than 100 lines of non-test code (max 2 per PR);
every thread is replied to and resolved first. To wait for a PR to land (a dependency, a release):
`pwsh scripts/wait-pr.ps1 60,61` with `run_in_background`. Never poll by hand, never ask the user "is it merged?".

Stacked PRs: with merge commits, a branch that already contains its base merges cleanly once the base lands;
just `git merge origin/main`. Never rebase or force-push.