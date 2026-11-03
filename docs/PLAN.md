# Plan

## Resume
Current task: C1 + C2 canvas fixes in PR #29 (fix/run-together-linked, D-058, D-059); M12 flow menu designed, nothing built yet
Last green checkpoint: docs(plan): C1 done, Resume block points back at M12
Next action: T12.2 spike S2 (no-activate menu window, needs nothing), DESIGN §5; T12.1 needs the user's Xbox pad
Tree state: builds, 942/942 tests green · Uncommitted: none
Notes for next agent: release.yml is tag-only (D-043): never push a v-tag without the user. "Shortcut" in the UI means a hotkey; the .lnk is always "desktop shortcut" (UX §5.4); controller combos are "controller buttons". M12 answers from the user (2026-11-03): pads = Xbox + Steam Controller, the menu never takes focus, XInput polling only while a pad is on is acceptable. After PR #29: T12.1 spike S1 (needs the user's Xbox pad) or T12.2 spike S2 (needs nothing), DESIGN §5. T10.5 is parked as issue #26 (T12.9 shares it). The editor has no flow delete yet. FlowRunnerTests' waits fail after 30 s real time; Runtime.Tests can stall when another session's tests use the per-user pipe: rerun alone.

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

## M8 Inspector + Library ✔ (2026-11-02)
- [x] T8.1 Inspector from `ParamSpec` (device pickers live via pipe, hotkey recorder, file picker, durations),
  failure-policy control, Test step.
- [x] T8.2 Library pages with Test (feat/m8-library).
- [x] T8.3 Problems list (feat/m8-library).

