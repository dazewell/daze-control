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

If the user's request conflicts with a recorded decision, say so and ask — the user may have changed
their mind (then record a superseding decision) or may not remember the trade-off.

## 2. Work

- Branch from `main` (or the open PR you stack on): `feat/<m>-<topic>`, `fix/<topic>`, `docs/<topic>`,
  `chore/<topic>`, `spike/<topic>`. Spikes are throwaway; their *result* goes into the docs.
- One PR per task (PLAN.md). Build and test with the `build` skill. Keep the tree building at every commit.
- **Checkpoint after every green sub-step** (`resume` skill): commit + Resume block + push.
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
Reference decisions: `Refs: D-003`. `wip(<area>): …` is fine on branches (squash-merge cleans it).

Never commit `bin/`, `obj/`, `artifacts/`, local settings, or anything with secrets.

Version: one `<Version>` in `Directory.Build.props`. A PR with a `feat` commit bumps the minor, a `fix`-only
PR the patch; docs/chore/spike PRs don't. Bump once per PR, against `main`'s number. (Releases arrive in
T10.1; until then the version only labels builds.)

## 5. Definition of done

- [ ] `build` skill: build + tests green; for engine/editor changes, the app was actually run and the
      change seen (or you say explicitly that it wasn't).
- [ ] Knowledge recorded (decisions / dead ends / architecture / open questions closed).
- [ ] `docs/PLAN.md` task ticked, Resume block points at the next task; GOALS status current.
- [ ] If any instruction in `CLAUDE.md` or a skill turned out wrong or stale, fix it in this same change.
- [ ] Pushed and a PR opened with `gh pr create` (when a remote exists); merged per the merge policy
      below. Commit to `main` directly only if the user says so.

PR description: what changed, why (link D-xxx), how it was verified, what's left.

## 6. Merge policy (user, 2026-11-01: agents merge while the app is being scaffolded)

Agents merge their own PRs so work isn't blocked while the user sleeps. Docs-only PRs (no code, tests or build
files changed; a spike's untracked probe counts as docs) get **no** Copilot review: merge once CI is green (user,
2026-11-04). Every other PR:
1. Copilot doesn't review on its own (user, 2026-11-01). Right after opening the PR (or marking it ready),
   request it, every time: `gh pr edit <n> --add-reviewer @copilot` (works: PR #4 review 2, 2026-11-01; fallback if it fails:
   `gh api -X POST repos/dazewell/daze-control/pulls/<n>/requested_reviewers -f "reviewers[]=copilot-pull-request-reviewer[bot]"`).
   Check it took: `gh api repos/dazewell/daze-control/issues/<n>/timeline --jq '.[] | select(.event=="review_requested") | .requested_reviewer.login'`
   prints `Copilot` (`reviewRequests` empties again once Copilot starts, so it can't confirm it). A review counts
   against the budget only once requested. Then wait for CI green and the review.
2. Read the overview (🟢 / 🟡 / 🔴) and every comment. **Critical** = the review calls it critical or 🔴, or it
   is a correctness, data-loss, security or fail-soft violation (your judgement counts too).
3. No Critical → fix cheap findings or reply why not, resolve the threads, squash-merge
   (`gh pr merge <n> --squash --delete-branch`), sync `main`, start the next task.
4. Critical → fix all findings, reply on each thread, push, request a **second** Copilot review (step 1's
   command). **Budget: 2 Copilot reviews per PR, never a third** (each costs the user).
5. Critical still open after the second review → fix them yourself, then review your own fixes (`code-review`
   skill at high effort on the PR diff) until you find nothing critical; note in the PR what the second
   review raised and how you verified the fixes; then merge.
6. Never merge with CI red, with a finding you think is critical but couldn't fix, or a PR that needs the
   user's decision (scope, UX, a decision that contradicts a D-xxx): stop, write it in the Resume block
   notes and the PR, and leave it for the user.
7. Never end a turn just to wait for CI or a Copilot review: run `gh pr checks <n> --watch` (or a polling
   loop for the review) in the background and act when it exits. Nothing wakes an ended turn overnight.

Stacked PRs: PRs are squash-merged, so once a base PR lands, the next branch conflicts with `main`
even though nothing new is in it. If `git diff <base PR's last head> origin/main` is empty and the
branch contains that head, merge `origin/main` and resolve every conflict to the branch's side
(`git checkout --ours`). Then check that `git write-tree` matches the pre-merge tree. Never rebase or
force-push.
