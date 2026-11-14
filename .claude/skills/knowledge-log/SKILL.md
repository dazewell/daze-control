---
name: knowledge-log
description: Records the why behind this repo's code — decisions with rejected alternatives, dead ends, user intent/scope changes, and architecture updates — in docs/ with strict brevity rules. Use whenever a choice between real alternatives is made, an approach is rejected or fails, a non-obvious Windows/WinUI/AOT behaviour is discovered, the architecture changes, an open question gets answered, or the user states a preference or changes goals. Also use when finishing a task, and when asked "why is it done this way" or "did we try X".
---

# Knowledge log

The reader is the next AI agent, starting cold. It needs to know **what the user wants, why the code
looks this way, why not the obvious alternative, and what already failed** — nothing else. Every line
costs that agent context, so each line must change what it would do. Test for every bullet: *would a
fresh agent make a worse decision without it?* If not, cut it.

## Where things go

| Knowledge | File | Style |
|---|---|---|
| What the user wants, scope, non-goals, progress, next step | `docs/GOALS.md` | Current state; edit in place |
| How the system is built now: components, data flow, threading, invariants, open questions | `docs/ARCHITECTURE.md` | Current state; rewrite sections, no history |
| A choice + reasons + rejected alternatives | `docs/DECISIONS.md` | Append-only log |
| Something tried that failed | `docs/DEADENDS.md` | Append-only log |
| Bulky research behind a decision (tables, sources) | `docs/research/<yyyy-mm>-<topic>.md` | Snapshot, never maintained |
| A large feature designed before it's built (D-053) | `docs/<feature>/DESIGN.md` | Spec of what's still open; each task moves what it built into ARCHITECTURE/UX/FEATURES and cuts it there. Decisions, tasks and strings stay in the shared files |
| A quirk tied to one line of code | Code comment, pointing to D-/X- id | One line |

Rule of thumb: ARCHITECTURE and GOALS answer "what is", DECISIONS and DEADENDS answer "why / why not".
When the architecture changes, rewrite ARCHITECTURE to the new truth and put the reason in a decision —
don't leave "previously we…" prose in ARCHITECTURE.

## Decision entry

Next free number, newest at the bottom. Keep it under ~10 lines.
Parallel agents: run `git fetch` and take the next free D-number (max on origin/main + 1) right before committing, and re-check at merge time; duplicates have happened (D-132, D-134).

```
## D-007 · 2026-11-03 · <decision as a short statement>
Status: accepted
- Decision: <what, concretely — names of APIs/libs/files>
- Why: <the forces that made this the right call>
- Why not: <each serious alternative — one clause on why it lost>
- Revisit if: <observable trigger that would flip the decision>
- Ref: <optional: research file, PR, issue, external link>
```

Changing a decision: add a new entry with `Supersedes: D-003`, and change only the old entry's status line
to `Status: superseded by D-007`. Old reasoning stays — it explains why the old code existed.

Not a decision: following an obvious convention, picking a variable name, anything with no real
alternative. Those need no entry.

## Dead-end entry

Only for things actually tried. An option rejected on paper goes into a decision's "Why not".

```
## X-003 · 2026-11-05 · <what was tried>
- Tried: <approach, enough to recognise it if someone is about to repeat it>
- Failed because: <root cause, not symptoms>
- Evidence: <error message, Windows build, commit/branch>
- Retry only if: <condition, or "never">
```

## User intent

When the user states a preference, priority or scope change, update `docs/GOALS.md` in their terms
(paraphrase tightly; quote if the wording matters). If it overturns something, also add a decision.
Intent is the most expensive thing to lose — code can be re-derived, what the user wanted can't.

## Open questions

Unknowns live in ARCHITECTURE → "Open questions" as `Q<n>`. When answered: delete the question, write
the answer into the relevant section, and add a decision if alternatives were weighed.

## Brevity rules

- Bullets, not paragraphs. One fact per bullet. No narration of the process ("first I tried…, then…").
- Name the concrete thing: API, class, window class, package + version, Windows build.
- Link to code/commits instead of pasting code. No logs longer than the one telling line.
- Dates as absolute `yyyy-mm-dd`.
- If a file grows past ~300 lines, consolidate: merge superseded chains into a one-line summary entry
  that points to git history, and confirm with the user before pruning.