## M9 Runs ✔ (2026-11-02, PRs #16, #17)
- [x] T9.1 Live run overlay on the canvas (feat/m9-runs, PR #16).
- [x] T9.2 History list + parallel-lane timeline (feat/m9-runs-page, PR #17).

## M10 Ship (feat/m10-ship)
- [x] T10.1 Velopack pack/install/update + release.yml (D-043). Verified: local pack, install into a scratch folder,
  `dctl update` from a local feed, apply while running (engine exits cleanly) and at engine start, uninstall
  (folder, shortcut, Apps entry and Run value gone).
- [x] T10.2 README.
- [x] T10.3 UX acceptance pass (2026-11-02, screenshots at 175 % scale / 110 % text, the PC's settings). Passed: run
  states without colour in light/dark/high contrast (Flows, Runs, pop-up), Runs lanes + one caution bar + "Done, with
  a problem", pop-up 340 DIP and §5.7 wording, dashed may-fail edge, aligned columns. Fixed: Flows list said "Worked,
  with problems" (now the shared run words), canvas fit centred a flow too big for the window (now starts at its
  first step), run list cut the duration off, a stale "Updates arrive with the first release" card.
  Not checked (system-wide settings on the user's PC, or not re-run this pass): 100–150 % scale, 225 % text (editor
  and pop-up), Narrator/Outline, Tab order, drag-into-cycle tip, empty states, animation effects off, tray menu.
- [ ] T10.5 Pop-up for Narrator — parked as issue #26 (user, 2026-11-03). Deferred past M10, not in the ship brief: it draws everything itself, so screen
  readers get only the window title. Raise a UIA notification (or a minimal provider with an invoke for "see what
  happened") without activating (PR #8 review).
- [x] T10.4 Footprint gate passed (ARCHITECTURE → Footprint, 2026-11-02). The 24 h soak and retiring scd are the
  user's (GOALS → Waiting on the user).

## M11 Shortcuts, pickers, Bluetooth list (user, 2026-11-02; design D-049..D-052, X-005)
Order: T11.1 alone first (a bug the user sees); then T11.2 → T11.3 and T11.4 → T11.5. One PR per branch, feat
PRs bump the minor version.
- [x] T11.1 Bluetooth list = Windows' paired devices (fix/bluetooth-list, D-052). `Providers/Bluetooth`: CfgMgr32
  enumeration of present `BTHENUM\DEV_*` + `BTHLE\DEV_*` behind a seam, merged by address with the endpoint data
  (connectable + Connected); `DeviceInfo.State`, a "not connectable" flag the editor disables (Library "Add from
  Windows" and the step picker, #26). Check one devnode property for non-audio Connected against Settings; none
  reliable → no state. Done when: tests on the merge with fake devnodes/endpoints (ghost endpoints dropped, LE+classic
  of one address merged, non-audio listed not connectable); on the dev PC `dctl devices bluetooth` lists exactly MX
  Master 3S, MX Keys Mini, Steam Ctrl, OPPO dazewell, Zone 301, WH-1000XM5, Technics EAH-AZ80 and none of SOUNDPEATS
  H3 / Technics EAH-AZ40M2 / WF-C710N; editor screenshot of the picker. (fix/bluetooth-list, PR #23)
- [x] T11.2 Catalog suggestions + examples (feat/m11-pickers, D-050). Core: `ParamSpec.Suggest` (Devices / Config /
  Static), `Ordered`, `Examples`; Android key table shared with the provider; examples per UX §5.4. Ipc/Providers:
  `devices services` (EnumServicesStatusEx, no admin), `devices credentials` (CredEnumerate `DazeControl:*`, names
  only), `DeviceInfo.Value`; `dctl devices services|credentials`. Done when: a catalog test says every Text/TextList/
  MultilineText/TextMap param has a Suggest or Examples (or is on a short reviewed allow-list, e.g. `notify.text`);
  every Devices kind exists in `SystemDevices.Kinds`; key-table round-trip test with the provider.
- [x] T11.3 Inspector pickers (feat/m11-pickers, D-050, D-051). Editable ComboBox, checklist, token list, Examples
  menu in `ParamFieldViewModel` + InspectorPane; `display.outputs` checklist with "Use the screens that are on now"
  (name or port per D-051, unplugged kept checked; provider matches exact names before "contains"); Android input from the chosen device. Done when: VM tests (writes,
  twin-name → port, unplugged kept, undo of an example), screenshots light/dark of each control, Narrator names on the
  checklist and token list. (feat/m11-pickers, PR #25; T11.2 too)
- [x] T11.4 Desktop shortcuts, engine side (feat/m11-shortcuts, D-049; installed-copy update/uninstall unverified, GOALS). `--run`, second-instance forward, queue
  until first load, trigger "Desktop shortcut", `FlowShortcuts` (.lnk + glyph .ico), ops `shortcuts`/`create-shortcut`/
  `remove-shortcut`, `dctl shortcut <flow> [--remove]`, uninstall hook cleanup, id-only resolution (`exactId`),
  config path kept in the args. Done when: unit tests (args parse, scan matches only our target, name sanitising
  incl. `/` and reserved names, a deleted flow's id never matches another flow's name); a non-default config's
  shortcut cold-starts that config; on the dev PC a shortcut runs its flow with the engine running and
  with it stopped (history shows "Desktop shortcut"), an installed copy's shortcut survives an update, uninstall
  removes it.
- [x] T11.5 Desktop shortcuts, editor side (feat/m11-shortcuts; the editor has no flow delete yet, so nothing to remove on Save). Flow settings row + Flows card context menu, Update
  on an old name, engine-off state (#25), deleting a flow removes its shortcut on Save. Done when: VM tests, screenshots.

## M12 Flow menu + controller trigger (user, 2026-11-03; design docs/flow-menu/DESIGN.md, D-053..D-057)
Order: T12.1 and T12.2 (spikes, gates) first, either order; then T12.3 → T12.4 → T12.5, T12.6, T12.7 → T12.8 →
T12.9 → T12.10. One PR per task unless two share a branch; feat PRs bump the minor version. Each task moves what it
built from DESIGN.md into ARCHITECTURE/UX (D-053).
- [ ] T12.1 Spike S1: controller input in AOT (spike/gamepad-input, DESIGN §5). Done when: gate (a)–(g) answered on
  the dev PC with the user's Xbox pad (ask the user to plug it in; Steam on and off); D-056 set to accepted with the
  backend and numbers; Footprint rows added; probe deleted or left in `spikes/` untracked by the solution.
- [ ] T12.2 Spike S2: no-activate menu window over apps and games (spike/menu-window, DESIGN §5). Done when: gate
  (a)–(e) answered with the throwaway D3D11 app in three modes, screenshots of the menu over each; D-055 updated
  with what held; a failed (a) stops M12 and goes to the user.
- [ ] T12.3 Core: `menu.show` descriptor, `GamepadChord`, `Flow.Gamepad`, `ParamSpec.RefersTo = Flow`, validation
  (feat/m12-core, DESIGN §2.5, §3.5). Done when: YAML round-trip of DESIGN §2.1's flow; chord parse/format tests
  (order, `+` spacing, Guide/one button/5 buttons refused); validator tests for own flow, unknown and duplicate item,
  `wait` loop, 0/13 items, `closeAfter` range, `timeout` ≤ `closeAfter`, chord clash (later flow); a flow rename
  rewrites menu items (Core edit + test); catalog test still passes (items has Suggest).
- [ ] T12.4 Runtime: `MenuProvider`, `IMenuPresenter`, `IFlowStarter` in `EngineController`, `StepContext.SetSummary`,
  `StepEndsFlowException` + `RunFinished.Quiet`, resource `menu`, dry run and test-step (feat/m12-runtime, DESIGN
  §3.2). Done when: Runtime.Tests with a fake presenter: pick launches with trigger "Menu · Couch" and summary; ignore
  while running; `wait` takes a failed child's outcome; nothing picked → node and flow Cancelled, quiet, `always`
  edges don't run; two menus serialize on the presenter gate; a `wait: true` parent picking a submenu flow shows the
  child's menu (gate released before the launch) and takes its outcome; "Back" to a parent that is still running
  follows its `whenRunning`; dry run doesn't show; test-step returns the pick without a run.
- [ ] T12.5 Runtime: pure `MenuModel` + `GamepadChordRecognizer` + `GamepadTable` (feat/m12-runtime, DESIGN §2.4,
  §3.4, §4.3). Done when: tests with `ManualTime`: list/grid moves (no wrap, 2D), repeat 400/120 ms, stick hysteresis,
  released-since-open, 1–9, close timer restarts on input and ends as cancel, last pick focused first, Steam dedupe
  (key within 100 ms of the same pad move dropped); chord fires on first release only for the exact set, never inside
  a longer chord; table diff on reload, Pause, `record-gamepad` pausing chords.
- [ ] T12.6 Engine: `GamepadSource` (S1's backend) + `DeviceArrival` + suspend/resume, loaded only when needed; ops
  `devices gamepads`, `record-gamepad`; `dctl devices gamepads` (feat/m12-gamepad). Done when: unit tests on the seams
  (no chord → never started; last pad gone → stopped; `record-gamepad` with no chord in the config starts the source,
  records the first chord and releases it on answer, timeout and client gone; `devices gamepads` lists a pad with no
  chord configured); on the dev PC a chord on a test flow runs it with another app
  in front and history says "Controller · View + Menu"; `dctl devices gamepads` lists the pad; footprint per DESIGN
  §3.8 measured (no chord; chord + no pad; pad on) and recorded.
- [ ] T12.7 Engine: `AcrylicSurface` cut from `RunPopup`, `FlowMenu` (list, grid, large, focus visual, hint row,
  close countdown, empty state), `MenuKeys`, mouse hook, placement, full-screen check, animation and high contrast
  (feat/m12-menu-window, DESIGN §3.3, §4). Done when: RunPopup unchanged (screenshots before/after); screenshots of
  the menu light/dark/high contrast, list and grid, normal and large, empty state, at 175 % scale; `dctl run` of a
  menu flow navigated by keys, mouse and the pad on the dev PC; foreground window unchanged; keys free after each
  close path (a test app registers Up); footprint after 10 menus within D-036.
- [ ] T12.8 Editor: `menu.show` inspector (flow token list, title, layout, size, close after, wait), Test shows the
  menu, "Controller buttons" recorder, "Controller menu" template, trigger texts (feat/m12-editor, DESIGN §3.7). Done
  when: VM tests (add/remove/reorder items, own flow not offered, missing flow chip, recorder states incl. engine off
  and no pad, template content); screenshots light/dark of the inspector and the recorder; Narrator names on the
  token list and recorder.
- [ ] T12.9 Narrator: UIA notifications from the menu without focus, sharing issue #26's provider (feat/m12-narrator).
  Done when: Narrator reads "Couch menu, 4 choices. Connect TV, 1 of 4." on open and the new choice on each move,
  the foreground window unchanged; #26's pop-up announcement may land in the same PR.
- [ ] T12.10 Acceptance + docs fold-in (DESIGN §4, UX §5.9 M12). Done when: the checklist's M12 lines pass on the dev
  PC (or are listed as the user's), README has the Steam Controller recipe and the known limits (DESIGN §4.6),
  DESIGN.md holds only what's still open, GOALS status current.

## First-use fixes (user, 2026-11-02; branch feat/first-use-fixes)
- [x] F1 Flows list stays inside the window when maximized (X-003).
- [x] F2 Fire TV → Android everywhere, clean rename (D-045).
- [x] F3 Library › Displays: rename (refs follow) and details of the saved screens (D-046).
- [x] F4 Library › Programs: Open file location, "Used by" opens the flows (D-046; Programs stays derived, D-041).

## Canvas fixes (user reports)
- [x] C1 Linked steps can be dropped / Ctrl+G'd together; the links between them go (D-058; fix/run-together-linked, PR #29).
- [x] C2 A new fallback switches its source from Stop to Skip, with a tip (D-059; same PR).
