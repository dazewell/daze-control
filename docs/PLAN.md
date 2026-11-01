# Plan

## Resume
Current task: M4 Providers — T4.3 audio (branch feat/m4-basic-steps, no PR yet; T4.1–T4.2 done)
Last green checkpoint: 0ef5d49 "wip(providers): process.run and script.powershell"
Next action: T4.3 code + fake tests are in (Providers/Audio, ComThread). Still to do: AOT publish of the engine (scd-rewamp:build skill) must stay warning-free with the hand-made IMMNotificationClient vtable; then a live check of audio.setDefault via a temp config + dctl run, recording the current default output first and setting it back right after; then tick T4.3 and start T4.4 bluetooth
Tree state: builds, 382/382 tests green · Uncommitted: none
Notes for next agent: PR #4 left 3 minor items open (see its last comment). Never end a pipe I/O by disposing the pipe (X-001). Providers read params via `StepParams` (written value, else descriptor default). Plan: PR 1 = T4.1–T4.4, PR 2 = T4.5–T4.9. T4.4 live gate (Connect Technics on Alt+Z) needs the user's headphones: prove with unit tests + dry run, leave the live check to the user.

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

## M4 Providers (each CLI-testable via `dctl run`/`devices`; hardware steps verified on the dev PC)
- [x] T4.1 wait, notify, flag (branch feat/m4-basic-steps).
- [x] T4.2 process.run, script.powershell (branch feat/m4-basic-steps).
- [~] T4.3 audio (port OSD worker + IPolicyConfig; wait for endpoint via notification) (branch feat/m4-basic-steps).
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
