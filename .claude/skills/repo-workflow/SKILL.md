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
only labels local builds.

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

Agents merge their own PRs so work isn't blocked while the user sleeps. Merge with **merge commits** and let
GitHub do it (D-079): `gh pr merge <n> --auto --merge`. A ruleset requires the `build` check on main, so the PR
merges itself when CI is green; arm it and end the turn, don't wait for CI. Never `--squash`, never `--admin`.
If main moved and the PR conflicts, `git merge origin/main`, resolve, push (auto-merge stays armed).

Copilot review (user, 2026-11-01, trimmed 2026-11-04):
- **No review:** docs-only PRs (no code, tests or build files changed; a spike's untracked probe counts as docs),
  and `fix` PRs under ~100 changed lines whose new or changed tests cover the change. Arm auto-merge right away.
- **Every other PR:** one review, a second only after Critical findings (step 4).

With a review:
1. Copilot doesn't review on its own. Right after opening the PR (or marking it ready),
   request it: `gh pr edit <n> --add-reviewer @copilot` (works: PR #4 review 2, 2026-11-01; fallback if it fails:
   `gh api -X POST repos/dazewell/daze-control/pulls/<n>/requested_reviewers -f "reviewers[]=copilot-pull-request-reviewer[bot]"`).
   Check it took: `gh api repos/dazewell/daze-control/issues/<n>/timeline --jq '.[] | select(.event=="review_requested") | .requested_reviewer.login'`
   prints `Copilot` (`reviewRequests` empties again once Copilot starts, so it can't confirm it). A review counts
   against the budget only once requested. Then wait for the review (one background poll that exits when it
   lands; CI runs meanwhile and auto-merge waits for it).
2. Read the overview (🟢 / 🟡 / 🔴) and every comment. **Critical** = the review calls it critical or 🔴, or it
   is a correctness, data-loss, security or fail-soft violation (your judgement counts too).
3. No Critical → fix cheap findings or reply why not, resolve the threads, push, arm auto-merge
   (`gh pr merge <n> --auto --merge`) and end the turn.
4. Critical → fix all findings, reply on each thread, push, request a **second** Copilot review (step 1's
   command). **Budget: 2 Copilot reviews per PR, never a third** (each costs the user).
5. Critical still open after the second review → fix them yourself, then review your own fixes (`code-review`
   skill at high effort on the PR diff) until you find nothing critical; note in the PR what the second
   review raised and how you verified the fixes; then merge.
6. Never arm auto-merge with a finding you think is critical but couldn't fix, or on a PR that needs the
   user's decision (scope, UX, a decision that contradicts a D-xxx): stop, write it in the PR's Resume
   section, and leave it for the user.
7. Waiting: one background poll per thing you actually need (the review), never a loop of short checks, and
   never wait for CI (auto-merge does). If CI fails, the app's Auto-fix or the coordinator wakes you.
   To know when PRs landed (a dependency, a release), run `pwsh scripts/wait-pr.ps1 60,61` with
   `run_in_background`: it exits when they merge (1 if closed unmerged or after 120 min) and re-invokes you, with
   no model calls while it waits. Never poll `gh pr view` by hand, never ask the user "is it merged?".

Stacked PRs: with merge commits, a branch that already contains its base merges cleanly once the base lands;
just `git merge origin/main`. Never rebase or force-push.
