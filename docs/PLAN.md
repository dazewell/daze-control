# Plan

## Resume
Current task: M11 — T11.1 merged (#23, 0.20.1); T11.4/T11.5 desktop shortcuts (PR #24, 0.21.0); next T11.2 → T11.3 (feat/m11-pickers)
Last green checkpoint: 4872024 "fix(shortcuts): refuse another config's run…" + merge of main (#23)
Next action: T11.2 — `ParamSpec.Suggest`/`Ordered`/`Examples` in Core/Catalog, then the catalog coverage test
Tree state: builds, all tests green, AOT publish clean · Uncommitted: none
Notes for next agent: release.yml is tag-only (D-043): never push a v-tag without the user. "Shortcut" in the UI means a hotkey; the .lnk is always "desktop shortcut" (UX §5.4). Desktop shortcuts on an installed copy (survives an update, uninstall removes it) are unverified: GOALS → follow-ups. The editor has no flow delete yet.

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
- [ ] T10.5 Pop-up for Narrator (deferred past M10, not in the ship brief): it draws everything itself, so screen
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
  H3 / Technics EAH-AZ40M2 / WF-C710N; editor screenshot of the picker. (fix/bluetooth-list)
- [ ] T11.2 Catalog suggestions + examples (feat/m11-pickers, D-050). Core: `ParamSpec.Suggest` (Devices / Config /
  Static), `Ordered`, `Examples`; Android key table shared with the provider; examples per UX §5.4. Ipc/Providers:
  `devices services` (EnumServicesStatusEx, no admin), `devices credentials` (CredEnumerate `DazeControl:*`, names
  only), `DeviceInfo.Value`; `dctl devices services|credentials`. Done when: a catalog test says every Text/TextList/
  MultilineText/TextMap param has a Suggest or Examples (or is on a short reviewed allow-list, e.g. `notify.text`);
  every Devices kind exists in `SystemDevices.Kinds`; key-table round-trip test with the provider.
- [ ] T11.3 Inspector pickers (feat/m11-pickers, D-050, D-051). Editable ComboBox, checklist, token list, Examples
  menu in `ParamFieldViewModel` + InspectorPane; `display.outputs` checklist with "Use the screens that are on now"
  (name or port per D-051, unplugged kept checked; provider matches exact names before "contains"); Android input from the chosen device. Done when: VM tests (writes,
  twin-name → port, unplugged kept, undo of an example), screenshots light/dark of each control, Narrator names on the
  checklist and token list.
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

## First-use fixes (user, 2026-11-02; branch feat/first-use-fixes)
- [x] F1 Flows list stays inside the window when maximized (X-003).
- [x] F2 Fire TV → Android everywhere, clean rename (D-045).
- [x] F3 Library › Displays: rename (refs follow) and details of the saved screens (D-046).
- [x] F4 Library › Programs: Open file location, "Used by" opens the flows (D-046; Programs stays derived, D-041).
