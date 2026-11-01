# Plan

## Resume
Current task: T0.2 — seed docs (branch main, no remote yet)
Last green checkpoint: 52c0154 "chore: init repo with .gitignore and .gitattributes"
Next action: commit docs, then T0.3 skills + SessionStart hook
Tree state: no code yet · Uncommitted: docs/ (this commit)
Notes for next agent: `gh repo create` waits on the user (name, private?) — ask before creating it.

---

## Workflow (usage-cap-proof, D-018)

- This file = the task list with status `[ ]` / `[~]` / `[x]`, branch and PR per task. Its top is the
  **Resume block**, rewritten in every checkpoint commit. Format:
  ```
  ## Resume
  Current task: T2.2 — failure policies (branch feat/m2-runner, PR #7, stacked on #6)
  Last green checkpoint: 3f2a1c9 "wip(runtime): retries with TimeProvider"
  Next action: FlowRunnerTests.IgnoredFailureRunsDependents, then implement in Scheduler.Complete
  Tree state: builds, 41/41 tests green · Uncommitted: none
  Notes for next agent: skip propagation must ignore `always` edges (see D-003)
  ```
- **Checkpoint rule:** after every green sub-step (≤ ~20 min of work): build + tests → commit (`wip(...)`
  allowed on branches; squash-merge cleans it) → update Resume block in the same commit → `git push`. Never
  hold uncommitted work across a long step. A cut session loses at most one sub-step.
- **Session start (`resume` skill, also wired as a SessionStart hook that prints the Resume block):**
  read Resume → `git status` (leftovers from a cut session: build them; keep if green, else `git stash` and
  note it) → `gh pr list --state open` → build + tests → continue at "Next action". Don't re-read whole docs:
  GOALS + the task's files + grep DECISIONS/DEADENDS for the area.
- **Near the cap / context limit:** stop at the next green point, commit, write the Resume block with the exact
  next action and any half-formed reasoning, push. Better a small clean stop than a big dirty one.
- **Task sizing:** every task lands a green, mergeable increment in one session (~≤ 1 h agent time), names
  its files and its done-when (test names / commands / what to see on screen). Tasks marked _(mechanical)_
  can run on a cheaper model. Broad reading goes to subagents to keep the main context small.
- **Definition of done per task:** build + tests green · knowledge logged (D-/X-) · PLAN.md status + Resume
  updated · GOALS status current · pushed + PR (user merges; stacked-PR rule in `repo-workflow`).

## Verification (every task)

- `dotnet build` + `dotnet test --solution` green (CI the same).
- Runtime semantics: unit tests over fake providers + `ManualTime` (parallel branches, joins, `when` edges,
  policies, retries, timeouts, locks, `whenRunning`, hot-reload last-good).
- Engine: `scripts/measure.ps1` budgets (idle private/working set, 10-min CPU delta, post-run delta);
  `dctl status/run/devices`; tray + hotkey on the real desktop; kill/restart leaves nothing behind.
- Hardware: the user's Connect Technics flow (Alt+Z) end to end; audio switch seen in the OSD app; display
  presets reversible (always test with a revert flow ready).
- Editor: run it, screenshot each page, light/dark, 175 % scale; canvas drag/connect/group via UIA +
  input injection; UX.md acceptance checklist.

---

## M0 Bootstrap
- [~] T0.1 `git init`, `.gitignore`; ask the user before `gh repo create` (name, private?).
- [~] T0.2 Docs copied from the plan: CLAUDE.md, GOALS, FEATURES, ARCHITECTURE, DECISIONS D-001..D-021,
  DEADENDS, UX, PLAN (this file).
- [ ] T0.3 Skills: `repo-workflow`, `knowledge-log`, `build` (adapted from the OSD), new `resume`;
  `.claude/settings.json` SessionStart hook printing the Resume block.
- [ ] T0.4 Solution skeleton: all projects compile, one passing test per test project, `ci.yml` (build + test).
- [ ] T0.5 Spike (gate): AOT engine with tray icon + one hotkey + YamlDotNet static context load; no trim/AOT
  warnings; `scripts/measure.ps1` → idle numbers in ARCHITECTURE. Over budget → decide with the user.

