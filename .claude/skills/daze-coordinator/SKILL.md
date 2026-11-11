---
name: daze-coordinator
description: Run a Daze Control coordinator session - take the user's requests, hand each task to its own child session, track PRs, cut releases on request, and report briefly. Use when the user opens a session to "coordinate", asks for several changes in a row, or says "let's work through the list" in the Daze Control repo.
---

# Daze Control coordinator

The coordinator does not write product code. It routes work, watches PRs, cuts releases, and answers the user in a few lines. Every token it spends on code is a token a child session would have spent anyway, with less context.

## Per request

1. **Read the user's message as the brief.** Don't ask questions for routine choices; pick the sensible default and say it in one line. Ask only when the answer changes what gets built.
2. **One fresh session per task**, each in its own worktree: `mcp__ccd_session__start_session` with `initiation: "user_asked"`, `context: "fresh"`, `use_worktree: true`, `wake_when_done: true`. (Load the tool via ToolSearch first. It is refused on automated turns: then use `own_initiative`, which shows a card, and say so.)
3. **Model policy**: Sonnet 5.5 medium for small fixes and tooling; Opus 5.5 medium for features and spikes; Haiku 5.5 low for lookups; Fable 5.1 only when Opus is stuck.
   `start_session` silently ignores a `model` pricier than the coordinator's own (a Sonnet coordinator's Opus child starts as Sonnet). So for any child that needs a pricier model than yours, do not start it: prepare a card (`initiation: "own_initiative"`, full brief) and tell the user in one line to check the model on the card before starting.
4. **Write the brief so the child needs nothing else**: the problem in the user's words, files/area to grep, what done looks like, what to verify, and the standing rules below. Put the *why* in `background` (at least 80 chars).
5. **Say one line** to the user right before starting ("Started X, model, what it will do") and end the turn. Don't wait, don't poll.
6. **Follow-ups on a running task** go to the same child with `SendMessage` (`to` = its `local_...` id), never a new session. A message to an archived session fails: start a new one.
7. **Batch small fixes** (usage is scarce, D-079): group tiny changes into one session instead of one each, unless they touch different areas the user may want released separately.

## Standing rules to put in every brief

- Own branch, build + tests green, follow CLAUDE.md and the `repo-workflow` skill.
- D-079: the tag is the version; PRs never bump `<Version>`. Merge commits only: `gh pr merge <n> --auto --merge`; never `--admin` or `--squash`.
- Copilot review: per `repo-workflow` (none for docs-only and small fixes); **read it before arming auto-merge**.
- On merge conflicts, merge origin/main keeping both sides (renumber colliding D-xxx). Enable Auto-fix with `mcp__ccd_pr__set_monitor`; if the app refuses, don't retry, say so in the PR `## Resume`.
- Live state lives in the PR's `## Resume`. Child sessions are archived on PR close (auto-archive is on).
- No Narrator/screen-reader work, no live audio run (D-091). Keep accessible names.
- No UI jumping or layout shifts for any reason; no empty reserved gaps.
- Daily PC: restore any audio, Bluetooth or display state changed; no admin rights.
- Never push a `v*` tag.
- Say honestly what was not verified live (no real phone, no UIA screenshots, no text-scale pass).

## When a child reports back

The note is a report, not proof. Check what matters with one command (`gh pr view <n> --json state,body,autoMergeRequest`), don't re-read the transcript. Then tell the user in 3-6 lines:
- the PR link, merged or auto-merge armed, whether Auto-fix is on (if refused, say you will watch it yourself);
- what changed in user terms;
- what was not verified live;
- whether it is in a release yet.

If the child did something different from what the user asked, say so plainly and offer the choice.

## Releases (only when the user asks, in chat, that turn)

Use the `release` skill and `scripts/cut-release.ps1`: it waits for the PRs, for CI green on main's head, tags that SHA, waits for the Release run, sets notes, checks assets. Write the notes file first: a short bullet list in user terms (what the user sees; no internals, no PR numbers). Report: version, CI/release status, the bullets, the link, and what was not verified. If the script breaks, patch a temp copy to finish the release, then send the fix to a session as a small PR.

## Guards

- Pause cleanly at 97 % of the 5-hour or weekly limit (`mcp__ccd_session_mgmt__get_usage`; check before starting large work). After a reset, wake paused sessions with `SendMessage`.
- Don't create scheduled tasks or crons for waiting. Use a background `pwsh -File wait-pr.ps1 <n>` (run_in_background) and end the turn.
- Don't start anything the user did not ask for. Parked ideas stay parked; offer them in one line at the end of a report.
- Auto-archive and other app settings: only change when the user asks.
- Keep replies short: what happened, what is unverified, what is next. No recaps of earlier turns.
