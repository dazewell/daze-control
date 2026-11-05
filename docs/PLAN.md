# Plan

## Resume
Current: M13 and L1 done (released up to v0.39.0). M12: T12.2 done (#56, "Borderless only", D-055); T12.1 (spike S1, controller input) paused in draft PR #58 until the user has the Xbox pad: its PR `## Resume` has the gates left.
Next action: T12.1 when the pad is back; T12.3 (core: `menu.show`, `GamepadChord`, validation) can start before it. Also open: the M13 gate (UX acceptance list); DESIGN §1 B live run (changes the default audio device: ask the user).
Workflow (D-079): PRs don't bump `<Version>`, merge with `gh pr merge --auto --merge`, keep their live state in the PR's `## Resume`, and don't edit this block.
Notes for next agent:
- Never push a v-tag without the user (D-043). Live display tests: `scripts/display-check.ps1` (`-Go` changes the user's screens: ask first every time).
- Installed app version: `%LOCALAPPDATA%\DazeControlApp\current\sq.version` (updates apply on the next engine start).
- Runtime.Tests can stall when another session's tests use the per-user pipe: rerun alone. measure.ps1 can't run while the installed engine holds the pipe.
- Win+[ and Win+] are held by the user's `SwitchDisplay.ahk` (leave it running). This PC runs with Num Lock off: live-test hotkeys with F-key chords.
- The user's Connect Technics flow: `bt` should be "Skip what depends on it" (told in T13.7).
---

## Workflow (usage-cap-proof, D-018)

- This file = the task list with status `[ ]` / `[~]` / `[x]`, branch and PR per task. Its top is the
  **Resume block**: what's next overall, kept short, rewritten only by the coordinator. A task's live state is
  the `## Resume` section of its PR description (D-079); the format is in the `resume` skill (not copied
  here, so a search-and-replace of the block can't clobber it).

---

## M14 Step defaults, checks and "Only if" (user, 2026-11-04) — before the remaining plan (M12 T12.9/T12.10, T10.5, M13 gate)
Designed 2026-11-04: docs/step-settings/DESIGN.md, D-080..D-083. One PR per task, no version bumps (D-079); every task
moves its as-built facts from DESIGN into ARCHITECTURE/UX/FEATURES and cuts them there (D-053). Opus medium each.
**Order and parallelism** (files that clash are named; "after" = same files, merge it first):
`T14.1 → T14.2`; `T14.1 → T14.3 → T14.4` (editor, parallel with T14.2); `T14.2 → T14.5` (Core/Runtime again);
`T14.5 → T14.6, T14.8, T14.9` (providers: separate folders, all three add lines to `Core/Catalog/ProviderCatalog.cs` and
`Providers/BuiltInProviders.cs`: take both sides on conflict); `T14.4 + T14.5 → T14.7` (editor); `T14.7 → T14.10`.
Safe in parallel: {T14.2, T14.3}, {T14.4, T14.5}, {T14.6, T14.8, T14.9}.

- [x] T14.1 Defaults model: Core (feat/m14-defaults-core, D-080). Files: `Core/Model/Node.cs` (`OnFailure?`, retry as two nullable
  fields), `Model/Configuration.cs` (`Settings.StepDefaults`), `Yaml/ConfigDocument` + `ConfigMapper` (read/save,
  explicit `stop` kept, null omitted), `Validation/ConfigValidator` (A4 table), new `Model/StepSettings.Resolve` and
  `StepDefaults.Apply`. Done when: Core tests cover the precedence table per field (own > type > built-in; count set,
  delay inherited), YAML round-trip of A2's sample, bad entries → Problems with the bad part dropped, and `full.yaml`
  + `samples/user-config.yaml` load and save byte-identical; all existing tests pass unchanged except the ones that
  assert `OnFailure`/`Retry` shapes.
- [ ] T14.2 Defaults reach runs (feat/m14-defaults-runtime, after T14.1). Files: `Runtime/FlowRunner.cs` (apply defaults first in
  `RunCoreAsync`, captured with the library at trigger), `Engine` run/test-step/dry-run entry, `Ipc` (config text already carries
  settings; check). Done when: Runtime tests: a type default timeout of 1 s fails a hanging step at 1 s, an explicit step timeout
  wins, a retry default retries, a default `skip` skips dependents; a default edited mid-run changes only the next run (D-021);
  `dctl run` against a config with `stepDefaults` (fake or `wait`/`notify` steps) shows the effect; dry run and "Test this step"
  use the on-screen defaults. Idle gate unchanged.
- [ ] T14.3 Settings › Step defaults card (feat/m14-defaults-settings, after T14.1). Files: Settings page + `SettingsViewModel`,
  new `StepDefaultsEditor` service (add/edit/remove entry, "Used by n steps in m flows" counts) in `Editor.ViewModels`, UX §5.7
  strings. Done when: view-model tests for add/edit/remove/counts, autosave writes `stepDefaults` and nothing else, a screenshot
  of the card (empty and with two rows), keyboard and Narrator names checked.
- [ ] T14.4 Inspector: Default/Set rows, Reset, "Use these for all … steps…" (feat/m14-defaults-inspector, after T14.3: uses its service). Files:
  `Editor.ViewModels/Inspector*`, the Timing and "If it fails" XAML, canvas chips (effective values, dimmer when inherited),
  the fallback menu's Stop→Skip switch (uses effective policy). Done when: view-model tests: Reset clears one field; a changed
  default moves inherited steps and not set ones; "Use for all" shows n steps and has Undo; D-059 switch sets Skip when the
  inherited policy is Stop; screenshots of a step with inherited and set rows.
- [ ] T14.5 Conditions core + runtime (feat/m14-conditions-core, after T14.2: same Core/Runtime files). Files: `ProviderDescriptor.Conditions`,
  `IFactProbe` (`Runtime/Providers`), `StepNode.Only` + YAML + validator (B7), `FlowRunner` (probe after the lock, once, inside the
  timeout; cannot tell → run; skip = Succeeded + note), `NodeResult` note through events, IPC and run history, dry-run assumption,
  "Test this step" answer. Fake probe + fake provider only. Done when: Runtime tests for B4 (unmet → skipped with note and `success`
  dependents run, `failure` edge doesn't fire, flow Succeeded; probe throws → step runs; retry doesn't re-probe; card member), a
  Core round-trip with `only: always`, old run-history files still read; `dctl run --dry-run` shows normal results.
- [ ] T14.6 Bluetooth condition + check (feat/m14-bluetooth-check, after T14.5). Files: `Providers/Bluetooth` (probe `bluetooth.connected` from the
  picker's device state), `bluetooth.check`, `Conditions` on connect/disconnect, catalog entries. Done when: provider tests with the
  fake query; on the dev PC (read-only) `dctl run` of a one-step check flow reports connected / not connected for the Technics
  library entry in < 1 s. A live Connect/Disconnect is not run without asking the user.
- [ ] T14.7 Editor for conditions and checks (feat/m14-conditions-editor, after T14.4 and T14.5). Files: inspector "Only if" row, palette entries,
  "If it isn't" label, canvas chip and condition-skipped dashed ring + caption, Runs bar and log line, pop-up variant of #48 for checks,
  "Test this step" Yes/No answer, UX §5.7 strings. Done when: view-model tests (row choices come from `Conditions`, "Always" stores
  or removes the key per B6, relabel for check steps), screenshots of an `only` chip, a skipped step after a run, and a check with a
  fallback; high contrast reads without colour.
- [ ] T14.8 Audio, program, service and file checks (feat/m14-checks-a, after T14.5). Files: `Providers/Audio`, `Providers/Process`,
  `Providers/Service`, new `Providers/File`, catalog entries (B5 rows). Done when: provider tests with fakes for each probe and
  `Conditions` (`notDefault`, `notRunning`, `stopped`/`running`), `file.check` exists/missing on a temp file, descriptor-has-one-
  implementation test passes, a dry run of each.
- [ ] T14.9 Display and Android checks, optional (feat/m14-checks-b, after T14.5). Files: `Providers/Display` (reuse `DisplayCheck`, D-077),
  `Providers/Android`; catalog. Done when: fake tests; `display.check` on the dev PC reports the current layout match without changing
  the screens (read-only); `android.check` only behind an adb already running (no new process on a quiet PC, or noted in
  FEATURES).
- [ ] T14.10 `only` in step defaults (feat/m14-only-defaults, after T14.7). Files: `StepDefaults`/validator/mapper (`only`), Settings card row,
  inspector row state (Default/Set). Done when: `bluetooth.connect: { only: disconnected }` skips a connected Connect, a step with
  `only: always` still runs, Reset returns to the default, YAML round-trip.

- [x] T14.11 Drop on the left third: "Run before" (feat/m14-run-before, D-083, DESIGN Part C; independent of T14.1–T14.10). Files:
  `Core/Layout/FlowEdits.cs` (`RunBefore`), `Editor.ViewModels/Canvas/FlowCanvasViewModel.cs` (`DropZone.RunBefore`, `ZoneAt`, `Drop`),
  `Editor/Canvas/FlowCanvas.xaml.cs` (hint), step context menu + Outline "Run before…", UX §5.2/§5.7. **Shares
  `FlowCanvasViewModel.cs` / `FlowCanvas.xaml.cs` with T14.4 and T14.7 (chips, rings): take both sides on conflict.** Done when:
  Core tests for the Part C table (incoming kinds kept, loop refused, card target, member dragged), `ZoneAt` thirds, one Undo
  restores the flow, Outline text; a screenshot of the left-third highlight; keyboard route reaches the same result.
- [x] T14.12 Type a shortcut instead of pressing it (feat/typed-shortcut; independent of T14.1–T14.11; user has no numpad). Files:
  `Editor/Inspector/HotkeyRecorder.xaml(.cs)` ("Type it instead" link, text box, Enter/Esc), `ViewModels/Inspector/ShortcutTexts.cs`
  (`TryParseTyped`), tests. Typed text goes through `Hotkey.TryParse` (not free text, so D-021 stands) and then `SetShortcut` like a
  recorded chord (same clash checks).

- [x] T14.13 Number pad operators as shortcut keys (feat/numpad-operators, D-085; independent of T14.1–T14.12). Files: `Core/Input/Hotkey.cs`,
  `Editor/Inspector/HotkeyRecorder.xaml.cs` (pad Delete = Num.), tests, UX/FEATURES.

## M15 Edit several steps at once (user, 2026-11-04) — after all M14 tasks (needs the M14 Default/Set and "Only if" rows)
Designed 2026-11-04: docs/step-settings/DESIGN.md Part D, D-084. Selection (click, Ctrl+click, marquee, Ctrl+A) already exists;
`InspectorMode.Many` shows only "n selected". Order T15.1 → T15.2 → T15.3 (all in `Editor.ViewModels/Inspector`: serial).
No version bumps (D-079). Opus medium.
- [ ] T15.1 Bulk-edit model (feat/m15-bulk-core, after T14.4, T14.7, T14.10). Files: new `Editor.ViewModels/Inspector/BulkEdit.cs`
  (shared fields of a selection, per-field state All default / All set / Mixed, apply Set / Reset to many steps through one
  `UpdateDraft`). Done when: view-model tests: one type vs several types vs steps+cards give the Part D2 field sets; Mixed when
  values differ or are partly inherited; Set writes every step, Reset clears every step; one Undo restores all; per-step
  validation problems appear and nothing is half-applied.
- [ ] T15.2 Many-mode inspector: policy, Timing, Only if (feat/m15-bulk-inspector, after T15.1). Files: `InspectorViewModel*.cs`, the Many
  section of the inspector XAML, header text, context menu "Select all <type> steps" in `FlowCanvasViewModel`/`FlowCanvas.xaml.cs`,
  UX §5.4/§5.7. Done when: tests for the Mixed placeholders and captions, "Reset all", text applies on commit as one Undo;
  screenshots of a one-type selection (mixed and uniform) and a two-type selection; Narrator names read "…, mixed" /
  "…, default"; keyboard reaches every row.
- [ ] T15.3 Many-mode params (feat/m15-bulk-params, after T15.2). Files: `ParamFieldViewModel.cs`, `InspectorViewModel.cs` param
  builder. Done when: tests: params of a one-type selection edit all (a device pick for all, a text for all, a bool), `device`
  fields with different library kinds across types are not shown, a required param can't be cleared, an optional one clears
  for all; one Undo each.

## M13 fixes (user, 2026-11-03, v0.29.0 feedback) — first, before the remaining M13 tasks
Designed 2026-11-03 (PR: docs/m13-fixes-plan): D-074..D-077, X-008, X-009, UX §5.1/§5.2/§5.7 #66–#70 "M13 fixes"
notes. User answers (2026-11-03): right-drag is fine (it pans only where there's room to scroll; the user misread
it), so F1 adds wheel zoom instead, and a flow that fits can still be moved; Delete with an Undo bar (no dialog);
Delete is off while another flow runs it; a live display test was OK'd (run 2026-11-03 20:45, screens restored).
Also found: this PC's installed app was still 0.22.0 (0.23.0 downloaded, applies on the next engine start).

**Parallel:** F1–F5 run at once, one session and one PR each. Code files don't overlap except `ShellWindow.xaml`
(F3: one attribute on `FlowEditorPage`; F4: the Flows card menu and the Undo InfoBar): whichever merges second takes
both sides. F4 must not touch the inspector or canvas files (F2, F1); F5 touches only `Providers/Display`, its tests
and a new script. Docs: each adds its own lines (UX, ARCHITECTURE, FEATURES, PLAN tick) and takes both sides on
conflict. Version (until D-079; PRs no longer bump it): `fix` PRs bumped the patch, `feat` PRs (F1, F4) the minor, against `main` at merge time (main's
number + 1).

- [x] F1 **Wheel zoom on the canvas; a flow can always be moved** (feat/m13-wheel-zoom, D-074, PR #45; 0.30.0). Not a bug: right-drag
  (D-069) pans only along an axis with room to scroll, so a flow that fits the window doesn't move (seen on the 0.29.0
  build); the user asked for wheel zoom and for pan room. Today the wheel scrolls, Shift+wheel scrolls sideways and
  Ctrl+wheel/pinch zoom 50–200 % through the `ScrollViewer`; Fit, 100 % and Zoom in/out in the CommandBar use
  `ZoomBy` / `ChangeView`. Files: `Editor/Canvas/FlowCanvas.xaml` (a transparent wrapper Grid around `Host` as the
  `ScrollViewer`'s content), `FlowCanvas.xaml.cs` (wrapper padding = viewport − 48 DIP per side at the current zoom,
  on size and zoom changes; `PointerWheelChanged` on the wrapper for mouse only, handled: plain and Ctrl+wheel zoom
  ×1.1 per notch at the pointer, Shift+wheel scrolls sideways; touchpad two-finger scroll and pinch stay the
  `ScrollViewer`'s; Fit and open centre; the zoom label and buttons follow), a pure helper `Editor.ViewModels/Canvas/
  CanvasViewport.cs` (offsets that keep the point under the pointer, pan room, clamp 50–200 %) with tests in
  `Editor.ViewModels.Tests`; docs UX §5.2 Navigate, ARCHITECTURE Editor canvas, FEATURES F7. Done when: helper tests
  (zoom keeps the anchor at 50/100/200 %, clamps at both ends, pan room for a flow smaller and larger than the
  viewport); running editor on a scratch copy of the user's config: the wheel zooms at the pointer, the zoom label
  and buttons agree, Shift+wheel scrolls sideways, right-drag moves the 2-step "New flow" both ways and stops with
  48 DIP showing, Ctrl+0 brings it back, a still right click still opens the menus; screenshots.
- [x] F2 **"Run a flow": label under Flows, and a working flow picker** (fix/m13-flow-run-picker; 0.29.2). Cause of the
  label: `CanvasNodeViewModel.Area` maps every catalog area but `"flow"`, so the Steps panel heading, the inspector's
  "flow › Run a flow" and the icon picker show the raw lowercase key. Cause of the picker: `ProviderCatalog` gives
  `flow` `Suggest = Config(Flows)`, but `InspectorViewModel.Suggest` has cases only for Devices, Flags and
  AndroidInputs, so the list is always empty; typing a name doesn't work either, because the step needs the flow's id,
  which the UI never shows (validator #51). Files: `Editor.ViewModels/Canvas/CanvasNodeViewModel.cs` (`"flow" =>
  "Flows"`), `Editor.ViewModels/Inspector/InspectorViewModel.cs` (the `ParamSuggest.Flows` case: the other flows, by
  name, with #29 captions; #69 when there are none), `Inspector/ParamFieldViewModel.cs` (`RefersTo == Flow` → a
  picker that shows names and stores the id; an unknown id kept as "<id> · isn't there anymore"),
  `Editor/Inspector/InspectorPane.xaml(.cs)` (its template), `Editor.ViewModels.Tests` (+ a test that every catalog
  area has a title starting with a capital). Done when: those tests; running editor: Steps panel shows "Flows › Run a
  flow", picking "Connect Technics" writes `flow: connect-technics` and clears #51, Dry run lists it; screenshots.
  Built (0.29.2): as planned, plus the canvas card's summary shows the called flow's name (it showed the id) and an
  empty Flow says "Choose a flow." (it said "Choose a device."). No "Running" caption: the inspector doesn't know live runs.
- [x] F3 **No stray "Ctrl+S" tip** (fix/m13-ctrl-s-hint, X-008; 0.29.1). Cause: Ctrl+S is a `KeyboardAccelerator` on the
  page-wide `FlowEditorPage` Grid with the default placement, so WinUI shows "Ctrl+S" as that Grid's tooltip wherever
  the pointer rests without another tooltip (seen at once after opening a flow on 0.29.0). Files:
  `Editor/ShellWindow.xaml` (`KeyboardAcceleratorPlacementMode="Hidden"` on `FlowEditorPage`). Done when: running
  editor, the user's steps (double-click the canvas, Esc the search, click elsewhere, wait 5 s) and resting over the
  palette, canvas and inspector show no tip; Save's own tooltip still says Ctrl+S; Ctrl+S still saves with Save off
  (a text box not committed yet); screenshot.
- [x] F4 **Delete a flow with Undo; the id follows a saved rename** (feat/m13-flow-delete-rename, D-075, D-076). Cause:
  the Flows list has no Delete (only Open / Run / desktop item), and the id is set once (`new-flow`, `new-flow-2`) and
  never follows the name. Files: new `Core/Model/FlowIds.cs` (`Slug`, `Unique`) and `Core/Model/FlowRefs.cs`
  (`Callers`, `Rename`) with `Core.Tests`; `Editor.ViewModels/Config/ConfigSession.cs` (`SaveDraft` renames when the
  name changed and reports the new id), `ShellViewModel.cs` (the open flow follows its new id), `FlowsPageViewModel.cs`
  (`NewFlow` via `FlowIds`; `Delete` / `Undo` / `CanDelete` + #68; the bar's end removes the desktop shortcut),
  `Engine/DesktopShortcutStore.cs` (move a shortcut to a new id), `Editor/ShellWindow.xaml(.cs)` (card menu Delete,
  Delete key, Undo InfoBar #67). Done when: slug tests (spaces, punctuation, diacritics, Cyrillic, emoji only → "flow",
  40-char cut, `-2` on a clash), rename tests (callers' `flow.run` rewritten, no change when the slug equals the id,
  draft undo), ConfigSession and FlowsPage tests (delete, undo at the same place and id, off while called, bar ends
  on New flow / open / leave); running editor on a scratch config: rename "New flow" to "Morning", Save → YAML key
  `morning`, a caller's step follows, its desktop shortcut still runs it, the engine log names `morning`; Delete then
  Undo restores it with its hotkey working; screenshots.

- [x] F5 **Display steps do what they say, or fail** (fix/m13-display-restore, PR #49, 0.33.1; live matrix passed 2026-11-03 21:10 with the user's OK, screens restored; open: switches undone after Duplicate, ARCHITECTURE → Open questions; D-077, X-009). Live test on the user's
  PC (2026-11-03, laptop panel INT-0 + DELL on HDMI-0; log and scripts in the coordinator's notes folder,
  `f5-display/`): "Project" (`display.topology`) is right every time, and "Restore screen layout" is right from a
  normal state. Three failures, all in `CcdDisplays.UseOnly` and `CcdLayout.Apply`:
  (1) "Use only these screens" [INT-0, HDMI-0] gives Extend with the laptop primary and DELL to its right, not the
  user's arrangement (DELL primary at 0,0, laptop left at −2880), because `UseOnly` sends
  `SDC_USE_SUPPLIED_DISPLAY_CONFIG` with no modes plus `SDC_ALLOW_CHANGES`, so Windows makes up positions instead of
  reading its database (X-009); (2) "Use only" [HDMI-0] from Extend left the laptop on, duplicated, and reported
  Succeeded: `ALLOW_CHANGES` lets Windows change the request and nothing checks the result; (3) "Restore screen
  layout" Dual from that duplicate state failed with error 87: `Apply` keeps the saved source ids and remaps only
  adapters and targets. Files: `Providers/Display/CcdDisplays.cs` (`UseOnly`: one distinct source id per chosen target
  on its adapter; `SDC_TOPOLOGY_SUPPLIED | SDC_APPLY` first so Windows restores that set's last arrangement, then
  `USE_SUPPLIED | ALLOW_CHANGES | SAVE_TO_DATABASE` only when the database has none), `CcdLayout.cs` (validate first;
  source ids remapped to free ones per adapter; on error 87 switch to the layout's set with `TOPOLOGY_SUPPLIED`, then
  apply), a pure `Providers/Display/DisplayCheck.cs` (what's active now vs what was asked: the set of screens, no
  shared source unless asked, and for a layout each screen's resolution and position) called after every write, the
  step failing with #70 when they differ; `Providers.Tests` for the source-id plan and the check (fakes, never
  `SetDisplayConfig`); `scripts/display-check.ps1` (the live matrix: saves the current config, runs each step through
  a dev engine and `dctl run --wait`, prints the state, restores in `finally`, restarts the installed engine). Done
  when: those tests; the live matrix on the user's PC, **only with the user's OK at that time**, gives the user's
  arrangement for "Use only" both, DELL alone for "Use only" HDMI-0 from Extend, and Dual from every starting state
  (internal, external, extend, the old duplicate); screens restored and the installed engine restarted;
  `display.topology` unchanged.

## M13 Priority: editor and flow updates (user, 2026-11-03) — after the M13 fixes above
Designed 2026-11-03 (PR: docs/m13-design): D-061..D-070, UX §5.1–§5.9 "M13" notes and #47–#66, X-006,
[flow-calls/DESIGN.md](flow-calls/DESIGN.md) for P6. User answers (2026-11-03): a failure its fallback fixed ends
"Done"; device inputs ship inside P6; Tidy up becomes a Layout menu with several strategies; after a wrapped chain the
next part goes on a new line under it. P-numbers are the user's items; T-numbers are the tasks.

**Order** (one fresh session per task, one PR each; until D-079 feat PRs bumped the minor against `main` at merge
time; PRs no longer bump it): T13.1 → T13.2 → T13.3 → T13.4 → T13.5 → T13.6 →
T13.7 → T13.8 → T13.9 → T13.10. **Independent lanes** (no shared files, may run at once):
- Lane A, editor shell (`ShellWindow.xaml(.cs)`): T13.1 → T13.2 → T13.3, then T13.6 (also needs T13.5).
- Lane B, canvas input (`FlowCanvas.xaml.cs`): T13.4.
- Lane C, layout (`Core/Layout`): T13.5 (then T13.6 in lane A).
- Lane D, outcomes and flow calls (Core/Runtime): T13.7 → T13.8 → T13.9, then T13.10 once lane A is done (it
  touches the inspector, Runs view and `ShellWindow`).
Every task: tests first where the logic is pure (Core/Runtime/ViewModels), the editor actually run and a screenshot of
the change for UI tasks, and the docs moved from "designed" to built (UX "M13" notes reworded as current, ARCHITECTURE
section rewritten, FEATURES where behaviour changed).

- [x] T13.1 P4 **Nav "Flows" returns to the list** (feat/m13-nav-flows, PR #36). Cause: with a flow open, Flows is still the
  selected nav item, so `SelectionChanged` never fires. Files: `Editor/ShellWindow.xaml(.cs)` (`ItemInvoked` on the
  Flows item while a flow is open → `LeaveFlowAsync` (D-047 prompt) → `CloseFlow`, show the list). Done when: in the
  running editor, Flows from an open flow shows the list; with a draft the Save / Don't save / Cancel prompt shows and
  Cancel stays in the flow; screenshots; no regressions in nav between other pages.
- [x] T13.2 P3 + P5 **Flows card: the flow's icon and a Run button** (feat/m13-flows-card, PR #40, D-070). Cause of P3: the
  card decodes `flow.Glyph` only (lightning when unset), while flow settings show `FlowGlyph.CodeOf` (own, else the
  first step's). Files: `Editor.ViewModels/FlowsPageViewModel.cs` (glyph via `FlowGlyph.CodeOf`; `Run`/`Stop`
  commands through `IEngineClient` with trigger "Editor"; `CanRun` + reason #62; running state from engine events),
  `Editor/ShellWindow.xaml(.cs)` (Run button replaces the chevron; the card one focus stop, Enter/Space open, UIA
  Invoke "Open <name>"; context menu Open / Run / desktop item), the Runs page list glyph if it has the same bug.
  Done when: ViewModel tests (glyph fallback, Run off reasons, Run ↔ Stop); running editor: icons match flow settings,
  Run starts the saved version and the pop-up shows, keyboard Tab/Enter opens; screenshots light/dark.
- [x] T13.3 P9 **Collapsible nav pane + `editor.json`** (feat/m13-nav-collapse, D-068). Files: new
  `Editor.ViewModels/Config/EditorSettingsStore.cs` (+ STJ context; `%LOCALAPPDATA%\DazeControl\editor.json`, atomic
  write 300 ms after a change on `TimeProvider`, bad file → defaults + log), `ShellViewModel.cs` (`IsNavPaneOpen`),
  `ShellWindow.xaml` (`IsPaneToggleButtonVisible`, `IsPaneOpen` two-way, `CompactPaneLength` 48). Done when: store
  tests (missing / corrupt / round-trip / debounce); running editor collapses to icons with tooltips and reopens
  collapsed after a restart; Narrator names the toggle.
- [x] T13.4 P1 **Right-drag pans the canvas** (feat/m13-right-drag, D-069). Files: `Editor/Canvas/FlowCanvas.xaml.cs`
  (pointer pressed/moved/released, `OnRightTapped` and context requests swallowed after a pan), a pure gesture helper
  in `Editor.ViewModels/Canvas` (arm → pan past the system drag rectangle → swallow), `GetSystemMetricsForDpi` via
  `LibraryImport`, converted to DIP. Done when: helper tests (below threshold = menu, past = pan + no menu, DPI scaling); running editor:
  right-drag over empty canvas, a pill and an edge pans without selecting; a still right click opens each menu.
- [x] T13.5 P2 **New line after a wrapped chain** (feat/m13-wrap-newline, PR #39, D-066). Files: `Core/Layout/FlowLayout.cs`
  (segments split at wrapped runs; the next segment at the block's x, one `WrapRowGap` under it, centred among
  itself; its entry edge gets `WrapLaneY`), `Core/Layout/EdgeCurve.cs` (wrap edge into a target lower than the lane),
  `Core.Tests` layout tests. Done when: tests for a 7-chain + fork (fork x = block x, top below the block, no overlap,
  extent width = block width), two blocks in a row, a block first and last; screenshot of that flow in the editor;
  ARCHITECTURE's layout text says it.
- [x] T13.6 P7 **Layout menu replaces Tidy up** (feat/m13-layout-menu, D-067; after T13.3 and T13.5). Files:
  `Core/Layout/FlowLayout.cs` (`LayoutOptions`: `WrapAfter` 0 = never, spacing presets, `Align` Centre/Top, `Lines`
  Curved/RightAngles), `EdgeCurve.cs` (orthogonal router for every edge), `Editor.ViewModels/Canvas/
  FlowCanvasViewModel.cs` (per-flow options from `EditorSettingsStore`, `CanUntangle` + reason via `FlowTidy`),
  `Editor/ShellWindow.xaml(.cs)` (Layout flyout #63), `EditorSettingsStore` (layouts by flow id). Done when: layout tests
  per option; untangle off on a chain with "No lines cross"; running editor: each choice visibly changes a sample flow
  and survives a restart; Untangle undoes with Ctrl+Z; screenshots of Compact / Roomy / Right angles.
- [x] T13.7 P8 **Fallbacks that weren't needed; fixed failures end Done** (feat/m13-fallback-outcomes, D-065). Files:
  `Core/Runs/Outcomes.cs` (`NotNeeded`; `Summarize` with handled failures), new `Core/Model/FlowShape.cs`
  (`Fallbacks`), `Runtime/Scheduling/Scheduler.cs`, `Runtime/Popup/RunPopupModel.cs` ("n of m", #48), `Runtime/Tray/
  TrayMenu.cs`, `Ipc/Protocol.cs`, editor run words / Runs timeline / canvas live captions (#47), FEATURES F1,
  ARCHITECTURE Config + Runtime. Done when: Scheduler tests on the user's Connect Technics shape (Technics works →
  Succeeded, Sony branch NotNeeded; Technics fails and Sony works → Succeeded; both fail → Failed), a chain after a
  fallback, a join of normal + fallback; pop-up model tests ("1 of 2", Done + #48); history round-trip with NotNeeded;
  `dctl run --dry-run` and a fake-provider run of that flow; AOT publish with zero warnings.
- [x] T13.8 P6a **`flow.run`: nested runs, loops, history, pop-up** (feat/m13-flow-run, PR #41, D-061..D-063; after T13.7).
  Files: `Core/Catalog/ProviderCatalog.cs` (`flow.run`; adds `ParamSpec.RefersTo = Flow`, which M12's T12.3 then
  reuses), `Core/Validation/ConfigValidator.cs` (call graph incl. `menu.show` wait items when they exist),
  `Core/Runs` (`NodeResult.Called`, `CalledRun`, events' `ParentRunId`), `Runtime/Control/IFlowStarter.cs` +
  `Runtime/Providers/FlowProvider.cs`, `Runtime/FlowRunner.cs` (`RunNestedAsync`, call chain, depth 8),
  `Runtime/FlowLauncher.cs` (chain for start mode), `RunHistory`/`HistoryJson`, `RunPopupModel` (fold nested, #59),
  `Ipc/Protocol.cs`, `Engine/EngineHost.cs` wiring. Done when: tests for every row of DESIGN §2.2, both modes, own flow
  / cycle / unknown / turned-off validation, chain and depth guards (incl. a D-021 old+new mix), two parallel calls of
  one flow both running, a nested step's lock blocking a top-level one; `dctl run` of DESIGN §1's shapes with safe
  steps (notify, wait, flags); AOT zero warnings; `scripts/measure.ps1` idle unchanged.
- [x] T13.9 P6b **Flow inputs** (feat/m13-flow-inputs, D-064). Files: `Core/Model` (`Flow.Inputs`, `FlowInput`),
  `Core/Yaml` DTO + mapper (`inputs:`), `ConfigValidator` (`$name` kinds, defaults, call `inputs:`), `Runtime/FlowRunner`
  (resolve before resources), `Editor.ViewModels/.../LibraryEdits` rename. Done when: YAML round-trip of DESIGN §1 B;
  validator tests (unknown input, wrong kind, `$` outside a LibraryRef, bad default, bad passed value, pass-through);
  runtime test: scenario B with fakes runs Technics then Sony and locks per device; rename test.
- [x] T13.10 P6c **Editor for flow calls and inputs** (feat/m13-flow-run-editor, PR #54; 0.38.0; after T13.9 and lane A). Files:
  `Editor.ViewModels/Inspector` + `Editor/Inspector` (Run a flow inspector, flow settings Inputs, device picker groups,
  #49–#61), `Canvas` pill (called flow's glyph, title, summary, live #59, InfoBar #64), `Runs` (nested lanes under a
  bracket, Open in editor), `ShellWindow` (Open this flow with the D-047 prompt). Done when: ViewModel tests; running
  editor builds DESIGN §1 B by mouse and keyboard, runs it, shows one Runs entry with nested lanes; screenshots;
  flow-calls/DESIGN.md moved into ARCHITECTURE/UX/FEATURES and cut to its open questions (D-053 rule).

## M13b Hotkeys: more keys, and "taken" explained (user, 2026-11-03; design D-071..D-073, X-007, UX §5.7 H1–H8)
The user asked for Win+[, Win+], Win+\ and Num1, Num2…, and why keys stay unassignable after another app stopped.
Cause, found on the dev PC: Win+[ ] \ were refused by `Hotkey.TryCreate`, and the recorder's `Rejected` event had
no listener, so nothing happened. Win+[ and Win+] are also held by `C:\Projects\Autohotkey\SwitchDisplay.ahk`,
still running. A failed chord was retried only on the next reload, and recording the same chord again doesn't
reload. The engine's own chords never reach the recorder. Order: T13b.1 first, then T13b.2 and T13b.3 in either
order (they touch different files). Can run alongside M13 (no shared files except the docs).
- [x] T13b.1 **Model.** `Core/Input/Hotkey.cs` (names, `TryCreate`: punctuation VKs, Num0–Num9 alone, Shift+Num
  refused; `ToString`; `TryParse` accepts "Numpad1"), a `Rejection` reason enum for the recorder, the new
  `Core/Input/WindowsShortcuts.cs` (Windows 11's own Win+key list), and the `ConfigValidator` message ("use Win,
  Ctrl or Alt plus a letter, number, F1–F24 or a key like [, or a number pad key like Num1"). Tests:
  `Core.Tests` HotkeyTests (parse/format every new key, both directions; `Win+\` and `Win+[` YAML round trip
  through `ConfigYaml`; letters alone still refused). Branch `feat/m13b-hotkey-keys`. Done when: tests green,
  0 AOT/trim warnings, the engine registers `Win+\`, `Ctrl+Num1` and `Num5` from a hand-written config.yaml
  (manual check on the dev PC, with the shortcuts removed afterwards). Done (feat/m13b-hotkey-keys): the
  reason enum is `HotkeyRejection` (`Hotkey.Check` never returns `NumLockOff`: the recorder sets it); manual check
  2026-11-03 passed (an outside `RegisterHotKey` got 1409 for all three while the test engine ran, free after).
- [x] T13b.2 **Recorder.** `Editor/Inspector/HotkeyRecorder.xaml.cs` (`Rejected` carries the reason; Num Lock off =
  a pad navigation key with `IsExtendedKey` false → H3; modifiers let go with no key → H7),
  `Editor.ViewModels/Inspector/InspectorViewModel.cs` + the inspector XAML (the reason under the box, the H8 tip
  for a number-pad key alone). Branch `feat/m13b-recorder`. Done when: ViewModel tests for each reason → its text;
  screenshots of H1, H3 and H8 (light and dark); Narrator reads the note. Done (feat/m13b-recorder): `HotkeyRejection`
  gained `NothingCameThrough` (H7, recorder only); texts in `Editor.ViewModels/Inspector/ShortcutTexts`; a refused
  key's reason wins over the engine problem until the next recorded key; the recorder raises a UIA notification
  (`HotkeyRecorderNote`, seen by a UIA listener for H1 and H3). The tip isn't announced: recording rebuilds the rows.
- [x] T13b.3 **Engine.** Pipe op `record-hotkey` (`Ipc/Protocol.cs`, `Runtime/Control/EngineController`, editor
  engine client, `HotkeyRecorder.RecordingChanged` → op); `Runtime/Triggers/HotkeyTable` (recording flag apart from
  Pause; `RetryFailures()`, called when recording ends, on Resume and when the tray menu opens; a
  `shortcutsChanged` event when the failures change, and the inspector refreshes on it);
  `Engine/Shell/GlobalHotkey` (1409 → H5 when on `WindowsShortcuts`, else H6); `EngineHost` and `TrayIcon` wiring.
  Branch `feat/m13b-engine-hotkeys`. Done when: `Runtime.Tests` with the fake registrar (recording unregisters and
  re-registers all; a chord that failed and is free now is registered after recording ends, after Resume and when
  the menu opens; a dropped connection ends recording); the editor can record a chord the engine already owns;
  `scripts/measure.ps1` idle numbers unchanged. Done (feat/m13b-engine-hotkeys): the editor sends the op from
  `InspectorPane`'s `RecordingChanged` (no `HotkeyRecorder` change). Live 2026-11-04 (scratch engine, outside
  `RegisterHotKey` probes): the chord is free while the box has focus and held again after; a chord held by
  another app at start (H6) is registered once let go and recording ends; a dropped connection ends recording; the
  editor recorded Ctrl+Alt+F9 that the engine owned (clash note shown, the flow didn't run). Idle 5.9 MB / 22.2 MB / 0 ms.

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
PRs bumped the minor version (until D-079; PRs no longer bump it).
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
After M13: a menu's `wait: true` nests (D-061) and its loops share M13's call graph (D-063); `ParamSpec.RefersTo`
comes from T13.8.
Order: T12.1 and T12.2 (spikes, gates) first, either order; then T12.3 → T12.4 → T12.5, T12.6, T12.7 → T12.8 →
T12.9 → T12.10. One PR per task unless two share a branch; no version bumps (D-079). Each task moves what it
built from DESIGN.md into ARCHITECTURE/UX (D-053).
- [x] T12.1 Spike S1: controller input in AOT (spike/gamepad-input, PR #58, DESIGN §5). Done when: gate (a)–(g)
  answered on the dev PC with the user's Xbox pad (ask the user to plug it in; Steam on and off); D-056 set to
  accepted with the backend and numbers; Footprint rows added; probe deleted or left in `spikes/` untracked by the
  solution. Done 2026-11-04 with the Steam Controller (no Xbox pad that day): XInput accepted (D-056, X-011); the
  probe stays in `spikes/GamepadProbe` (`gi`/`xi`, `--keys`, `--raw`, `--quiet`). Moved to T12.6: S1 (c) and (d),
  and sleep/resume, with an Xbox pad.
- [x] T12.2 Spike S2: no-activate menu window over apps and games (spike/menu-window, DESIGN §5). Done when: gate
  (a)–(e) answered with the throwaway D3D11 app in three modes, screenshots of the menu over each; D-055 updated
  with what held; a failed (a) stops M12 and goes to the user.
  **Result 2026-11-04: (a) failed for mode 2.** Borderless works. Any window we show knocks a DXGI full-screen
  game (FSO too) out of full screen, X-010. (b), (c) and (e) hold; (d) is partly tested (one monitor). The user
  decided (2026-11-04): **borderless only** (D-055 accepted). QUNS = 3 → #32, logged, nothing shown. M12 goes on:
  T12.1 next, then T12.3 onward.
- [x] T12.3 Core: `menu.show` descriptor, `GamepadChord`, `Flow.Gamepad`, `ParamSpec.RefersTo = Flow`, validation
  (feat/m12-core, #61, DESIGN §2.5, §3.5). Done when: YAML round-trip of DESIGN §2.1's flow; chord parse/format tests
  (order, `+` spacing, Guide/one button/5 buttons refused); validator tests for own flow, unknown and duplicate item,
  `wait` loop, 0/13 items, `closeAfter` range, `timeout` ≤ `closeAfter`, chord clash (later flow); a flow rename
  rewrites menu items (Core edit + test); catalog test still passes (items has Suggest).
- [x] T12.4 Runtime: `MenuProvider`, `IMenuPresenter`, `IFlowStarter` in `EngineController`, `StepContext.SetSummary`,
  `StepEndsFlowException` + `RunFinished.Quiet`, resource `menu`, dry run and test-step (feat/m12-runtime, DESIGN
  §3.2). Done when: Runtime.Tests with a fake presenter: pick launches with trigger "Menu · Couch" and summary; ignore
  while running; `wait` takes a failed child's outcome; nothing picked → node and flow Cancelled, quiet, `always`
  edges don't run; two menus serialize on the presenter gate; a `wait: true` parent picking a submenu flow shows the
  child's menu (gate released before the launch) and takes its outcome; "Back" to a parent that is still running
  follows its `whenRunning`; dry run doesn't show; test-step returns the pick without a run.
- [x] T12.5 Runtime: pure `MenuModel` + `GamepadChordRecognizer` + `GamepadTable` (feat/m12-runtime, PR #60, DESIGN §2.4,
  §3.4, §4.3). Done when: tests with `ManualTime`: list/grid moves (no wrap, 2D), repeat 400/120 ms, stick hysteresis,
  released-since-open, 1–9, close timer restarts on input and ends as cancel, last pick focused first, Steam dedupe
  (key within 100 ms of the same pad move dropped); chord fires on first release only for the exact set, never inside
  a longer chord; table diff on reload, Pause, `record-gamepad` pausing chords.
- [x] T12.6 Engine: `GamepadSource` (S1's backend) + `DeviceArrival` + suspend/resume, loaded only when needed; ops
  `devices gamepads`, `record-gamepad`; `dctl devices gamepads` (feat/m12-gamepad). Done when: unit tests on the seams
  (no chord → never started; last pad gone → stopped; `record-gamepad` with no chord in the config starts the source,
  records the first chord and releases it on answer, timeout and client gone; `devices gamepads` lists a pad with no
  chord configured); on the dev PC a chord on a test flow runs it with another app
  in front and history says "Controller · View + Menu"; `dctl devices gamepads` lists the pad; footprint per DESIGN
  §3.8 measured (no chord; chord + no pad; pad on) and recorded; S1's leftovers: pad on and untouched with the pad
  kept awake ≤ 300 ms over 10 min (S1: ~410 ms at 20 Hz, so 10 Hz while only chords listen, D-056), reads under a
  full-screen game, sleep/resume only with the user's OK.
  **Done 2026-11-04 (#68), live pad legs deferred to T12.10:** seams and ops unit-tested (`GamepadServiceTests`), `devices gamepads`, `record-gamepad` and the menu's pad feed wired, no-pad footprint rows recorded (ARCHITECTURE → Footprint). No pad was powered during the session, so NOT run live: a real chord running a flow ("Controller · View + Menu"), the menu driven by a pad, pad-on CPU at 10 Hz, reads under a full-screen game, sleep/resume. T12.10's acceptance does them with the user at the pad.
- [x] T12.7 Engine: `AcrylicSurface` cut from `RunPopup`, `FlowMenu` (list, grid, large, focus visual, hint row,
  close countdown, empty state), `MenuKeys`, mouse hook, placement, full-screen check, animation and high contrast
  (feat/m12-menu-window, DESIGN §3.3, §4). Done when: RunPopup unchanged (screenshots before/after); screenshots of
  the menu light/dark/high contrast, list and grid, normal and large, empty state, at 175 % scale; `dctl run` of a
  menu flow navigated by keys, mouse and the pad on the dev PC; foreground window unchanged; keys free after each
  close path (a test app registers Up); footprint after 10 menus within D-036. The menu must check
  `SHQueryUserNotificationState` before drawing or taking keys: QUNS = 3 (exclusive mode or FSO) → #32 in history
  and one log line, no window, no pop-up (D-055, X-010); a unit seam fakes QUNS for the test.
  **Done 2026-11-04 (#65), pad navigation deferred to T12.6:** `IMenuPadFeed` seam, the model's pad path is tested in T12.5; the pad leg of the live `dctl run` check and "Controller" hint row live-look wait for T12.6. Not built: open scale, tile scale, pick flash (polish). Footprint: flat, +6.3 MB after 10 menus, ~1.8 MB over D-036's line, absolute budget holds (ARCHITECTURE → Footprint).
- [x] T12.8 Editor: `menu.show` inspector (flow token list, title, layout, size, close after, wait), Test shows the
  menu, "Controller buttons" recorder, "Controller menu" template, trigger texts (feat/m12-editor, DESIGN §3.7). Done
  when: VM tests (add/remove/reorder items, own flow not offered, missing flow chip, recorder states incl. engine off
  and no pad, template content); screenshots light/dark of the inspector and the recorder; Narrator names on the
  token list and recorder. Done (PR #63): recorder codes to DESIGN §3.6 until T12.6 lands `record-gamepad`; Layout and Size are drop-downs, not drawn radios.
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
- [x] C3 Wrap long simple chains, keep complex branches apart (user, 2026-11-03; design D-060, branch feat/canvas-wrap, 0.23.0).
  `FlowLayout` wrap pass for spine runs > `WrapAfter` (5), `EdgeCurve` wrapped-edge shape + hit-test, canvas draws it,
  fit/zoom from wrapped bounds, Right/Left across a row break. Done when: `FlowLayout` tests — a 10-step chain gives
  rows of 5/5 and the next step after the block; a chain beside a parallel branch doesn't wrap; a fork whose long arm runs beside a direct edge to its join doesn't
  wrap; a join never starts a run; a fork/join flow is
  unchanged; a card inside a chain counts as one box; wrapped-edge hit-test; drag-reorder and Tidy up on a wrapped
  flow keep `nodes` as before; screenshots light/dark at 175 %.

## Library fixes (user reports)
- [x] L1 Edit Android inputs in Library › Android (user, 2026-11-03): add, rename (refs follow), remove (off while used),
  moves as an ordered Up/Down list; the `android.input` hint "Add inputs in the Library." becomes a link there.
  Done (D-078; feat/l1-android-inputs, 0.39.0): `AndroidInputEdits` (Core), the card's Inputs panel, the inspector
  link (`ShellViewModel.LibraryRequested`). Live-checked on a scratch config: add, moves, rename (the step followed),
  save; the link's nav-pane hop is covered by a view-model test only (selecting a step needs the foreground).