## M1 Core model + YAML
- [ ] T1.1 Model records + outcomes.
- [ ] T1.2 YAML DTOs/mapper, round-trip tests, fixtures incl. the user's flow.
- [ ] T1.3 Validation (cycles, refs, card rules, params vs descriptors; problem records with node paths).
- [ ] T1.4 Provider catalog descriptors + `ParamSpec`.
- [ ] T1.5 _(mechanical)_ Hand-convert the user's scd.toml into `samples/user-config.yaml` (Technics flow +
  both Bluetooth devices) — the one-time "import".

## M2 Runtime (pure, fake providers + `ManualTime`)
- [ ] T2.1 Scheduler: ready set, all-inputs join, edge `when`, skip propagation, cards.
- [ ] T2.2 Failure policies, retries, timeouts, cancellation.
- [ ] T2.3 Resource locks.
- [ ] T2.4 `whenRunning`.
- [ ] T2.5 Run events + history (last 100) + `history.jsonl`.
- [ ] T2.6 ConfigHost: load, debounced reload, last valid version per flow (D-021), never-valid flows disabled.

## M3 Engine shell
- [ ] T3.1 Composition root, single instance mutex, Log, global exception handlers.
- [ ] T3.2 TrayIcon from flows (folders, separators, running mark, Pause hotkeys, Open editor, Exit).
- [ ] T3.3 GlobalHotkey from flows (per-chord failure non-fatal).
- [ ] T3.4 Autostart.
- [ ] T3.5 PipeServer + protocol.
- [ ] T3.6 `dctl` CLI.
- [ ] T3.7 Footprint gate.

## M4 Providers (each CLI-testable via `dctl run`/`devices`; hardware steps verified on the dev PC)
- [ ] T4.1 wait, notify, flag.
- [ ] T4.2 process.run, script.powershell.
- [ ] T4.3 audio (port OSD worker + IPolicyConfig; wait for endpoint via notification).
- [ ] T4.4 bluetooth (port OSD D-023 + disconnect). **Gate: the user's Connect Technics flow works on Alt+Z →
  user can stop scd.**
- [ ] T4.5 display presets + outputs-only (port scd CCD).
- [ ] T4.6 Spike + display.layout capture/apply.
- [ ] T4.7 http + credentials.
- [ ] T4.8 firetv.
- [ ] T4.9 service.

## M5 Run pop-up
- [ ] T5.1 Spike: native acrylic window in AOT (DWM backdrop + D2D text, theme, DPI, no-activate).
- [ ] T5.2 Pop-up with progress/failure summary, position/duration, animation (honour Animation effects).

## M6 Editor foundation
- [ ] T6.1 WinUI shell (Mica, TitleBar, NavigationView: Flows, Library, Settings, About), unpackaged
  self-contained (`EnableMsixTooling`, OSD X-004), single instance + bring to front.
- [ ] T6.2 EngineClient (pipe) + "engine not running" state.
- [ ] T6.3 Config load/autosave (debounced, atomic, backups), external-change InfoBar.
- [ ] T6.4 Flows list page; Settings + About pages.

## M7 Canvas
- [ ] T7.1 Render nodes/cards/edges from VM, pan/zoom/fit.
- [ ] T7.2 Move, select, multi-select.
- [ ] T7.3 Connect by port drag, cycle refusal.
- [ ] T7.4 Cards: group/ungroup, drag pills in/out.
- [ ] T7.5 Palette + quick-add search.
- [ ] T7.6 Auto-arrange (Core layered layout, tested).
- [ ] T7.7 Undo/redo, copy/paste, delete.
- [ ] T7.8 Outline view (keyboard + screen reader equivalent).

## M8 Inspector + Library
- [ ] T8.1 Inspector from `ParamSpec` (device pickers live via pipe, hotkey recorder, file picker, durations),
  failure-policy control, Test step.
- [ ] T8.2 Library pages with Test.
- [ ] T8.3 Problems list.

## M9 Runs
- [ ] T9.1 Live run overlay on the canvas.
- [ ] T9.2 History list + parallel-lane timeline.

## M10 Ship
- [ ] T10.1 Velopack pack/install/update + release.yml.
- [ ] T10.2 README.
- [ ] T10.3 UX acceptance pass against the UX.md checklist (screenshots, light/dark/high contrast, 100–175 %
  scale, text size).
- [ ] T10.4 Footprint gate + 24 h daily-driver soak (user), then retire scd (user turns off its scheduled task).
