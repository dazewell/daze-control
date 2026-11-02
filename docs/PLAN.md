# Plan

## Resume
Current task: M7 done on feat/m7-edit (PR open, see `gh pr list`); next M8 T8.1 Inspector
Last green checkpoint: 9ae0f67 "docs: record M7 edit commands and bump to 0.12.0"
Next action: if the feat/m7-edit PR is still open, finish its merge policy (§6) first. Then branch feat/m8-inspector from main: T8.1 = inspector pane 320 wide right of the canvas in ShellWindow, VM `InspectorViewModel` built from `ParamSpec` for the selected step (nothing selected → flow settings)
Tree state: builds, 648/648 tests green · Uncommitted: none
Notes for next agent: version is 0.12.0. M7 leftovers are listed in ARCHITECTURE → Open questions (port/edge "+", minimap, dot grid, F2, Ctrl+L, live region). Test the editor only with `--config <scratch>\config.yaml`; send Enter with SendKeys (build skill). Deferred from PR #9 (minor): "Close without saving" flushes after a plain save error; OnClosing is async void around ContentDialog.

---

## Workflow (usage-cap-proof, D-018)

- This file = the task list with status `[ ]` / `[~]` / `[x]`, branch and PR per task. Its top is the
  **Resume block**, rewritten in every checkpoint commit; its format is in the `resume` skill (not copied
  here, so a search-and-replace of the block can't clobber it).

---

## M0 Bootstrap ✔ (2026-10-31; T0.1–T0.4 on main, T0.5 PR #1)
- [x] T0.1 `git init`, `.gitignore`; ask the user before `gh repo create` (name, private?).
- [x] T0.2 Docs copied from the plan: CLAUDE.md, GOALS, FEATURES, ARCHITECTURE, DECISIONS D-001..D-021,
  DEADENDS, UX, PLAN (this file).
- [x] T0.3 Skills: `repo-workflow`, `knowledge-log`, `build` (adapted from the OSD), new `resume`;
  `.claude/settings.json` SessionStart hook printing the Resume block.
- [x] T0.4 Solution skeleton: all projects compile, one passing test per test project, `ci.yml` (build + test).
- [x] T0.5 Spike (gate): AOT engine with tray icon + one hotkey + YamlDotNet static context load; no trim/AOT
  warnings; `scripts/measure.ps1` → idle numbers in ARCHITECTURE. Over budget → decide with the user.

## M1 Core model + YAML ✔ (2026-10-31, PR #2)
- [x] T1.1 Model records + outcomes (PR #2).
- [x] T1.2 YAML DTOs/mapper, round-trip tests, fixtures incl. the user's flow (PR #2).
- [x] T1.3 Validation (cycles, refs, card rules, params vs descriptors; problem records with node paths) (PR #2).
- [x] T1.4 Provider catalog descriptors + `ParamSpec` (PR #2).
- [x] T1.5 _(mechanical)_ Hand-convert the user's scd.toml into `samples/user-config.yaml` (Technics flow +
  both Bluetooth devices) — the one-time "import" (PR #2).

## M2 Runtime (pure, fake providers + `ManualTime`) ✔ (2026-11-01)
- [x] T2.1 Scheduler: ready set, all-inputs join, edge `when`, skip propagation, cards (feat/m2-runtime).
- [x] T2.2 Failure policies, retries, timeouts, cancellation (feat/m2-runtime).
- [x] T2.3 Resource locks (feat/m2-runtime).
- [x] T2.4 `whenRunning` (feat/m2-runtime).
- [x] T2.5 Run events + history (last 100) + `history.jsonl` (feat/m2-runtime).
- [x] T2.6 ConfigHost: load, debounced reload, last valid version per flow (D-021), never-valid flows disabled (feat/m2-runtime).

## M3 Engine shell ✔ (2026-11-01, PR #4)
- [x] T3.1 Composition root, single instance mutex, Log, global exception handlers (feat/m3-engine-shell).
- [x] T3.2 TrayIcon from flows (folders, separators, running mark, Pause hotkeys, Open editor, Exit) (feat/m3-engine-shell).
- [x] T3.3 GlobalHotkey from flows (per-chord failure non-fatal) (feat/m3-engine-shell).
- [x] T3.4 Autostart (feat/m3-engine-shell).
- [x] T3.5 PipeServer + protocol (feat/m3-engine-shell).
- [x] T3.6 `dctl` CLI (feat/m3-engine-shell).
- [x] T3.7 Footprint gate (feat/m3-engine-shell).

## M4 Providers ✔ (2026-11-01; PRs #5, #6, and the T4.7–T4.9 PR) (each CLI-testable via `dctl run`/`devices`; hardware steps verified on the dev PC)
- [x] T4.1 wait, notify, flag (branch feat/m4-basic-steps).
- [x] T4.2 process.run, script.powershell (branch feat/m4-basic-steps).
- [x] T4.3 audio (port OSD worker + IPolicyConfig; wait for endpoint via notification) (branch feat/m4-basic-steps).
- [x] T4.4 bluetooth (port OSD D-023 + disconnect) (branch feat/m4-basic-steps). **Gate: the user's Connect
  Technics flow works on Alt+Z → user can stop scd.** Unit tests + dry run pass; live check waits on the user
  (GOALS → Waiting on the user).
- [x] T4.5 display presets + outputs-only (port scd CCD) (branch feat/m4-system-steps).
- [x] T4.6 Spike + display.layout capture/apply, `dctl devices` + `dctl capture-display-layout` (branch feat/m4-system-steps).
- [x] T4.7 http + credentials (branch feat/m4-net-steps).
- [x] T4.8 firetv (branch feat/m4-net-steps).
- [x] T4.9 service (branch feat/m4-net-steps).

## M5 Run pop-up ✔ (2026-11-01, PR #8)
- [x] T5.1 Spike: native acrylic window in AOT (DWM backdrop + D2D text, theme, DPI, no-activate) — passed, D-034 (feat/m5-popup).
- [x] T5.2 Pop-up with progress/failure summary, position/duration, animation (honour Animation effects) (feat/m5-popup).

## M6 Editor foundation ✔ (2026-11-01)
- [x] T6.1 WinUI shell (Mica, TitleBar, NavigationView: Flows, Library, Settings, About), unpackaged
  self-contained (`EnableMsixTooling`, OSD X-004), single instance + bring to front.
- [x] T6.2 EngineClient (pipe) + "engine not running" state.
- [x] T6.3 Config load/autosave (debounced, atomic, backups), external-change InfoBar.
- [x] T6.4 Flows list page; Settings + About pages (feat/m6-editor).

## M7 Canvas ✔ (2026-11-02, PRs #10, #11, feat/m7-edit)
- [x] T7.1 Render nodes/cards/edges from VM, pan/zoom/fit (feat/m7-canvas).
- [x] T7.2 Move, select, multi-select (feat/m7-canvas).
- [x] T7.3 Connect by port drag, cycle refusal (feat/m7-connect).
- [x] T7.4 Cards: group/ungroup, drag pills in/out (feat/m7-connect).
- [x] T7.5 Palette + quick-add search (feat/m7-edit).
- [x] T7.6 Auto-arrange (Core layered layout, tested) + Tidy up (feat/m7-connect).
- [x] T7.7 Undo/redo, copy/paste, delete (feat/m7-edit).
- [x] T7.8 Outline view (keyboard + screen reader equivalent) (feat/m7-edit).

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
  scale, text size). Includes the pop-up in a contrast theme and at 225 % text (not checked live in M5).
- [ ] T10.5 Pop-up for Narrator: it draws everything itself, so screen readers get only the window title. Raise a
  UIA notification (or a minimal provider with an invoke for "see what happened") without activating (PR #8 review).
- [ ] T10.4 Footprint gate + 24 h daily-driver soak (user), then retire scd (user turns off its scheduled task).
