# Plan

## Resume
Current task: M2 review — PR #3 (branch feat/m2-runtime), then T3.1
Last green checkpoint: 4737d5e "feat(runtime): raise run events and keep the last 100 runs"
Next action: PR #3: wait for CI + the Copilot review in the background (repo-workflow section 6.7), handle findings per section 6 (budget 2 reviews), squash-merge, sync main; then T3.1 on feat/m3-engine-shell: Engine/Program single-instance mutex, Log, global handlers; compose ConfigHost + FlowRunner (ResourceLocks, RunHistory at %LOCALAPPDATA%\DazeControl\history.jsonl) + FlowLauncher; hotkeys from ConfigHost.Current.RunnableFlows, re-registered on Changed (marshal to the message thread)
Tree state: builds, 232/232 tests green, AOT publish clean · Uncommitted: none
Notes for next agent: M2 (T2.1-T2.6) is complete on feat/m2-runtime, PR #3 open, no Copilot review yet. Version 0.3.0. No providers exist yet: T3.x wires with an empty provider list until M4.

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
