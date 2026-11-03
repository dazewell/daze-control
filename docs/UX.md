# UX

The design spec for the engine's tray/pop-up and the editor. Source: the designer pass of 2026-10-31 (plan §5).
Acceptance checklist in §5.9 gates T10.3.

Builds on OSD `SettingsWindow.xaml` (cards, InfoBar, HotkeyRecorder), `OsdWindow.xaml` (340 DIP, padding 16,
4-px grid, text-size growth) and the D-026 voice. User-facing text never says "node", "DAG" or "OSD".

## 5.1 Information architecture
- Engine (tray) owns hotkeys, tray menu, run pop-up and the YAML file; it works without the editor.
- Editor window: WinUI 3, Mica, TitleBar (page name as subtitle), NavigationView Left 200 wide.
  Pages: **Flows** (cards: glyph, name, shortcut, last result, On toggle; a click on the card (not the toggle) → flow editor with
  BreadcrumbBar "Flows › Connect Technics"; New flow: Blank / TV only / Audio → Headphones templates) ·
  **Library** (Pivot: Audio, Bluetooth, Displays, Android, Programs; settings cards with Test and "Used by 2
  flows", which lists the flows and opens one; layouts and Android devices Rename (their flows follow), a layout's
  Details list its screens; Programs add Open file location) · **Runs** (history + timeline) · footer **Settings** (start with Windows, pop-up, logs, updates),
  **About** (Copy details). Save errors: InfoBar on top of every page.
- Flow editor: "Not saved" + **Save** (accent, Ctrl+S) + **Cancel** beside the breadcrumb (D-047): edits are a
  draft until Save; Cancel throws it away; leaving the flow (breadcrumb, nav pane, closing the window) with a draft
  asks "Save your changes to <flow>?" Save / Don't save / Cancel. Run runs the saved version (its tooltip says so);
  Dry run and Test this step use what's on screen. Library and Settings keep autosaving (300 ms).
  CommandBar (Run, Dry run, Stop, Undo, Redo, Tidy up, Canvas | Outline, Problems (n)) · left
  palette 240 (collapsible, search) · canvas · right inspector 320 (280–480). Nothing selected → inspector
  shows the flow's settings (no extra dialog).

## 5.2 Canvas interaction
- **Layout is always automatic** (D-020): left→right columns (one per layer), gaps 64 between columns, 16
  between rows. Users choose connections and order within a column, never x/y; dragging regroups/reorders and
  nodes glide to their slot. Mind-map feel without chaos; no coordinates in YAML.
- Create: drag from the palette · double-click empty canvas or `/` → search flyout at the pointer · "+" on a
  selected node's output port (adds connected after) · "+" on an edge midpoint (insert).
