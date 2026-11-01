# Plan

## Resume
Current task: M4 Providers — T4.6 rest (branch feat/m4-system-steps, no PR yet; T4.1–T4.4 merged as PR #5 `72153ab`, T4.5 + most of T4.6 on this branch, synced with main)
Last green checkpoint: 080da76 "Merge remote-tracking branch 'origin/main' into feat/m4-system-steps"
Next action: answer Ops.Devices and Ops.CaptureDisplayLayout (stubs in Runtime/Control/EngineController.cs, "This version can't do that yet"): add a small `IDeviceQueries` seam in Runtime, implement it in Providers (audio/bluetooth/displays lists, CcdDisplays.CaptureLayout → YAML snippet for library.displays), wire it in Engine/Program.cs, print it in dctl; then tick T4.6 and do T4.7 http + credentials
Tree state: builds, 406/406 tests green · Uncommitted: none
Notes for next agent: PR 2 (T4.5–T4.9) opens from this branch; bump the version once against main (0.5.0 → 0.6.0). Display tests on the dev PC use `CcdDisplays(validateOnly: true)` (SDC_VALIDATE): never apply a layout live without a revert ready. Set DAZE_DEV_PC=1 to turn on the dev-PC hardware asserts. T4.4 live gate waits on the user (GOALS → Waiting on the user). Bash heredocs with nested quotes fail to parse in this tool; write files with Write.

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
- [x] T4.3 audio (port OSD worker + IPolicyConfig; wait for endpoint via notification) (branch feat/m4-basic-steps).
- [x] T4.4 bluetooth (port OSD D-023 + disconnect) (branch feat/m4-basic-steps). **Gate: the user's Connect
  Technics flow works on Alt+Z → user can stop scd.** Unit tests + dry run pass; live check waits on the user
  (GOALS → Waiting on the user).
- [x] T4.5 display presets + outputs-only (port scd CCD) (branch feat/m4-system-steps).
- [~] T4.6 Spike + display.layout capture/apply (branch feat/m4-system-steps): capture/apply + library kind done, spike validated; `dctl capture-display-layout` + `devices` pipe ops still to do.
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
