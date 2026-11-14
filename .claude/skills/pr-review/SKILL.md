---
name: pr-review
description: Handle a PR's Copilot review, review threads, CI wait/rerun, readiness check and merge with the deterministic scripts/pr-*.ps1 scripts (cheap, short output). Use whenever you open, review-wait, fix review comments on, check, or merge a PR here, or when a brief says "follow the pr-review skill". Never hand-roll gh/graphql for these.
---

# PR review and merge (D-122)

Every script takes the PR number, works from any worktree, prints one short line per fact, and has `-DryRun` where it writes. Run from the repo root: `pwsh scripts/<name>.ps1 <pr> ...`.

## Standing rules (children follow these; briefs just say "follow the pr-review skill")

- **Never `gh pr merge --auto`** (it fires before Copilot threads are answered), `--squash` or `--admin`. Merge only through `pr-merge.ps1` (merge commit).
- **Copilot review is required** when the PR adds more than 100 lines of non-test code (docs and tests excluded). Not for docs-only or small fixes whose tests cover the change. Max **2 reviews per PR** (the script refuses a third; Critical findings = 🔴, or correctness, data loss, security, fail-soft). If Critical is still open after the second, fix it yourself and run the `code-review` skill at high effort on the diff; note it in the PR.
- **Wait for the review before merging, reply to AND resolve every thread, verify `unresolved: 0`.**
- Never wait on CI or the review with your own loop or model turns: `pr-wait.ps1` in the background.
- If a finding needs the user's decision (scope, UX, contradicts a D-xxx): stop, write it in the PR `## Resume`, leave it.

## Flow

1. `gh pr create`. If this environment exposes PR monitoring tools, discover their schema and enable Auto-fix/address-comments for this PR. If unavailable or refused, record that in `## Resume` and handle checks and comments through the scripts below; do not call tools from another agent app by name.
2. Review needed: `scripts/pr-request-review.ps1 <pr>` (idempotent), then `scripts/pr-wait.ps1 <pr>` in a background process or persistent terminal session (blocks until the review landed and CI finished; prints review state and CI; exit 0 ok, 1 CI failed, 2 timeout, 3 closed/not requested). Keep waiting on that process through completion. For a small fix, use the same waiter without requesting a review; it still waits if a review was requested separately. Use `-NoReview` only when no review is pending.
3. `scripts/pr-threads.ps1 <pr>` lists unresolved threads (`id file:line @author body≤300`) plus review bodies; `-Full <id>` shows one in full, `-All` includes resolved.
4. Fix, push, then `scripts/pr-reply.ps1 <pr> "<id>=<reply>" "<id>=<reply>"` (ids may be unique prefixes; replies the thread, resolves it, re-queries and prints `unresolved: N`). Same message for all: `-All "Fixed in <sha>"`.
5. `scripts/pr-status.ps1 <pr>` (one block ending READY or the exact blockers; `-Ready` prints only that) then `scripts/pr-merge.ps1 <pr>` (ready check + `gh pr merge --merge`, prints the merge commit; `-DryRun` to preview). If merge is blocked by the permission check, say so in your report; the coordinator merges.
6. CI red: `scripts/pr-rerun.ps1 <pr>` prints the failed test names and reruns only known flakes (`TriggerServiceTests`, DEADENDS X-012); a real failing test is printed and not rerun: fix it. To wait for someone else's merge: `scripts/wait-pr.ps1 <n>` in the background.

GitHub's mergeable/conflict notice can be stale: `pr-status`/`pr-merge` re-check a reported conflict with `git merge-tree` on the exact head SHA and warn when it was stale. If main moved: `git merge origin/main`, resolve, push (never rebase or force-push).

## Coordinators

`pr-status.ps1 <pr>` per child PR instead of `gh pr view` JSON. To let children merge without the auto-mode classifier blocking `gh pr merge`, the user can allow-list the single command `pwsh scripts/pr-merge.ps1` (done in the checked-in `.claude/settings.json`: `permissions.allow` has `PowerShell(pwsh scripts/pr-merge.ps1:*)` and the Bash form).

Tests for the pure logic: `pwsh scripts/tests/pr-lib.tests.ps1` (runs in CI).
