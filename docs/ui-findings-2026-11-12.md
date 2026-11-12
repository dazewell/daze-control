# UI findings 2026-11-12 (M22 input)

Live test of the editor on the dev config (`scripts/dev-run.ps1 -Sample showcase` and `-Sample big`), build = main at `ca1a432`
(with PR #199 for the bound-device input). Window 3768×2100 px on a 4K screen at 200 % scale, dark theme; light theme glanced at.
Tester: Sonnet 5.5 agent. **No computer-use tool was available**, so input was real mouse/keyboard via PowerShell `SendInput`
(left drag with 15 intermediate moves, wheel, right click, SendKeys), screenshots via `CopyFromScreen`, UI Automation dumps
and `dctl`. Right-drag/middle-drag/Space-drag, touchpad pan/pinch, 225 % text scale and display-scale changes were **not**
exercised. No audio, Bluetooth or display device was changed (no reliable way to mute and restore them in this session), so the
Library "Test" buttons, Set default device, Bluetooth connect and screen steps were not run against real devices.

**Coverage:** done = Flows list (search, empty search, context menu, drag reorder, run, Stop, prompts), Run-result banners,
canvas (select, marquee, library drag-drop, step drag, connect, Undo/Redo, copy/paste, delete, wheel zoom, Fit, edge auto-pan,
Layout menu in Tree / Left→right / Top→bottom / Circular), Outline, Problems flyout, Library (all 6 tabs, Add by hand, Bind
devices, rename, delete-in-use), Runs, Settings, inputs incl. Bound device kind, Pick device step, window resize 400–1900 DIP,
light theme, UIA names, `dctl`. **Not done:** join-layout case, per-shape pixel checks of "no line through a step", right/middle/Space pan,
pinch, text scale, tray folders/hotkeys UI, 12 PC-state check steps' inspectors one by one, keyboard-only pass beyond Tab
order, History run-detail page, high contrast, 100+ steps editing. A follow-up round should cover those (see batch 9).

Severity: **bug** = wrong or broken; **inconsistency** = contradicts the app's own pattern or a platform convention;
**missing** = expected by comparable tools; **polish** = cosmetic. Size S ≤ 1 h, M ≤ half a day, L = a day or more.

## Ranked findings

| ID | Area | What happens | Expected / why | Sev | Repro | Suspected files | Size | Model |
|----|------|--------------|----------------|-----|-------|-----------------|------|-------|
| F-001 | Canvas · auto-pan | Dragging a step and resting the pointer a few DIP from the canvas right edge for ~2 s panned the view by more than 1500 DIP, the whole flow scrolled out of view; the drop landed half under the inspector. No cap, no easing | Miro/Figma pan at a capped speed (≈ 600 DIP/s) that ramps with distance into the edge band, and stops when content is exhausted; a drop zone under another pane is refused | bug | Showcase › Desk setup (simulated) › Canvas. Drag "Show message / Evening desk mode" to x = canvas right edge − 5 px, hold 2 s, release | `Canvas/FlowCanvas.Camera.cs`, `Canvas/FlowCanvas.Pointer.cs` (grep `AutoPan`) | M | Sonnet |
| F-002 | Inspector · multi-select | Marquee or Ctrl+A over everything shows a blank-ish inspector: header "95 steps and 5 cards selected" and only the "If it fails" group with "Mixed", while the spec (UX §5.9 M15) says Settings shows shared params; and nothing says which actions apply (delete, group, copy) | A selection summary with count by type and the safe bulk actions; for huge selections skip per-param work | inconsistency | Probe 100 › Canvas, click empty canvas, Ctrl+A | `Inspector/InspectorPane.xaml.cs`, `src/DazeControl.Editor.ViewModels/Inspector/InspectorViewModel.cs` (grep `selected`) | S | Sonnet |
| F-003 | Canvas · Delete | Ctrl+A then Delete removes all 95 steps with no confirmation and no toast; recoverable only by Ctrl+Z (works) | Large destructive deletes in editors (Figma, Miro) show an "n items deleted · Undo" toast; or a confirm above a threshold | missing | Probe 100 › Ctrl+A › Delete | `Canvas/FlowCanvas.Keys.cs` | S | Sonnet |
| F-004 | Palette · a11y | UIA Name of every palette group header is the C# record text `PaletteGroup { Area = Audio, Items = <>z__ReadOnlyList`1[...` (Narrator reads it) | Group name = "Audio" | bug | `pwsh` UIA dump of the Steps panel, or Narrator | `ShellWindow.xaml` (palette ItemsControl/ListView template), `src/DazeControl.Editor.ViewModels/Canvas/StepPaletteViewModel.cs` (add `ToString()` or `AutomationProperties.Name` on the group header) | S | Sonnet |
| F-005 | Flows list · small window | At ≤ 800 px (≈ 400 DIP) wide the cards lose name/toggle/Run off the right edge with no scroll or reflow; the window has no minimum size. In the editor at ~750 DIP the canvas gets ~25 % of the width because palette (≈150 DIP) and inspector (≈190 DIP) never collapse; toolbar overflow to "…" works well | WinUI apps set `MinWidth` (≈ 500 DIP) and collapse side panes (Steps auto-hides under ~900 DIP) | bug | Resize the editor to 800×700 px; open a flow | `ShellWindow.xaml(.cs)`, `WindowPlacementInterop.cs`, flow list XAML in `ShellWindow.xaml` | M | Sonnet |
| F-006 | Canvas · Fit & zoom range | Zoom is clamped at 50 %–200 %. Fit on a 95-step flow stays at 50 % and cuts off the right ~15 % (also the initial view of Probe 100 opens clipped) | Miro/Figma/Power Automate zoom out to 5–10 % and Fit really fits everything; initial view should be fitted | bug | Big sample › Probe 100 › Fit | `Canvas/FlowCanvas.Camera.cs` (grep `MinZoom`, `Fit`) | S | Sonnet |
| F-007 | Library · bound devices | Rename flyout on a bound device edits the internal slug (`dev-buds-fake`) while the row and the Bind dialog show the display name "Dev Buds (fake)"; the subtitle repeats the name ("Dev Buds (fake) + Dev Headset (fake)"); validation "Pick a Bluetooth device." stays after a device was picked; combos have no placeholder text | One consistent name; validation clears on change; "Choose a Bluetooth device" placeholder | inconsistency | Library › Bound devices › Bind devices › pick BT, pick audio, Bind; click the rename icon | `Library/LibraryView.xaml(.cs)`, `src/DazeControl.Editor.ViewModels/` (grep `Bound`) | S | Sonnet |
| F-008 | Inputs · Bound device kind | New "Bound device" input shows the placeholder "Default device" and the error "Bound device needs a default device." although for this kind a default is required (not a system default); no hint or link when the Library has no bound devices | "Choose a bound device"; empty-state line "Bind devices in Library › Bound devices" with a link | polish | Flow › Inputs › Add an input › Kind = Bound device | `Inspector/InspectorPane.xaml.cs`, `InspectorViewModel.cs` (grep `needs a default`) — text likely in `ConfigValidator.cs` | S | Sonnet |
| F-009 | Inspector · Add an input | A new input row is appended at the very bottom of the inspector, partly clipped; the panel does not scroll it into view | Scroll the new row into view and focus its name box | polish | Pick a device (fakes) › Add an input | `Inspector/InspectorPane.xaml.cs` | S | Sonnet |
| F-010 | Library · long names | A 120-char Audio item name is cut mid-word with no ellipsis ("behavic"), pushing "Not used yet" tight against it; the same row layout is used on all tabs | `TextTrimming=CharacterEllipsis` + tooltip with the full name | polish | Library › Audio › Add by hand › type 120 chars | `Library/LibraryView.xaml` | S | Sonnet |
| F-011 | Library · gaps | Audio and Bluetooth items cannot be renamed (layouts, Android and bound devices can); the Programs tab has no "Add by hand" while the others do; a disabled delete explains itself only in a hover tooltip ("Used by 1 flow. Remove it from that flow first.") | Consistent verbs across tabs; the reason should also be in the UIA name/description and reachable by keyboard | inconsistency | Library tabs | `Library/LibraryView.xaml(.cs)` | M | Sonnet |
| F-012 | Run counters | Editor toolbar says "Running · 18 of 20" while the pop-up says "18 of 21"; later 70 of 71 vs 73 of 75; totals grow while running (nested/parallel steps added late) | One number, from one source; a moving total makes the bar jump backwards | bug | Probe 100 › Run, compare toolbar and pop-up | `Runs` view-model (grep `of {`), `src/DazeControl.Runtime` progress events, `Popup` presenter | M | Opus |
| F-013 | Runs page | "0 s" wraps leaving an orphan "s" on its own line ("… · 20:54 · 0\ns"); the detail pane's empty state is a tiny line top-left of a 1900-DIP-wide area; list column stays ~300 DIP | Non-breaking space between number and unit; centred empty state with an icon like the Flows/Library ones | polish | Runs page with event runs | `Runs/RunsView.xaml(.cs)`, formatter for durations (grep `" s"`) | S | Sonnet |
| F-014 | Flows page · narrow | At ~650 DIP the search box shrinks to ~35 DIP ("Search" clipped) while Sort/Show/Make this my order/Select keep full size; card subtitle "No shortcut · 17 steps ·" is clipped before "No recent run" with no ellipsis | Wrap the toolbar to two rows below ~900 DIP; trim card text with ellipsis | polish | Resize to 1300×900 px on the Flows page | flow-list XAML in `ShellWindow.xaml` | S | Sonnet |
| F-015 | Canvas · Layout menu | The Layout flyout changes width and rows with state ("Arrange again" only after hand placement), the current shape's radio only shows after arranging, "Reset layout" vs "Arrange again" are unclear; Tree/Left→right/Top→bottom/Circular all rendered and glided; an orphan step (no edges) was placed far to the right under the inspector and the view did not refit | A stable menu; shapes show the current one; after Arrange the camera brings everything into view | inconsistency | Canvas › Layout › each entry | `Canvas/FlowCanvas.Menus.cs`, `Canvas/FlowCanvas.Motion.cs`, layout code in `src/DazeControl.Editor.ViewModels/Canvas` | M | Sonnet |
| F-016 | Canvas · view state vs Cancel | Layout choice and moved positions survive **Cancel** (per-PC view state) while Cancel discards every flow edit; after layout-only edits Undo is enabled but Save/Cancel are disabled; users cannot tell what Cancel reverts | Either layout changes are part of the dirty state or the Cancel tooltip/confirm says what it keeps | inconsistency | Move a step, Layout › Top→bottom, Cancel | `Canvas/*`, `ShellWindow.xaml.cs` (grep `CancelButton`) | M | Opus |
| F-017 | Editor · Cancel | Cancel discards 6 unsaved edits instantly, no confirmation, Undo history cleared | Office/VS Code ask "Discard changes?" with a count, or Cancel = Undo-all that is itself undoable | missing | Edit a flow 6 times › Cancel | `ShellWindow.xaml.cs` | S | Sonnet |
| F-018 | Problems flyout | Problem text leaks a parameter key: "Volume (%) is missing (percent: …)" | Plain wording per UX ("no jargon") | polish | Add Set volume with an empty level › Problems | `src/DazeControl.Core/Validation/ConfigValidator.cs` (grep `is missing (`) | S | Sonnet |
| F-019 | Outline view | Flat list only; no "Waits for …", no Failure-ignored or problem marker, uses only a narrow column of a wide area; UX §5.9 says the outline lists every node with "Waits for …" | Show dependencies and state per row; a tree grouped by order | inconsistency | Canvas › Outline | outline view in `ShellWindow.xaml` / `Canvas/*` (grep `Outline`) | M | Sonnet |
| F-020 | Keyboard · focus | Tab from the canvas goes to the nav rail's "Flows" item (focus ring + tooltip) not to the inspector (UX §5.9 says palette → canvas → inspector); a focused node showed only its port dots, no visible focus ring | Predictable order; a visible 2-px focus visual on the focused node | bug | Open a flow, click empty canvas, press Tab twice | `Canvas/FlowCanvas.Keys.cs`, `NodeViews.cs`, `ShellWindow.xaml` (TabIndex / KeyboardNavigation) | M | Sonnet |
| F-021 | Flows card · a11y | UIA: the button named "Run Run others" repeats the verb for a flow called "Run others"; the link chip on a card is named by the flow ("Failure lab") rather than "Used by 2 flows: show them" | Names that describe the action | polish | UIA dump of Flows page | `ShellWindow.xaml` flow-card template | S | Sonnet |
| F-022 | Flows card menu | Context menu lacks Rename (F2 exists on the canvas), and shows no accelerator hints; "Open" is first but double-click/Enter also opens | Add "Rename", show shortcuts (Alt+↑/↓) as accelerator text | missing | Right-click a flow card | flow-list code in `ShellWindow.xaml.cs` (grep `Move to`) | S | Sonnet |
| F-023 | Flows list · drag | While dragging a row the translucent ghost's text overlaps the target row's text and a stray "Move" label shows; works and Undo restores | Drag preview must not overlap content (offset it, lower opacity) | polish | Flows (big sample) › drag a row onto another | flow-list drag code, `FlowRowTemplateSelector.cs` | S | Sonnet |
| F-024 | Light theme | Step pills have almost no contrast against the canvas (no border), "Failure ignored" chip loses its pill shape, ports are faint; theme switches live (good) | WCAG 3:1 for control boundaries; chips keep a border | bug | Set Windows app mode to Light, open any flow | `Canvas/NodeViews.cs`, theme dictionaries in `App.xaml` | M | Sonnet |
| F-025 | Settings | No Appearance/Text size/Animation section; the editor only follows Windows (UX needs "Animation effects off" support, tested via OS only) | At least a link "Change in Windows settings" next to each, as Windows 11 apps do | polish | Settings | `ShellWindow.xaml` (settings page) | S | Sonnet |
| F-026 | dev-run tooling | `dev-run.ps1 -Sample X` closes only editors on X's config, so a previous sample's editor stays open while the engine switches (two editors, one engine) | `Stop-Editors` should close all dev-config editors (`dev\*.yaml`) | bug | `dev-run -Sample showcase` then `-Sample big` | `scripts/dev-run.ps1` (`Stop-Editors`) | S | Sonnet |
| F-027 | Samples + toasts | The showcase samples' Show message steps raise Windows toasts on every run (sound depends on the user's notification settings) | Run notices that are "silent" for dev samples, or a `silent: true` | polish | Run any showcase flow | `samples/dev-*.yaml`, toast provider in `src/DazeControl.Providers` | S | Sonnet |
| F-028 | Ask prompt | Yes/No prompt opens with the **last** choice ("No") highlighted and Enter runs it; the 3-choice prompt highlights the first | Same rule for both; for Yes/No the safe default (first) | inconsistency | Showcase › Ask then branch › Run | menu/prompt model (grep `Enter Run`) in `src/DazeControl.Runtime` / `Providers` | S | Sonnet |
| F-029 | Pick device step | "Default" is a free-text box ("Optional", a Library name typed by hand) while the other device params use pickers | Library picker filtered by the Pick kind | inconsistency | Pick device step › Settings | `Inspector/InspectorPane.xaml.cs` | S | Sonnet |
| F-030 | Canvas · drag edges | Dragging an existing step above the top of the flow clamped at the top of content while the same spot accepted a library drop; a dropped step overlapped a neighbour by ~8 px and sat off-column (84 % zoom) | UX §5.9: "After any drop: columns aligned, gaps 64/16, no overlap" | bug | Morning check › drag "Check internet" up beyond the first row | `Canvas/FlowCanvas.Pointer.cs`, snap/gap code in `src/DazeControl.Editor.ViewModels/Canvas` | M | Opus |
| F-031 | Memory | Editor working set 311 MB after opening a 95-step flow (editor is not under the engine's 35 MB budget but is on-demand) | Record a baseline; watch for growth when switching flows | polish | Task manager after opening Probe 100 | n/a (measure) | S | Sonnet |

**Worked well (keep):** library drop lands at the pointer; connect drag shows a live line and snaps to the port; marquee selects by
intersection; Ctrl+C / Ctrl+V pastes with offset and reflow; Undo/Redo everywhere; run overlay on the canvas with dimmed inspector and
the InfoBar "You can edit again when this run finishes"; result banner "Done, with a problem …" with the offending step; Layout
shapes glide; palette search with "No steps match."; flow search with "No flows match." + Clear; flows list reorder with insertion
line and Undo; wide-window toolbar overflow; `dctl run --wait` prints per-step results; UIA names on canvas nodes include
"Waits for …" and Failure ignored; every library delete button has a descriptive UIA name.

## Files that overlap (do not run these in parallel)

- `Canvas/FlowCanvas.Camera.cs` + `FlowCanvas.Pointer.cs`: F-001, F-006, F-030 (and F-015 camera refit).
- `Canvas/FlowCanvas.Keys.cs`: F-003, F-020.
- `ShellWindow.xaml(.cs)`: F-005, F-014, F-017, F-020, F-021, F-022, F-023, F-025.
- `Library/LibraryView.xaml(.cs)`: F-007, F-010, F-011.
- `Inspector/InspectorPane.xaml(.cs)` + `InspectorViewModel.cs`: F-002, F-008, F-009, F-029.
- `Canvas/NodeViews.cs`: F-020 (focus ring), F-024.
- `ConfigValidator.cs`: F-008 (text), F-018.

## Proposed plan (M22 UI polish)

Each batch touches a disjoint set of files and fits one session. Order = value. All Sonnet unless marked.

1. **Batch 1 · Canvas camera and drop correctness** (F-001, F-006, F-030, camera part of F-015). Files: `Canvas/FlowCanvas.Camera.cs`, `FlowCanvas.Pointer.cs`, motion. **Opus** (touches live drag invariants D-119/D-120; add tests in the scene model).
2. **Batch 2 · Destructive actions and selection** (F-003, F-017, F-002, F-016 text). Files: `FlowCanvas.Keys.cs`, `InspectorViewModel.cs`, `ShellWindow.xaml.cs` Cancel handler.
3. **Batch 3 · Window and layout responsiveness** (F-005, F-014, F-023). Files: `ShellWindow.xaml(.cs)`, `WindowPlacementInterop.cs`.
4. **Batch 4 · Accessibility and keyboard** (F-004, F-020, F-021, F-022). Files: `ShellWindow.xaml`, `StepPaletteViewModel.cs`, `NodeViews.cs`. Run after batch 3 (shared `ShellWindow.xaml`).
5. **Batch 5 · Library consistency** (F-007, F-010, F-011). Files: `Library/LibraryView.*`.
6. **Batch 6 · Inputs and Pick device** (F-008, F-009, F-029). Files: `InspectorPane.*`.
7. **Batch 7 · Light theme and Settings links** (F-024, F-025). Files: `NodeViews.cs` styling, `App.xaml`, settings page. After batch 4.
8. **Batch 8 · Runs and messages** (F-012 Opus, F-013, F-018, F-019, F-028). Files: `Runs/*`, runtime progress, validator text, Outline view.
9. **Batch 9 · Second live round** (tester session, no product code): join layout (Check connection / Connect → Set default device), right/middle/Space pan, pinch, 225 % text, high contrast, tray folders and hotkeys, 100+ step editing, History run detail, keyboard-only pass, the 12 PC-state checks' inspectors, Library "Test" buttons on real devices with the user present. Also F-026/F-027/F-031 (tooling and samples) as a tiny side PR.

## Restore verification (done at the end of this session)

Nothing was changed on the PC beyond the dev config, the Windows app theme (switched to Light and back to Dark, `AppsUseLightTheme`
read back = 0) and window placement of the dev editor. Verified after `dev-run.ps1 -Restore`: installed engine
`%LOCALAPPDATA%\DazeControlApp\current\DazeControl.exe` running (pid 25048), no dev engine or editor left, audio endpoints and the
display layout identical to the start snapshot (one external 3840×2160 at (0,0)), no Bluetooth or default-device changes were made.
The dev `showcase.yaml` was reset to the sample (the "Zzz test headset" library item removed); `big.yaml` has a reordered flow list
(harmless dev file).

---

# Round 2 (2026-11-12, night; tester Sonnet 5.5)

Build = main `68c6df4` (batches 2-8 merged, PRs 201-207) plus the F-026 fix (PR 208). Dev config only (`dev-run -Sample showcase`, then `big`), window 2400×1500 px at 175 % scale, dark theme (light switched on for a check and back). No computer-use tool again: SendInput helper + UIA + screenshots. **UIA bounding boxes of canvas nodes do not follow the camera** (stale after pan/zoom), so canvas checks used pixels. Audio, Bluetooth and display devices were **not changed** (no need arose; the Library "Test" buttons and Set default device were not run). Not done: 225 % text scale and high contrast (both are Windows system settings, off limits for an unattended run), touchpad pinch (Ctrl+wheel only), tray menu folders (tray not reachable by UIA), hotkey firing (capture box only), live drag/auto-pan items (F-001, F-030: no PR claims a fix).

## Regression table (F-001..F-031)

| ID | Status | Note |
|----|--------|------|
| F-001 | fixed in code (T22.1, D-126), not re-tested live | Auto-pan 600 DIP/s max, stops one column past the still steps; a release over the inspector/palette is no drop. Unit tests only (agents can't drag) |
| F-002 | **still broken** | hint exists (`ManyHint`) but never shows: see F-032 |
| F-003 | fixed | Ctrl+A, Del: "Delete 18 items?" with Delete / Cancel |
| F-004 | fixed | palette groups named "Audio", "Bluetooth", … in UIA |
| F-005 | partly | minimum size holds (640 DIP), Steps pane hides, wrapped toolbar; at the minimum the canvas still gets ~45 % (F-039) |
| F-006 | fixed in code (T22.1, D-126), not re-tested live | Zoom 10–200 %; Fit and the opening view fit the whole flow (the canvas is its own column: the inspector never covers it) |
| F-007 | partly verified | combos have placeholders, rename icon on Bluetooth and bound rows; the Bind error clearing was not exercised |
| F-008 | partly verified | Bound device input exists with a "Bound device: default device" picker; the empty-state link not exercised |
| F-009 | fixed | the new input row scrolls into view and "Input name" has focus |
| F-010 | not re-tested | long names |
| F-011 | partly | Bluetooth rename icon and the disabled Remove's reason in its name seen; audio rename and Programs "Add by hand" skipped by design |
| F-012 | not fixed, not re-tested | Opus work left for the user |
| F-013 | fixed | duration kept on one line; the centred "Pick a run" state not looked at closely |
| F-014 | fixed | toolbar wraps at the minimum width |
| F-015 | fixed (menu) | Layout menu is now the same every time: Arrange › (Left to right, Top to bottom, Tree, Circle), Wrap long chains, Spacing, Line up, Lines, Untangle lines, Reset layout; refit after Arrange: T22.1 (Arrange and Reset layout fit the camera; not re-tested live) |
| F-016 | text only, as claimed | Cancel dialog says layout stays |
| F-017 | fixed | "Discard 1 change to Morning check?" Discard / Keep editing |
| F-018 | not re-tested | |
| F-019 | fixed | Outline rows show "Waits for … · Failure ignored" |
| F-020 | partly | Tab order canvas → inspector works; one extra stop on the title bar (F-038); arrow keys select neighbouring steps; focus ring not judged |
| F-021 | partly, as claimed | "Used by N flows" chip named; the on/off switch on a card is named only by the flow (F-041) |
| F-022 | not re-tested | Rename in the card menu skipped by design |
| F-023 | not re-tested | |
| F-024 | fixed (steps); **new bug** | pills, chips and edge labels have visible outlines in light; the window caption buttons are nearly invisible (F-034) |
| F-025 | fixed | Settings › Appearance with three "Change … in Windows settings" links |
| F-026 | fixed (PR 208) | switching `-Sample showcase` → `big` left exactly one editor |
| F-027 | not fixable in samples | the notice is a tray balloon whose sound is the OS default: F-037 |
| F-028 | by design | the highlighted answer is the step's `default` ("No" with `default: no`); Up + Enter picked the other answer without stealing focus |
| F-029 | not fixed (skipped) | design pass needed |
| F-030 | fixed in code (T22.1, D-126), not re-tested live | A move goes above/left of the flow (origin shift, as a library drop); the lead snaps to a column within 32 DIP and keeps 64/16 gaps, preview == commit |
| F-031 | baseline recorded | editor 235 MB just started, 331-342 MB with the 95-step flow open; growth in F-036 |

Counts: fixed 11 (F-003, F-004, F-009, F-013, F-014, F-015, F-017, F-019, F-024, F-025, F-026), partly 6 (F-005, F-007, F-008, F-011, F-020, F-021), still broken 2 (F-002, F-006), left or by design 6 (F-001, F-012, F-016 text only, F-027, F-028, F-029, F-030), not re-tested 4 (F-010, F-018, F-022, F-023).

## Checks that passed (keep)

- **Join layout** "Check connection / Connect → Set default device" (and a 6-step wide join, three in → one): in Left to right, Top to bottom, Tree and Circle no line crosses a step; the long edge is routed over or around the middle step. Only polish: F-040.
- 12 PC-state checks (internet, cable, Wi-Fi, metered, VPN, power, locked, idle, free space, file, program, time): every inspector renders its parameters, help line and failure policy; "Test this step" present.
- Run / Stop from the Flows list ("Running" dot, "Stopped just now"), prompt pop-up (Yes/No with the `default` highlighted, Up + Enter answers without activating), run result in Runs with the timeline and per-step detail ("Check file, didn't work, the flow went on"). Pick device's "Result of: Pick device" is offered in the device picker and the bound device is listed ("Bound device").
- Library "Add by hand" opens for Audio, Bluetooth and Android (name + address with a hint); Displays has only "Capture current layout", Programs is empty with an explanation; Bound devices "Bind devices" works with placeholders.
- Hotkey capture box (Ctrl+Alt+Shift+F11 recorded, "Type it instead", Clear). Outline view with Up/Down selecting rows. Wrap long chains makes a 40-step chain readable. Wheel zoom, Shift+wheel, Ctrl+wheel (pinch stand-in) and the left marquee work. The editor picks up an outside edit of the config live.

## New findings

| ID | Area | What happens | Expected / why | Sev | Repro | Suspected files | Size | Model |
|----|------|--------------|----------------|-----|-------|-----------------|------|-------|
| F-032 | Inspector · many selected | The F-002 hint ("Del deletes them all, Ctrl+G runs them together…") never appears: between the header and "If it fails" there is a gap | Show it | bug | any flow, Ctrl+A | `Inspector/InspectorPane.xaml:491` binds `ViewModel.ManyHint` one-time (the `x:Bind` default) while the VM computes it from `IsMany`; use `Mode=OneWay` and raise `PropertyChanged(ManyHint)` with `IsMany` (`InspectorViewModel.Many.cs:32`) | S | Sonnet |
| F-033 | Canvas · pan | Right-drag, middle-drag and Space + left-drag on empty canvas did **not** pan (40 moves, identical pixels before and after); a still right click on a step opens its menu, wheel and marquee work. UX §5.2 and M13 say all three pan | Pan | bug (suspected regression of M21, or a limit of synthetic input: **verify with a real mouse first**) | Open any flow, right-drag on empty canvas | `Canvas/FlowCanvas.Pointer.cs`, `FlowCanvas.Camera.cs` | M | Opus (after a human check) |
| F-034 | Light theme · title bar | Minimize / Maximize / Close glyphs are light grey on the light title bar, almost invisible | Caption buttons follow the theme | bug | Windows app mode Light, open the editor | `ShellWindow.xaml.cs` (`AppWindow.TitleBar` button foreground and hover colours, set per theme and on theme change) | S | Sonnet |
| F-035 | Editor · reload bar | The InfoBar "config.yaml changed outside the editor" always says `config.yaml` (the file was `showcase.yaml`) and stays on every page (Flows, Library, Runs, an open flow), taking ~60 DIP | Name the real file; dismiss on page change or after a few seconds | polish | Edit the open config file outside the editor | `ShellWindow.xaml(.cs)` (grep `changed outside`) | S | Sonnet |
| F-036 | Editor · memory | Editor 235 MB at start, 331 MB with a 95-step flow, **+320 MB per Ctrl+C/Ctrl+V of all 95 steps and +140 MB per Undo**, not released when leaving the flow (1.4 GB on the Flows page after one paste and undo; UIA walks inflate it further) | Undo snapshots and node views bounded and freed with the flow | bug (perf) | Probe 100, Ctrl+A, Ctrl+C, Ctrl+V, Ctrl+Z, watch private bytes | undo stack and scene rebuild: `src/DazeControl.Editor.ViewModels/Canvas`, `Canvas/FlowCanvas.Scene*.cs`, `NodeViews.cs` | M | Opus |
| F-037 | Notices · sound | `notify` shows a tray balloon with the OS notification sound; samples and quiet use cannot silence it (F-027) | A silent balloon (`NIIF_NOSOUND`) for run notices, or a `silent` param | polish | Run a flow with Show message, Windows sounds on | `src/DazeControl.Engine/Shell/TrayIcon.cs` (`ShowBalloon`), `TrayNotifier.cs` | S | Sonnet |
| F-038 | Keyboard | From the canvas the first Tab lands on the title bar, then the inspector | Canvas → inspector directly | polish | Click empty canvas, Tab | `ShellWindow.xaml` (title bar `IsTabStop`) | S | Sonnet |
| F-039 | Narrow window | At the minimum width a card shows "Morning…" and drops "17 steps · No recent run"; in the editor the canvas is ~45 % of the width | Trim the caption, keep the name | polish | Resize to the minimum, Flows, open a flow | flow-card template in `ShellWindow.xaml`, inspector width rule | S | Sonnet |
| F-040 | Canvas · edges | After Arrange › Left to right the camera leaves the flow at ~45 % of the pane; in the wide join, Top to bottom and Tree edges run along pill borders and share lanes, so which line goes where is hard to read | Fit after Arrange; lanes ≥ 8 DIP off the pills | polish | A 3-in → 1 join flow: Arrange each shape | `Canvas/FlowCanvas.Motion.cs`, edge routing in `src/DazeControl.Editor.ViewModels/Canvas` | M | Opus |
| F-041 | A11y · small | The flow card's on/off switch is named only by the flow ("Morning check"), no state or purpose; Check time lacks the "Fails when it isn't…" hint other checks show | "Turn Morning check on/off"; same help text | polish | UIA dump; Check time inspector | flow-card template in `ShellWindow.xaml`, `ProviderCatalog.cs` (`time.check`) | S | Sonnet |
| F-042 | CI · flaky test | `TriggerServiceTests.ViaNarrowsToWifiOrCableOnBothEvents` failed once on a script-only PR (Expected "cable-out", Actual "home-out", line 327) | Deterministic ordering | bug (test) | CI run 43202837346 | `tests/DazeControl.Runtime.Tests/TriggerServiceTests.cs:327` | S | Sonnet |
| F-043 | A11y · canvas | UIA bounding rectangles of canvas nodes do not follow pan/zoom (automation tools and screen readers get the old place) | Rectangles in screen space | polish | UIA dump after panning | `Canvas/NodeViews.cs`, node automation peers | M | Sonnet |

**Not covered (needs the user or a safer setup):** 225 % text scale, high contrast, touchpad pinch, tray menu folders and firing a hotkey, Library "Test" buttons and Set default device on real devices, Bluetooth connect, the run overlay and result banner on the canvas, Pick device "Default" (F-029), live drag drop and auto-pan (F-001, F-030).

## Files that overlap (new rows)

- `ShellWindow.xaml(.cs)`: F-034, F-035, F-038, F-039, F-041.
- `Canvas/*` camera, pointer, scene: F-033, F-036, F-040 (and F-001, F-006, F-030).
- `Inspector/InspectorPane.xaml` + `InspectorViewModel.Many.cs`: F-032 only.
- `TrayIcon.cs`: F-037; test file: F-042.

## Proposed batches for the new findings

Run after Opus batch 1 (T22.1) where they share canvas files.

10. **Shell and inspector polish** (Sonnet, done: F-032/034/035/038/039/041; F-039 only narrowed, not live-checked): F-032, F-034, F-035, F-038, F-039, F-041. Files: `InspectorPane.xaml`, `InspectorViewModel.Many.cs`, `ShellWindow.xaml(.cs)`, `ProviderCatalog.cs` (help line).
11. **Silent notices and the flaky test** (Sonnet): F-037, F-042. Files: `TrayIcon.cs`, `TrayNotifier.cs`, `TriggerServiceTests.cs`. Disjoint from everything else.
12. **Canvas input, routing and automation** (Opus, after T22.1): F-033 (verify by hand with a real mouse and touchpad first), F-040, F-043. Files: `Canvas/FlowCanvas.Pointer.cs`, `Motion.cs`, `NodeViews.cs`.
13. **Editor memory and run counters** (Opus): F-036 and F-012. Files: undo stack, scene rebuild, runtime progress events.
14. **Human check list**: right/middle/Space pan and pinch with real input, 225 % text scale, high contrast, tray folders and hotkeys, Library "Test" on real audio/Bluetooth, the F-029 design decision.

## Round 2 restore verification

Changed and restored: Windows app theme (`AppsUseLightTheme` 0 → 1 → 0, read back 0); dev `showcase.yaml` (extra test flows and a bound device added, then overwritten with the sample; a backup of the whole `dev` folder is in `%TEMP%\r2`); dev `big.yaml` identical to its backup. Verified after `dev-run.ps1 -Restore`: installed engine running from `DazeControlApp\current` (new pid), no dev editor or engine left, display layout identical to the start (one 3840×2160 at (0,0)). Not changed and so not compared: audio endpoints, Bluetooth connections, volumes, mute states. Not verified: no run with a Show message step was started, so nothing was heard.