- Connect: drag from output port (right) to an input port / node body (left); valid targets glow accent; a
  cycle shows not-allowed + teaching tip (§5.7 #3), never creates the edge.
- Parallel vs sequence: fan-out = "at the same time". Dragging a pill over another shows two drop zones:
  right third "Run after", body "Run together" (makes a card).
- Cards: select ≥ 2 → Ctrl+G / "Run together" (members' outer edges move to the card); Ctrl+Shift+G ungroups
  (stacked in one column, edges kept); drag pills in/out/between cards. No nested cards; edges between
  members refused.
- Fallback edges (`when: failure`): edge context menu "Only if this fails" → edge drawn critical-dashed with an
  "If it fails" chip; `always` → "Whatever happens" chip. The same without the edge menu: the inspector's
  "If it fails, run…" and Connections (§5.4); the empty state's tip points there.
- Selection: click, Ctrl+click, marquee (mouse/pen), Ctrl+A. An edge is clicked within 12 DIP of its curve (20 touch)
  and selected alone (Ctrl adds); the marquee takes edges with both ends inside; Del removes a selected edge. Ctrl+C/V/D copy nodes + inner edges + library
  refs, paste into a new column to the right. Del; deleting B in A→B→C reconnects A→C; deleting a card
  deletes its members. Undo/redo Ctrl+Z/Y, one entry per gesture, within the draft (Save or Cancel clears it).
- Navigate: wheel scroll, Shift+wheel horizontal, Ctrl+wheel/pinch zoom 50–200 %, Space+drag / middle-drag /
  one-finger pan, Ctrl+0 fit, Ctrl+1 100 %; minimap 160×100 bottom-right only when content overflows.
- Empty state: centred glyph, title, line, "Add a step" button.
- Keyboard: canvas is one tab stop with roving focus; Right/Left = next/previous step, Up/Down = same column;
  Enter → inspector; F2 rename; Alt+Up/Down reorder; Alt+Right into the adjacent card; Ctrl+L "Runs after…"
  checklist (cycle-making choices disabled with the reason).
- Screen readers: **Outline view** (TreeView in run order; card = branch) is the accessible equivalent; item
  name e.g. "Set default output, Technics. Audio. Waits for Connect Technics. May fail."; its context menu has
  every canvas command; live region announces run results (≤ 1/s); canvas exposes the same names.

## 5.3 Visual language
- Pill: min 232×56, max width 320, grows taller with text size. CornerRadius 28; the left cap is the status
  ring (32 DIP, inset 12, 16-DIP provider glyph). Title BodyStrong 1 line; summary Caption secondary ("Technics ·
  Default output"); ≤ 2 badge chips (20 DIP) then "+1". Fill CardBackgroundFillColorDefault, 1 px
  CardStrokeColorDefault; selected 2 px AccentFillColorDefault; system focus visual. Ports 10-DIP dots, 24-DIP
  hit (40 touch), on hover/selection, always on touch.
- Glyphs (Segoe Fluent Icons; verify in the gallery): Audio E767, Bluetooth E702, Display E7F4, Process E756,
  Script E943, HTTP E774, Android E8B2, Service E912, Wait E823, Message E8BD, Flag E7C1.
- Badges: "Stop the flow" = default, no badge · "Skip what depends on it" E893 · "Keep going" = **dashed 4/2
  border + "May fail" text chip** · retries E72C "×3" · timeout E916 "30 s" · problem E7BA caution + underline.
- Card: radius 12, padding 12, member spacing 8, CardBackgroundFillColorSecondary; header 32 (E8FD, editable
  name default "Run together", count chip); ports on the card only; drag-over = 2 px dashed accent + "Drop to run
  together".
- Edges: horizontal Béziers, 6-DIP arrowhead; normal 1.5 px ControlStrongStrokeColorDefault; into a may-fail
  node dashed 4/4; skipped dotted 1/3 TextFillColorDisabled; hover/selected 2 px accent with "+"/"×" just above the midpoint
  (not on it, so a click on the line still selects);
  during a run an edge turns accent once its source finished.
- Run states (shape + glyph, never colour alone): Pending empty ring · Running indeterminate ProgressRing accent ·
  Succeeded SystemFillColorSuccess E73E · Failed SystemFillColorCritical E711 + critical border · May-fail failed
  SystemFillColorCaution E7BA (not red) · Skipped disabled text/glyph, dashed ring E893. High contrast: system
  colours via theme dictionaries, ring 3 px, solid SystemColorWindow background, no dot grid.
- Grid 4 px; canvas SolidBackgroundFillColorBase + 24-DIP dot grid, 48 padding.
- Motion: add = fade + scale 0.96→1 150 ms · reflow offset 200 ms decelerate · state 150 ms crossfade · pop-up
  like the OSD (200 in / 150 out, FadeThrough). Animation effects off → all 0, static ¾ ring + "Running".

## 5.4 Inspector
- Header: glyph, editable title, Caption "Bluetooth › Connect", "Test this step". Sections: Settings · If it
  fails · Timing.
- Device picker: ComboBox items glyph + name + Caption state ("Connected" / "Not connected" / "Unplugged");
  Library items first, then "From Windows" live group (picking adds to Library silently); missing ref stays
  selected as "Not found on this PC"; refreshes live while open.
- Hotkey: OSD HotkeyRecorder with its conflict note. File: TextBox + Browse, inline "Can't find this file."
  Number+unit: NumberBox + suffix ("s"; stored as ms). Key/value rows + "Add header". Script: monospace 8 lines
  + "Open in editor".
- Failure policy: RadioButtons with descriptions — "Stop the flow" (default) "Nothing after this runs." ·
  "Skip what depends on it" "Steps that wait for this one are skipped. Others carry on." · "Keep going" "This
  step may fail, and the flow carries on." Hover/focus previews: affected nodes dim on the canvas.
  Below it **"If it fails, run…"** (DropDownButton): "A new step ›" area › step (added after this step with
  `when: failure`, selected) or a step already in the flow (no loops; an existing edge becomes a fallback).
- Connections: one row per edge at the step ("Runs after Connect", "Show a message runs after this") with a
  ComboBox "When it works" / "Only if it fails" / "Whatever happens".
- Timing: "Try again" 0–10, "Wait between tries" s, "Give up after" s + "No limit".
- Test: runs this step alone with on-screen values; "Testing…" + ProgressRing + Cancel; result InfoBar
  ("Worked in 1.2 s" / error + "Show details" expander); the pill ring shows it for 3 s.
- Flow settings: name, icon (glyph GridView flyout), tray folder (editable ComboBox) + "Line above it",
  shortcuts list + "Add shortcut", "If it's already running": Ignore it / Run it after / Start over, pop-up toggle.
  M11: a **Desktop shortcut** row: "Add to desktop" (#23); once there "On your desktop" + "Remove"; if the
  shortcut still has an old name, "Update". Engine not running → button off with #25. The Flows list card's context
  menu has the same "Add to desktop" / "Remove from desktop". Deleting a flow says "Its desktop shortcut goes too."
  and removes it on Save. "Shortcut" alone keeps meaning a hotkey; the file is always "desktop shortcut".
- **Suggestions and examples** (M11, D-050): every free-text value offers what the PC or the config has —
  Text: editable ComboBox, items glyph + name + Caption (service: display name · Running; credential: "Saved in
  Windows"; flag: "Used in 2 flows"; program: its folder); typing stays allowed. Screens (`display.outputs`):
  checklist rows name + Caption "DP-3 · On now", plus "Use the screens that are on now" (#27); saved but unplugged
  rows stay checked with #8. Android keys: token list in send order (× per token, Alt+Up/Down reorders) + an
  AutoSuggestBox "Add a key" (home, back, dpad_up/down/left/right/center, enter, menu, media_play_pause,
  media_next, media_previous, volume_up/down/mute, power, wakeup, sleep, settings, tv_input). Android input: the
  chosen device's inputs, none yet → hyperlink "Add inputs in the Library". Code boxes: DropDownButton "Examples"
  under the box; picking fills it (replaces, Ctrl+Z undoes). Examples — PowerShell: "Open a website"
  `Start-Process 'https://example.com'` · "Start an app and wait" `Start-Process notepad -Wait` · "Close an app"
  `Stop-Process -Name 'Spotify' -ErrorAction SilentlyContinue` · "Lock the PC" `rundll32.exe user32.dll,LockWorkStation`
  · "Fail unless an app is running" `if (-not (Get-Process steam -ErrorAction SilentlyContinue)) { exit 1 }`
  (pairs with "If it fails, run…"). Web request: address "Sunshine: close the running app"
  `https://localhost:47990/api/apps/close`; headers `Content-Type: application/json`; body `{ "key": "value" }`.
- Bluetooth pickers list Windows' paired devices (D-052); ones without audio are disabled with #26.

## 5.5 Runs
- Live: canvas overlays states; CommandBar "Running · 2 of 4" + Stop; finished pill summary becomes its
  duration; editing locked with an InfoBar. Dry run numbers nodes in start order, missing refs show failed,
  InfoBar summary.
- Runs page: ListView grouped by day (glyph, name, trigger "Alt+Z"/"Tray"/"Command line", time, result chip,
  duration). Detail: result sentence + **timeline** — time axis, one row per lane (greedy interval packing so
  parallel steps get separate lanes), bars 20 DIP radius 4 by state, may-fail-failed dashed caution, a card is a
  bracket over its members' lanes, retries are notches, min bar width 4; select bar → its log lines; "Open in
  editor" shows that run on the canvas. Keep the last 100 runs.

## 5.6 Tray menu and run pop-up
- Clicks (user, 2026-11-02): left click, or Enter/Space on the focused icon, opens the editor (starts it or brings
  it to the front); right click, or Shift+F10/Apps, opens the menu. No editor installed: every gesture opens the menu.
- Menu (native, dark/light): flows in YAML order with icon bitmaps and right-aligned shortcut (`\t`), folders as
  submenus, separators; running flow gets MFT_RADIOCHECK bullet (picking it applies "If it's already
  running"); invalid flow greyed "TV only (needs fixing)"; then separator · Open Daze Control · Pause
  shortcuts (checked) · separator · Exit. Icon variants busy / paused; tooltip "Daze Control · Running
  Connect Technics".
- Pop-up: 340 wide, padding 16, spacing 12, radius 8, acrylic, no-activate, the OSD's six positions. Row 1 (min
  28): glyph 20 | name BodyStrong | status Caption "2 of 3" on 20/*/44 columns · Row 2: 4-px determinate track
  (finished nodes) · Row 3: Caption current step "Connecting Technics…". ~104 DIP, grows with text size.
  Success: "Done" (status column) / "Took 3.1 s", hides after the duration, click-through. Longer outcomes are
  row 3's first line in the tone colour (D-035). Partial: "Done, with a problem" (caution) / "Connect headphones
  didn't work, so it went on without it." Failure: critical glyph, "Didn't finish", first failure in plain
  words (≤ 2 lines), Row 4 "Select to see what happened" (opens the run),
  stays 6 s and is clickable (still never activates on show).

## 5.7 Microcopy (the OSD's D-026 voice: plain, friendly, sentence case)
1 "This flow is empty" / "Add a step to start. Drag one from the left, or double-click anywhere." · 2 "No flows
yet" / "A flow does a few things for you with one shortcut, like connecting your headphones and switching to
them." · 3 "That would make a loop. A step can't wait for something that waits for it." · 4 "Steps in a card
start together, so they can't wait for each other." · 5 policies "Stop the flow" · "Skip what depends on it" ·
"Keep going" · 6 "May fail" · 7 "Technics isn't in your Library anymore. Pick another device." · 8 "Not
connected right now. The flow will still try." · 9 "Choose a device." · 10 "This flow is turned off until you
fix 2 problems. Your other flows still work." · 11 "Worked in 1.2 s." · 12 "Didn't work: Windows couldn't find
Technics. Make sure it's on and paired, then try again." · 13 "Connect Technics is done." · 14 "Done, with a
problem. Connect headphones didn't work, so it went on without it." · 15 "Skipped because Connect headphones
didn't work." · 16 "Connect Technics didn't finish. Bluetooth didn't connect in 10 s." · 17 "Connect Technics
is already running, so this press was ignored." · 18 "Another app uses Alt+Z. Try another one." · 19
"Couldn't save your changes" / "They'll work until you quit the app." · 20 "Capture current layout" / "Saves
how your screens are set up now." · 21 "Shortcuts are paused. Select Pause shortcuts again to turn them back
on." · 22 "You can edit again when this run finishes." · 23 "Add to desktop" / "Puts a shortcut on your desktop
that runs this flow." · 24 "This shortcut's flow isn't there anymore. You can delete the shortcut." · 25 "Start Daze
Control to add a desktop shortcut." · 26 "Daze Control can connect only headphones and speakers." · 27 "Use the
screens that are on now"

## 5.8 Risks → mitigations
1 XAML element count: pill template ≤ 12 elements, no ThemeShadow per node, all edges in one layer of a few batched paths (D-037),
semantic zoom < 60 % hides summaries/badges. 2 Edge hit-testing: distance to Bézier with a
12-DIP band (20 touch), hover thickens. 3 Half-built flows from autosave: engine keeps the **last valid version
per flow** and the editor says so; only a never-valid flow is off. 4 Touch/pen: ports on selection, long-press =
context menu, one-finger pan, marquee mouse/pen only, targets ≥ 40 DIP. 5 Text scaling: lay out from measured
sizes, check 225 % text. 6 DPI: canvas in DIP, minimap re-renders on DPI change, native pop-up per-monitor v2.
7 Focus traps: one tab stop + roving arrows; pointer capture, never OLE DragDrop inside the canvas. 8 High
contrast: solid background, no dots.

## 5.9 Design acceptance checklist (screenshot-verifiable)
- Light/dark/high contrast: every run state distinguishable without colour.
- May-fail pill: dashed border + "May fail" chip + dashed incoming edge.
- Dragging into a cycle shows the refusal tip and leaves no edge.
- After any drop: columns aligned, gaps 64/16, no overlap.
- 225 % text size: pills grow taller, nothing clips; pop-up grows too.
- Outline lists every node with "Waits for …"; Narrator reads name, provider, policy.
- Tab: palette → canvas → inspector; arrows move between nodes.
- Empty flow / no flows / no runs each show their empty state with an action.
- Runs page: "TV only" parallel steps on separate lanes; "Audio → Headphones" one caution bar, "Done, with a
  problem".
- Pop-up 340 wide, padding 16, failure wording per §5.7.
- Animation effects off: no motion anywhere, static running ring.
- Tray: right-aligned shortcuts, running bullet, invalid flow greyed "(needs fixing)".
- All strings sentence case, no jargon.
- M11: a desktop shortcut shows the flow's glyph and runs it with the engine stopped and running; every free-text
  param has a picker or an Examples menu; the Bluetooth picker matches Settings › Bluetooth & devices.
