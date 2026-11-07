# UX

The design spec for the engine's tray/pop-up and the editor. Source: the designer pass of 2026-10-31 (plan §5).
Acceptance checklist in §5.9 gates T10.3.

Builds on OSD `SettingsWindow.xaml` (cards, InfoBar, HotkeyRecorder), `OsdWindow.xaml` (340 DIP, padding 16,
4-px grid, text-size growth) and the D-026 voice. User-facing text never says "node", "DAG" or "OSD".

## 5.1 Information architecture
- Engine (tray) owns hotkeys, tray menu, run pop-up and the YAML file; it works without the editor.
- Editor window: WinUI 3, Mica, TitleBar (page name as subtitle), NavigationView Left 200 wide, collapsible to a 48-wide icon strip.
  Pages: **Flows** (cards: glyph, name, shortcut, last result, On toggle, Run; a click on the card (not the toggle or Run) → flow editor with
  BreadcrumbBar "Flows › Connect Technics"; New flow: Blank / TV only / Audio → Headphones templates) ·
  **Library** (Pivot: Audio, Bluetooth, Displays, Android, Programs; settings cards with Test and "Used by 2
  flows", which lists the flows and opens one; layouts and Android devices Rename (their flows follow), a layout's
  Details list its screens; an Android card's Inputs panel adds, renames (steps follow), removes (off while used) inputs
  and edits each one's moves as Up/Down chips in order (D-078); Programs add Open file location) · **Runs** (history + timeline) · footer **Settings** (start with Windows, pop-up, step defaults),
  **About** (version + Copy details, Updates, Files: config, logs and app folders). Save errors: InfoBar on top of every page.
- Flow editor: an unsaved-changes dot (fixed slot, tooltip "Unsaved changes", D-104) + **Save** (accent, Ctrl+S) + **Cancel** beside the breadcrumb (D-047): edits are a
  draft until Save; Cancel throws it away; leaving the flow (breadcrumb, nav pane, closing the window) with a draft
  asks "Save your changes to <flow>?" Save / Don't save / Cancel. **Flows** in the nav pane while a flow is open
  goes back to the list (after that prompt; Cancel stays in the flow). Run runs the saved version (its tooltip says so);
  Dry run and Test this step use what's on screen. Library and Settings keep autosaving (300 ms).
  CommandBar (Run, Dry run, Stop, Undo, Redo, Layout (§5.2), Canvas | Outline, Problems (n)) · left
  palette 240 (collapsible, search) · canvas · right inspector 320 (280–480). Nothing selected → inspector
  shows the flow's settings (no extra dialog).
- The Flows card's icon is the flow's own glyph, else its first step's (`FlowGlyph.CodeOf`, as in flow settings and
  the Runs list). A plain **Run** button on the card runs the saved version ("Stop" while that flow runs; off with #62,
  the reason in its tooltip and Narrator help text). Each card is one Tab stop that opens the flow (Enter/Space,
  Narrator "Open Connect Technics"), then its On switch and Run; its context menu has Open, Run and the desktop item
  (#66, D-070).
- Row layout (flows-list polish): grip | glyph tile | bold name over "shortcut · steps · last run" | problems | three fixed 28-DIP
  slots (not in tray menu, on desktop, used by) | On (96) | Run (80), spacing 4/8/12/16. Slots, switch and Run keep their
  columns on every row; empty slots stay empty and the grip only fades in on hover (opacity, 120 ms, no layout change). A coloured
  dot before the last-run text (green done, amber done with a problem, red didn't finish, accent running) always sits beside the
  words; a turned-off flow's name and glyph read quieter. Names are one line, trimmed with an ellipsis.
- **Back / Forward (D-095):** the mouse's back/forward buttons, a touchpad's back/forward gesture and Alt+Left / Alt+Right
  step through the pages and flows shown in this window (last 50), like a browser; a new place drops Forward. Leaving a flow
  with unsaved changes asks the usual Save / Don't save / Cancel (Cancel stays). Flows deleted since are skipped. There is
  no on-screen button. Alt+Left on a focused Flows card still moves it out of its group.
- The nav pane collapses: the toggle above the items ("Close Navigation" / "Open Navigation" for Narrator) shrinks
  it to a 48-DIP icon strip whose items show their name as a tooltip; open or collapsed is remembered on this PC
  (D-068).
- **M13 fixes (F4):** the card's context menu (and Delete on a focused card) has **Delete** (D-076): the
  flow goes at once and an InfoBar on the Flows page says #67 with **Undo**, until another Delete, New flow, opening
  a flow, leaving the page or closing the editor. Off while another flow runs it, with #68 as a greyed line under it.
  Renaming a flow and saving gives it a new id made from the name (D-075); nothing on screen shows the id.
- **M13 fixes (F2):** the "Run flow" step's Flow setting is a picker of the other flows by icon and name
  (it stores the id), captions "Turned off" / "Needs fixing" (#29); a deleted target stays selected as its id with
  "Isn't there anymore" and #51 under it; with no other flow it says #69; empty, #9 says "Choose a flow.". The Steps
  panel lists it under **Flows**, and its card shows the called flow's name.

### No layout jumps (D-104)
Nothing the user is looking at moves unless they caused it and it has to. Status that comes and goes (a dirty dot, a
"Restart now" button, a "Make this my order" button, a run result) gets a slot that is always there: invisible and
disabled when off, or drawn over empty space (the run result bar sits over the canvas), never collapsed. Allowed:
(1) a section the user just opened or a row they just added, animated with the motion tokens (D-097); (2) a field's
own validation line, in a panel with `Motion.Animate` (it slides, never snaps); (3) a different page or mode (Run
becomes Stop while a run is on); (4) rare persistent bars that must be seen on top of every page (can't edit, couldn't
save, conflict, engine not running). Never: a status text or a button that shifts its neighbours, a list that jumps
when a bar appears (the Flows page panel animates), scroll position lost on update.

## 5.2 Canvas interaction
- **Layout is always automatic** (D-020): left→right columns (one per layer), gaps 64 between columns, 16
  between rows. Users choose connections and order within a column, never x/y; dragging regroups/reorders and
  nodes glide to their slot. Mind-map feel without chaos; no coordinates in YAML.
- Create: drag from the palette · double-click empty canvas or `/` → search flyout at the pointer · "+" on a
  selected node's output port (adds connected after) · "+" on an edge midpoint (insert).
- Connect: drag from output port (right) to an input port / node body (left); valid targets glow accent; a
  cycle shows not-allowed + teaching tip (§5.7 #3), never creates the edge.
- Parallel vs sequence: fan-out = "at the same time". Dragging a pill over another shows three drop zones:
  left third "Run before" (D-083: it takes the target's incoming links, the target waits for it; keyboard route: the
  step menu's "Run before…"), right third "Run after", body "Run together" (makes a card). Linked steps may be dropped together: the drop
  fixes the order, so the dragged step's links on a path to or from the target go (D-058).
- Cards: select ≥ 2 → Ctrl+G / "Run together" (members' outer edges move to the card); Ctrl+Shift+G ungroups
  (stacked in one column, edges kept); drag pills in/out/between cards. No nested cards; grouping linked steps drops the
  links between them (a dropped fallback is said beside the card, D-058); a new edge between members is refused (§5.7 #4).
- Fallback edges (`when: failure`): edge context menu "Only if this fails" → edge drawn critical-dashed with an
  "If it fails" chip; `always` → "Whatever happens" chip. The same without the edge menu: the inspector's
  "If it fails, run…" and Connections (§5.4); the empty state's tip points there.
- Selection: click, Ctrl+click, marquee (mouse/pen), Ctrl+A. An edge is clicked within 12 DIP of its curve (20 touch)
  and selected alone (Ctrl adds); the marquee takes edges with both ends inside; Del removes a selected edge. Ctrl+C/V/D copy nodes + inner edges + library
  refs, paste into a new column to the right. Del; deleting B in A→B→C reconnects A→C; deleting a card
  deletes its members. Undo/redo Ctrl+Z/Y, one entry per gesture, within the draft (Save or Cancel clears it).
- Navigate: the mouse wheel and Ctrl+wheel zoom 50–200 % (×1.1 a notch) with the point under the pointer staying
  put; Shift+wheel scrolls sideways; a touchpad's two-finger scroll pans and pinch zooms (D-074). Space+drag /
  middle-drag / right-drag / one-finger pan move any flow, one that fits the window too, until 48 DIP of its steps
  is left on screen; Ctrl+0 fits and centres it, Ctrl+1 100 %; minimap 160×100 bottom-right only when content overflows.
  Right-drag pans over pills and edges too, without selecting them; a right click that doesn't leave the system drag
  rectangle still opens the menu (D-069).
- Empty state: centred glyph, title, line, "Add a step" button.
- Keyboard: canvas is one tab stop with roving focus; Right/Left = next/previous step, Up/Down = same column;
  Enter → inspector; F2 rename; Alt+Up/Down reorder; Alt+Right into the adjacent card; Ctrl+L "Runs after…"
  checklist (cycle-making choices disabled with the reason).
- Screen readers: **Outline view** (TreeView in run order; card = branch) is the accessible equivalent; item
  name e.g. "Set default output, Technics. Audio. Waits for Connect Technics. May fail."; its context menu has
  every canvas command; live region announces run results (≤ 1/s); canvas exposes the same names.

- Long simple chains (more than 5 boxes, no forks or joins) wrap onto new rows like text; forks, joins and
  side-by-side branches keep their own columns (D-060). Whatever follows a wrapped chain starts on a new line
  under it, at its left edge, joined like the wrapped rows (D-066).
- **Layout** (M13, replaces Tidy up, D-067): a CommandBar flyout, per flow on this PC, applied at once (not a flow
  edit, no undo): Wrap long chains (After 3 steps / After 5 steps / After 8 steps / Never) · Spacing (Compact /
  Normal / Roomy) · Line up (Centred / Along the top) · Lines (Curved / Right angles) · separator · **Untangle lines**
  (reorders steps within columns, a flow edit with undo; off with its reason when it can't help) · Reset layout.
  Radio items show the current choice; strings #63. Untangle off shows its reason where a shortcut would sit (a
  disabled item has no tooltip). Reset layout is off while the flow has the default layout. After a pick the canvas
  has focus, so Ctrl+Z undoes Untangle.

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
- Hotkey: OSD HotkeyRecorder with its conflict note. M13b (D-071..D-073): it also takes [ ] \ ; ' , . / ` - = and
  Num0–Num9 and Num* Num+ Num- Num. Num/ (alone or with Win/Ctrl/Alt); a refused or taken key shows why under the box (§5.7 H1–H7), and a
  number-pad shortcut alone shows the H8 tip under its row. File: TextBox + Browse, inline "Can't find this file."
  Number+unit: NumberBox + suffix ("s"; stored as ms). Key/value rows + "Add header". Script: monospace 8 lines
  + "Open in editor".
- Failure policy: RadioButtons with descriptions — "Stop the flow" (default) "Nothing after this runs." ·
  "Skip what depends on it" "Steps that wait for this one are skipped. Others carry on." · "Keep going" "This
  step may fail, and the flow carries on." Hover/focus previews: affected nodes dim on the canvas.
  Below it **"If it fails, run…"** (DropDownButton): "A new step ›" area › step (added after this step with
  `when: failure`, selected) or a step already in the flow (no loops; an existing edge becomes a fallback).
  Any new fallback (this menu, Connections, the edge menu) turns "Stop the flow" into "Skip what depends on it",
  which is what the run does then (D-024), in the same undo step, with a tip (§5.7 #46); Skip and Keep going stay,
  and removing the fallback doesn't switch back (D-059).
- Connections: one row per edge at the step ("Runs after Connect", "Show message runs after this") with a
  ComboBox "When it works" / "Only if it fails" / "Whatever happens".
- **"Wait until" recipe (T18.5, D-081):** there is no Wait-until step. Put a Check step (Check a program is running, a service, a file, a connection…) in the flow, set its "Try again" (e.g. 10) and "Wait between tries" (e.g. 3 s) and the flow continues once the check passes. Every check's "Is" setting says so in its help: "Fails when it isn't. To wait until it is, set Try again and Wait between tries."
- Timing: "Try again" 0–10, "Wait between tries" s, "Give up after" s + "No limit". Each row (and "If it fails") is *Default* (effective value at 60 % opacity, caption "Default") or *Set* (normal, "Reset" link E777, tip "Use the default (1 s)", clears only that field); typing or picking a different value sets it. Caption "From the default for Set default device steps" when the type has an entry. Button "Use these for all Set default device steps…" → flyout (the values, "Applies to n other steps in m flows that don't set their own.", Apply / Cancel) → InfoBar "Saved as the default for Set default device steps" + Undo. Canvas chips show effective values, tertiary text when all inherited, only if different from the built-in. The "If it fails, run…" Stop→Skip switch (D-059) looks at the effective policy.
- Several selected (M15, D-084): header "3 steps selected · Set default device" (a heading), then "Only if" (one type with conditions), "If it fails", Timing; steps and cards show only "If it fails". Rows read as for one step, but differing values are **Mixed** (NumberBox/ComboBox placeholder "Mixed", no radio chosen, caption "Mixed"); "Reset" (tip "Use the default for all 3 steps") shows when any step sets the field; accessible names "Give up after, mixed" / "Give up after, 10 s, default". A pick or a committed number is Set on all steps as one Undo; a refused change shows its problem and applies to none. Right-click a step: "Select all <type> steps". Under "Settings" (a heading) come the params every selected step has (one type, or the same name, kind and library kind across types; none with a card selected): the same controls as for one step, differing values read "Mixed" (device and choice placeholder, text box placeholder, "Mixed" caption for a switch; name ends ", mixed"). Lists, maps and flow pickers stay single-step. A pick or a committed value is written to all as one Undo; a required param can't be emptied (the problem shows, nothing changes); an optional one clears for all with the device picker's "Current default" row or an empty text.
- Test: runs this step alone with on-screen values; "Testing…" + ProgressRing + Cancel; result InfoBar
  ("Worked in 1.2 s" / error + "Show details" expander); the pill ring shows it for 3 s.
- Flow settings: name, icon (glyph GridView flyout), **Group** (editable ComboBox, caption "Also the folder in the tray menu", D-087) + "Line above it",
  shortcuts list + "Add shortcut", "If it's already running": Ignore it / Run it after / Start over, pop-up toggle.
  M11: a **Desktop shortcut** row: "Add to desktop" (#23); once there "On your desktop" + "Remove"; if the
  shortcut still has an old name, "Update". Engine not running → button off with #25. The Flows list card's context
  menu has the same "Add to desktop" / "Remove from desktop". Deleting a flow says "Its desktop shortcut goes too."
  and removes it on Save. "Shortcut" alone keeps meaning a hotkey; the file is always "desktop shortcut".
- **Suggestions and examples** (M11, D-050): every free-text value offers what the PC or the config has —
  Text: editable ComboBox, items glyph + name + Caption (service: display name · Running; credential: "Saved in
  Windows"; flag: "Used in 2 flows"; program: its folder); typing stays allowed. Screens (`display.outputs`):
  checklist rows name + Caption "DP-3 · On now", plus "Use the screens that are on now" (#27); saved but unplugged
  rows stay checked with #8. Send keys: token list in send order (× per token, Alt+Up/Down reorders) + an
  AutoSuggestBox "Add a key" (home, back, dpad_up/down/left/right/center, enter, menu, media_play_pause,
  media_next, media_previous, volume_up/down/mute, power, wakeup, sleep, settings, tv_input). Choose input: the
  chosen device's inputs, none yet → hyperlink "Add inputs in the Library" (opens Library › Android with that device's inputs open, D-078). Code boxes: DropDownButton "Examples"
  under the box; picking fills it (replaces, Ctrl+Z undoes). Examples — PowerShell: "Open a website"
  `Start-Process 'https://example.com'` · "Start an app and wait" `Start-Process notepad -Wait` · "Close an app"
  `Stop-Process -Name 'Spotify' -ErrorAction SilentlyContinue` · "Lock the PC" `rundll32.exe user32.dll,LockWorkStation`
  · "Fail unless an app is running" `if (-not (Get-Process steam -ErrorAction SilentlyContinue)) { exit 1 }`
  (pairs with "If it fails, run…"). Send web request: address "Sunshine: close the running app"
  `https://localhost:47990/api/apps/close`; headers `Content-Type: application/json`; body `{ "key": "value" }`.
  Android app (`android.start`): an AutoSuggestBox over the device's apps, name with the package id under it, sorted by name; typing filters by name or package; Enter on a row stores its package, Enter on other text keeps it as the package; focusing the box asks the device once for a fresh list (D-094).
- Bluetooth pickers list Windows' paired devices (D-052); ones without audio are disabled with #26.
- **Run flow** (M13, built T13.10): **Flow** ComboBox (glyph + name +
  caption: the inputs it takes, "Turned off", "Needs fixing"; its own flow and loop-making flows off with #49/#50);
  **Wait for it to finish** toggle, on by default (#55); **Inputs**: a device picker per input of the chosen flow,
  first item "Its default: Technics", then "From this flow's inputs", then "Library" (#60); with no flow chosen
  "Choose a flow first.", with none to give "Night takes no inputs."; **Open this flow** link (D-047 prompt). Picking
  another flow drops the inputs it doesn't take. Pill: the called flow's glyph, title "Run Make it the default" (also the
  name box's placeholder), summary the given devices (a passed-through input by its name), "Its defaults", or
  "Doesn't wait".
- **Inputs** (M13, built T13.10) in flow settings, after the pop-up toggle: "Add an input" (#60) → rows name · ×, kind
  (Audio device / Bluetooth device / Android device / Screen layout), default (device picker). A new input is an audio
  device with the Library's first one. Device pickers in that flow get a group "This flow's inputs" ("Output" /
  "Technics unless the caller picks another") above "Library"; a step using one shows its name on the pill. Removing a
  used input, or changing its kind, writes its default into the steps, with tip #61.
- **Menu step** (M12, built): Choices = token list of
  flows (glyph + name, ×, Alt+Up/Down, "Add a flow", own flow not offered, a missing one caution with #40), Title,
  Layout List / Grid, Size Normal / Large "For a TV across the room", Close after (s), "Wait for it to finish" (#41).
  "Test this step" shows the real menu and runs nothing (#42). (Controller-button chords and their recorder were removed, D-086.)

**Motion (D-097).** Changes in the configuration pages animate instead of jumping: new rows fade in (120 ms) and their neighbours slide (WinUI's reposition timing); selecting another step fades the pane from 0.4 to full. Only opacity and position move, never size of text, and the control being used never moves under the pointer. Put `motion:Motion.Animate="True"` on a panel (or `AnimateItems` on an ItemsControl) in a new page. With Windows "Animation effects" off nothing animates.

## 5.5 Runs
- Live: canvas overlays states; CommandBar "Running · 2 of 4" + Stop; finished pill summary becomes its
  duration; editing locked with an InfoBar. Dry run numbers nodes in start order, missing refs show failed,
  InfoBar summary.
- Runs page: ListView grouped by day (glyph, name, trigger "Alt+Z"/"Tray"/"Command line", time, result chip,
  duration). Detail: result sentence + **timeline** — time axis, one row per lane (greedy interval packing so
  parallel steps get separate lanes), bars 20 DIP radius 4 by state, may-fail-failed dashed caution, a card is a
  bracket over its members' lanes, retries are notches, min bar width 4; select bar → its log lines; "Open in
  editor" shows that run on the canvas. Keep the last 100 runs.
- Fallbacks (D-065, built): a fallback that wasn't needed is listed "Not needed" (#47), not a skip, and has no bar; a
  failure its fallback fixed is a caution-dashed bar and the run says "Connect Technics worked." with #48.
- M13 (D-062, built T13.10): a "Run flow" bar that waited has a chevron after it ("Show the steps of …"); expanded,
  the called flow's lanes show below under a bracket with its name (one level), and "Open in editor" with one of them
  selected opens that flow showing its part of the run. Live: the call's pill says the called flow's current step
  ("Connect…", #59), and a flow running inside another shows its states with InfoBar #64.

## 5.6 Tray menu and run pop-up
- Clicks (user, 2026-11-02): left click, or Enter/Space on the focused icon, opens the editor (starts it or brings
  it to the front); right click, or Shift+F10/Apps, opens the menu. No editor installed: every gesture opens the menu.
- Menu (native, dark/light): flows in YAML order with icon bitmaps and right-aligned shortcut (`\t`), folders as
  submenus, separators; running flow gets MFT_RADIOCHECK bullet (picking it applies "If it's already
  running"); invalid flow greyed "TV only (needs fixing)"; then separator · Open Daze Control · Pause
  shortcuts (checked) · separator · Exit. Icon variants busy / paused; tooltip "Daze Control · Running
  Connect Technics".
- Flow menu (M12, built): the pop-up's surface (acrylic, radius 8, padding 16, 4-px grid) as a no-focus menu, centred on
  the foreground window's monitor. **List** (default) 400 DIP wide (Large 560): title row (Subtitle, Large: Title) with
  a Caption "4 choices"; rows 56 (Large 72), glyph 24 (32) in a 40 (48) square, name Body one line, state caption right
  ("Running", "Needs fixing", "Turned off"); scrolls under work-area height − 48. **Grid** tiles 136×112 (Large 184×152),
  glyph 32 (48) over a two-line name; 1–3 items one row, 4 → 2×2, 5–9 → 3 columns, 10–12 → 4; moves in 2D, no wrap.
  Focus = accent fill, on-accent text, 2-DIP two-tone ring, semibold name; slides in 120 ms (animations off: instant);
  hover = subtle fill; high contrast: Highlight fill, 3-DIP ring. Never colour alone. An off choice can be focused (its
  reason reads) but A/Enter only shakes it. Hint row (Caption) follows the last input: pad "Ⓐ Run  Ⓑ Close", keys
  "Enter Run · Esc Close", mouse none (#30); last 5 s of the close timer "Closing in 5 s" (#31). All off → #28.
  Input: pad D-pad/left stick move, A run, B close; keys arrows/Tab/Shift+Tab/Home/End move, Enter/Space run, Esc close,
  1–9 pick; mouse hover focuses, click picks, wheel scrolls, click outside closes. Steam Controller reaches it as arrows,
  Enter, Esc. Text size grows rows and tiles. Known limits (README): games still see pad presses; borderless or windowed
  games only (#32 over exclusive/FSO); keys another app owns as global shortcuts don't reach the menu; no Narrator (D-091);
  Steam turns View+Menu into Alt+Tab (unbind it in Steam's desktop layout, D-057).
- Pop-up: 340 wide, padding 16, spacing 12, radius 8, acrylic, no-activate, the OSD's six positions. Row 1 (min
  28): glyph 20 | name BodyStrong | status Caption "2 of 3" on 20/*/44 columns · Row 2: 4-px determinate track
  (finished nodes) · Row 3: Caption current step "Connecting Technics…". ~104 DIP, grows with text size.
  Success: "Done" (status column) / "Took 3.1 s", hides after the duration, click-through. Longer outcomes are
  row 3's first line in the tone colour (D-035). Partial: "Done, with a problem" (caution) / "Connect headphones
  didn't work, so it went on without it." Failure: critical glyph, "Didn't finish", first failure in plain
  words (≤ 2 lines), Row 4 "Select to see what happened" (opens the run),
  stays 6 s and is clickable (still never activates on show).
- Fallbacks (D-065, built): "n of m" leaves out fallbacks until one starts (then m grows by it); a fallback that
  wasn't needed counts for nothing. A failure its fallback fixed ends "Done" with #48 on row 3, in the normal tone.
  The canvas's live "Running · n of m" counts the same way and its pills say "Not needed" (#47). M13 (D-062): a flow
  run by "Run flow" (waiting) is one step of its caller: row 3 shows "Make it the default · Connecting Sony…"
  (#59) and the bar moves within that step; the called flow shows no pop-up of its own.

## 5.7 Microcopy (the OSD's D-026 voice: plain, friendly, sentence case)
Step defaults card (Settings, D-080): "Step defaults" · "How long and how often steps try" / "A step that sets its own value keeps it." · empty "Set how long and how often steps try, once for every step of a type." · "Add a step type…" · row "Give up after 30 s · Try again 2 times · Wait 0.5 s between tries · Skip what depends on it" + "Used by 5 steps in 2 flows" / "Not used yet" · flyout "Give up after (seconds)", "Try again (times)", "Wait between tries (seconds)", "If it fails", each with Reset.
1 "This flow is empty" / "Add a step to start. Drag one from the left, or double-click anywhere." · 2 "No flows
yet" / "A flow does a few things for you with one shortcut, like connecting your headphones and switching to
them." · 3 "That would make a loop. A step can't wait for something that waits for it." · 4 "Steps in a card
start together, so they can't wait for each other." · 5 policies "Stop the flow" · "Skip what depends on it" ·
"Keep going" · 6 "May fail" · 7 "Technics isn't in your Library anymore. Pick another device." · 8 "Not
connected right now. The flow will still try." · 9 "Choose a device." / "Choose a flow." · 10 "This flow is turned off until you
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
screens that are on now" · M12 flow menu: 28 "Nothing here can run right now" / "Turn on or fix these flows in
Daze Control." · 29 captions "Running" · "Needs fixing" · "Turned off" · 30 hints "Ⓐ Run  Ⓑ Close" / "Enter Run · Esc
Close" · 31 "Closing in 5 s" · 32 "The menu can't show over this full-screen game. Switch the game to borderless or
windowed, then try again." · 33 "Couch TV didn't start. It needs fixing." · 34–37 (controller recorder) removed, D-086 ·
38 "A menu can't list its own flow." · 39 "That would make a loop: Couch waits for TV menu, which waits for Couch."
· 40 "Couch TV isn't there anymore. Remove it from the menu." · 41 "Wait for it to finish" / "Steps after the menu
wait for the picked flow, and stop if it fails." · 42 "You picked Couch TV. Testing doesn't run it." · 43 (recorder) removed · 44 "Nothing picked" · 45 "These steps start together now, so the "if it fails" or
"whatever happens" connection between them is gone." · 46 "If this fails, its fallback runs and the flow carries
on, so it's set to "Skip what depends on it" now." · M13: 47 "Not needed" · 48 "Connect Technics didn't work, so
Connect Sony ran instead." · 49 "A flow can't run itself." · 50 "That would make a loop: Headphones runs Make it the
default, which runs Headphones." · 51 "Make it the default isn't there anymore. Pick another flow." · 52 "Make it the
default is turned off, so this step will fail. Turn it on in Flows." · 53 "Didn't run Make it the default: it's
already running further up, so that would loop." · 54 "Flows ran inside each other more than 8 deep, so this one
stopped." · 55 "Wait for it to finish" / on: "Steps after this wait for it, and its result decides what runs next." /
off: "It starts on its own, and this flow carries on at once." · 56 "Make it the default didn't finish: Bluetooth
didn't connect in 20 s." · 57 "Make it the default didn't finish in 10 min." · 58 "Make it the default was already
running, so it wasn't started again." · 59 "Make it the default · Connecting Sony…" · 60 "Add an input" / "Lets
another flow pick the device when it runs this one." · groups "This flow's inputs" / "From this flow's inputs" · "Its
default: Technics" · 61 "Steps that used Output now use Technics." · 62 Run off: "Start Daze Control to run flows." ·
"Turn it on to run it." · "Fix its problems to run it." · 63 "Layout" · "Wrap long chains" "After 3 steps" "After 5
steps" "After 8 steps" "Never" · "Spacing" "Compact" "Normal" "Roomy" · "Line up" "Centred" "Along the top" · "Lines"
"Curved" "Right angles" · "Untangle lines" (off: "No lines cross" / "Can't untangle these any further") · "Reset
layout" · 64 "Running inside Headphones." · 65 "Would run Make it the default." · 66 Flows card menu "Open" · "Run"
· "Delete" (F4) · M13 fixes: 67 "Connect Technics was deleted." / "Undo" · 68 "Headphones runs this flow. Remove that
step first." / "Headphones and Morning run this flow. Remove those steps first." · 69 "Make another flow first. This
step runs one of your other flows." · 70 (F5) "Windows set up the screens differently: DELL S2722QC is still off" /
"…: ATNA40HQ05-0 is still on." / "…: DELL S2722QC isn't where Dual puts it." / "…: ATNA40HQ05-0 and DELL S2722QC show the same picture." /
"…: … don't show the same picture." (a saved duplicate) (the step fails; its fallback can run)
M13b shortcut keys (own numbering, so M13's new numbers don't clash): H1 "That key can't be a shortcut. Try a
letter, a number, F1–F24, a key like [ or \, or a number pad key." · H2 "Add Win, Ctrl or Alt, so the shortcut doesn't
get in the way of your typing." · H3 "Turn on Num Lock to use the number pad." · H4 "Shift doesn't work with number
pad keys. Use Win, Ctrl or Alt instead." · H5 "Windows uses Win+E. Try another one." · H6 "Another app uses Win+[.
If you closed it, check the tray: it may still be running. Try another one." (replaces #18) · H7 "Nothing came
through. Another app may be using those keys." · H8 "While Num Lock is on, Num1 runs this flow instead of typing 1.
Turn Num Lock off or pause shortcuts to type numbers."

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
- Flows list groups (M16, D-087): a header row per tray folder (chevron, folder glyph, name, "4 flows · 1 running", "n flows need fixing", `…` menu: Rename…, Ungroup, Turn all on/off, Move up/down); members 24 DIP in on a thin rail; the chevron or a click on the header collapses (per PC, `editor.json` `flowList.collapsedGroups`); card menu "Move to ›" = No group · each group · New group…. Header name "Audio, group, 4 flows, expanded".
- M16 (checked 2026-11-05 by code and tests; the editor and tray were **not** run live, the user's installed engine holds the pipe): [x] drag, Alt+↑/↓/← and Move to reorder and regroup, one Undo each (`FlowReorderTests`, `GroupedListTests`); [x] sort, search, filter keep groups in place (`FlowViewTests`); [x] selection and bulk Group / Move / Turn on/off / Delete (`FlowSelectionTests`); [x] Duplicate = "X copy" after the original, shortcuts cleared, one Undo; "Show in tray menu" / "Line above in tray" write the flow and undo (`FlowDuplicateTrayTests`). **Open, for the user:** the tray menu changing live after those two toggles (engine running); drag insertion line and group look in light/dark/high contrast; "3 selected" read by Narrator is out of scope (D-091); 225 % text on rows and the command bar; long list scroll while dragging.
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
- M12 (checked 2026-11-05 by code and tests unless noted): [x] pad, keys and mouse each pick and close (`MenuModelTests`,
  `GamepadServiceTests`, `MenuPlumbingTests`: the model and seams; the real pad is open, below); [x] nothing picked
  shows no pop-up and ends the flow Cancelled (`MenuProviderTests`); [x] a full-screen game is refused with #32, never
  drawn over (`FullScreenGuardPresenter`, S2); [x] the menu never activates, foreground unchanged over a borderless
  test app (S2 probe, SPIKES.md); [x] editor inspector, validation #38–#41, Test shows the menu (`MenuEditorTests`,
  `FlowMenuTests`). **Open, for the user** (list in [flow-menu/DESIGN.md](flow-menu/DESIGN.md)): a real Xbox / Steam
  pad driving the menu; focus readable from 3 m (Large, light/dark/high contrast); animation effects off = no motion;
  225 % text doesn't clip rows or tiles; the menu over a real borderless game, and #32 over a real exclusive one.
- M13: right-drag pans and a still right click opens the menu; a 7-step chain then a fork leaves no long empty line
  (the fork starts under the chain); every Layout choice changes the canvas visibly on a sample flow; the Flows card
  shows the flow's icon and runs it with Run (keyboard: Tab to the card, Enter opens); nav Flows leaves the editor;
  the collapsed nav pane survives a restart; Connect Technics with Technics on says "Done" and "n of m" never counts
  the Sony branch; "Run flow" shows one pop-up and one Runs entry with the nested lanes.
- M13 fixes: the wheel zooms with the point under the pointer staying put, and a 2-step flow moves with right-drag;
  resting the pointer anywhere in the flow editor never shows a "Ctrl+S" tip; the Steps panel's "Run flow" sits
  under "Flows", and its Flow picker lists the other flows by name; Delete on a card, then Undo, brings it back with
  its shortcut working; renaming "New flow" to "Morning" and saving shows `morning` in the next run's log line.
  "Use only these screens" with both screens gives the user's Extend arrangement, with one gives that screen alone,
  and "Restore screen layout" works from any starting state or fails with #70.
- M13b: Win+\ , Ctrl+Num1 and Num5 record and run a flow; Win+[ with SwitchDisplay.ahk running shows H6 (or H7),
  and after that app quits, clicking into the box and out again (or opening the tray menu) clears the note
  without a restart; Num Lock off shows H3.
- M15: select two steps of one type (right-click › "Select all <type> steps"): header "2 steps selected · <type>", Settings shows the shared params, "Mixed" where they differ. Pick one device for both, then Undo once: both go back. Clearing a required param (Volume) shows a problem and changes nothing. Select two types: only "If it fails" and Timing (plus a param only when both have it with the same kind). Select a step and a card: only "If it fails". Tab reaches every row; Narrator reads "Give up after, mixed" / "Give up after, 10 seconds, default".
