# Flow list (M16) — design

Features (Parts A–G), designed 2026-11-04, **not built yet**. Decisions: D-087..D-090. Tasks: [PLAN.md](../PLAN.md) → M16.
Strings are drafts; the task that builds a part moves the as-built facts into ARCHITECTURE/UX/FEATURES and cuts them
here (D-053). User text never says node, DAG or OSD. Style follows [step-settings/DESIGN.md](../step-settings/DESIGN.md).

# Part A — What the list is today, and the target

## A1. Today
- **Flows page** (UX §5.1, D-070): `ItemsRepeater` of `FlowCard`s in **YAML order**, one Tab stop per card (Enter/Space opens,
  then the On switch and Run). Row: glyph · name · `shortcut · last result · time` · problems · On · Run. Context menu: Open,
  Run, desktop shortcut, Delete (D-076, InfoBar Undo). New flow button (Blank / templates / Controller menu).
- **Order = tray order.** `config.yaml` `flows:` order is the tray menu order. `tray.folder` (a string, optional) puts a
  flow in a tray submenu, which stands at its *first member's* place; `tray.separator` = a line above the item;
  `tray.show: false` and `enabled: false` hide a flow from the tray (`TrayMenu.Build`). The editor can only set
  folder/separator in a flow's settings; **nothing in the list changes order**, so the user edits YAML to reorder.
- The last run comes from the engine's `history` op (`Limit` 100 → newest per flow); older flows say "Not run yet".

## A2. Target (user, 2026-11-04)
The list becomes a working list: (1) drag and drop to reorder, (2) groups, (3) more per-row facts, and the usual list
tools: sort, search/filter, multi-select, context menu, keyboard. Scope:

| Feature | Scope |
|---|---|
| Drag and drop reorder (one flow, a group as a block, a selection) | **in** (B) |
| Keyboard reorder (Alt+↑/↓/←) and a "Move to" menu | **in** (B) |
| Undo for list changes (Ctrl+Z, the D-076 lifetime) | **in** (B) |
| Groups = tray folders (create, rename, ungroup, collapse, move into/out of) | **in** (C) |
| Richer row: shortcut, step count, last run + result, not-in-tray, desktop, called-by, problems | **in** (D) |
| Sort (Your order / Name / Last run), view-only, "Make this my order" | **in** (E) |
| Search box, one filter (All / On / Off / Needs fixing / Not in tray) | **in** (E) |
| Multi-select + bulk Group / Move / Turn on / Turn off / Delete | **in** (F) |
| Duplicate flow; "Show in tray menu" and "Line above in tray" in the context menu | **in** (F) |
| Inline rename (F2) from the list | later (a rename saves a new id, D-075: stays in flow settings for now) |
| Mini strip of the flow's step icons on the row | later |
| Nested groups (tray submenus are one level), persisted empty groups, group icons | out |
| Pins/favourites, tags, bulk Run, export/import of single flows, compact density | out |
| A separate tray-menu preview | out (the list *is* the tray order) |

# Part B — Reordering

## B1. The list shows exactly what the tray shows (D-088)
Display order = `TrayMenu.Build` order: top-level flows and groups interleaved; a group stands at its first member's place;
members in YAML order. This holds even for a hand-edited file whose group is scattered. A **reorder writes the displayed
order** back to `flows:` (one `session.Update`, autosaved like Delete), so a scattered group becomes contiguous; the tray
looks the same before and after. Other pickers (Run a flow, Runs filter, palette) follow `flows:` order, so they follow the list.

Pure, tested operations in a new `Editor.ViewModels/FlowList/FlowListModel` (no UI types):
`Rows(config)` · `Move(ids, targetGroup?, beforeId?)` · `MoveGroup(name, beforeItem)` · `SetGroup(ids, name?)` ·
`RenameGroup(old, new)` · `Ungroup(name)`. Each returns a new `Configuration` (same flows, new order and `tray.folder`); a test
asserts `TrayMenu.Build` before/after for pure-reorder cases and that no other flow field changes.

## B2. Drag and drop
- The whole card is the drag source; it starts after the **system drag rectangle** (as D-069), so a click still opens the flow.
  A grip glyph shows on hover/focus (tooltip "Drag to reorder"). Dragging a selected row drags the whole selection in its current
  relative order; dragging a group header drags the group with its members.
- Feedback: the dragged card dims; a 2-px accent **insertion line** shows between rows, indented when it would land inside a
  group; a group header gets an accent outline when the drop goes into it (middle of the header = end of the group, open or
  collapsed). Between a group's last member and the next top-level item the pointer's x decides: inside the indent = last in the
  group, left of it = after the group (same idea as D-083's zones). Edges auto-scroll. Esc cancels. Dropping on itself does nothing.
- Implementation preference: keep `ItemsRepeater` + `FlowCard` (the D-070 keyboard/UIA work stays) with `CanDrag`/`DragOver`/`Drop`
  on a *flat* row list (group header rows + flow rows). `ListView.CanReorderItems` is not chosen: it can't reorder across grouped
  sections and would replace `FlowCard`. T16.4 prototypes first; if the drag can't be made reliable, record an X-xxx and revisit.
- Off (grip hidden, tooltip says why) when the session can't edit, and when a **sort other than "Your order"** or any **search/filter**
  is active ("Sorted by name. Switch to Your order to rearrange.").

## B3. Keyboard and menu (the drag is never the only way)
- **Alt+↑ / Alt+↓** on the focused card moves it one place within its container (top level, or its group; a group header moves as a
  block among top-level items). **Alt+←** takes a member out of its group to just after the group. Narrator hears
  "Moved up. Connect Technics, position 1 of 4." (UIA notification); focus stays on the moved card.
- Context menu **Move to ›**: *No group* · each group (in list order) · *New group…*; plus **Move up** / **Move down**.

## B4. Undo
List changes (move, group, ungroup, rename group, bulk On/Off, duplicate) push the previous `Configuration` on a small stack (20).
**Undo** in the command bar and Ctrl+Z on the page restore it. Lifetime as D-076: cleared on opening a flow, leaving the page or
closing the editor. Delete keeps its InfoBar (its Undo is the stack's top entry). A change that arrives from outside (hot reload of a
hand edit) clears the stack rather than restoring stale text.

# Part C — Groups = tray folders (D-087)

## C1. One concept
A **group** *is* `tray.folder`: same YAML field, same tray submenu. No new key, no schema bump, no migration: every existing file
already has its groups.
- A flow with `tray.show: false` or turned off can still be in a group (shown dimmed with "Not in tray"; the tray skips it); a group
  whose members are all hidden has no tray submenu.
- Folder names compare case-insensitively (the tray does); the editor writes the spelling of the first member.
- A group exists only while it has a member. "New group…" asks for the name and moves at least the clicked/selected flow in. No
  empty-group placeholders (the tray couldn't show them; they'd be editor-only state).

## C2. Look
Group header row: chevron (expand/collapse, UIA Expand/Collapse), folder glyph (E8B7), name, caption "4 flows · 1 running",
aggregate problem badge, `…` menu. Members are indented 24 DIP with a thin left rail; ungrouped flows keep full width.
Collapsed state is per PC in `editor.json` (D-068), keyed by group name; groups start expanded; a search expands groups with hits.
Group menu: **Rename**, **Ungroup** (members stay, in place), **Turn all on / off**, **Move up / down**. No "delete group with its
flows" (Ungroup, select, Delete is deliberate). Rename onto an existing name merges (Undo covers it). Name: not empty, trimmed, ≤ 60 chars.

## C3. Flow settings
The "Tray folder" ComboBox (UX §5.4) is renamed **Group** with the caption "Also the folder in the tray menu". It lists the same groups
and creates one by typing, as today. "Line above it" stays there.

## C4. Tray
Unchanged: `TrayMenu.Build` already does folders, order and separators; this feature only edits the data. The "Controller menu"
template's "first four flows" follows the new order (existing menus store flow ids: unaffected).

# Part D — What a row says

Two lines plus badges. Line 1: glyph, **name**. Line 2 (caption): `Alt+Z · 5 steps · Worked 2 h ago`. Right: problem badge, small status
icons, On, Run.

| Fact | Source | Rule |
|---|---|---|
| Shortcut | `Hotkeys` | First one, "+1" when more ("Alt+Z +1"); "No shortcut" in tertiary text; all of them in the tooltip |
| Steps | `Nodes.Count` | "5 steps"; "No steps" for a blank flow |
| Last run | newest `RunRecord` | result word as the Runs page (`RunWords.Chip`) + relative time; older than a day → "Mon 14:02"; running → "Running" |
| No record | none in history | "No recent run" (the history keeps ≤ 200 runs, so "Not run yet" would be untrue) |
| Not in tray | `!Tray.Show` | icon (E7B3), tooltip "Not in the tray menu"; text secondary when the flow is Off |
| Desktop shortcut | `DesktopShortcutStore` | icon (E8A7), tooltip "On your desktop" |
| Called by | `flow.run` steps in other flows | tooltip only: "Used by 2 flows" (wording as the Library's "Used by") |
| Problems | validation | unchanged: icon + "n problems"; a flow that needs fixing keeps running its last good version (D-021) |
| Group | `Tray.Folder` | the group header shows it; the row repeats it only in search results ("in Audio") |

- **Idle = zero (D-009):** relative times are computed when the page shows, when the window activates and on run events. No timer
  ticks them.
- Accessible name: "Connect Technics, Alt+Z, 5 steps, worked 2 hours ago, in Audio, turned on" (+ "needs fixing", "not in the tray menu").
- Last-run source stays the `history` op; T16.2 raises the page's `Limit` to the file's 200, newest per flow. No new IPC op (D-089).

# Part E — Sort, search, filter

- Command bar: **Sort** (Your order · Name · Last run) · **Search** (Ctrl+F focuses; matches name, shortcut text, group name, step
  titles) · **Show** (All · On · Off · Needs fixing · Not in tray).
- **View-only (D-088).** "Your order" is the YAML/tray order and the only editable view. Name / Last run sort *within* each container
  (groups keep their place; ties keep Your order). While a sort is on, **"Make this my order"** writes the visible order (one Undo).
  Search/filter hide rows, never reorder; drag is off while either is active.
- Empty result: "No flows match." + **Clear**. Sort and filter are remembered per PC (`editor.json`); the search text is not.

# Part F — Selection, bulk actions, menus (D-090)

- **Selection** (additive to D-070: a plain click or Enter/Space still opens the flow): Ctrl+click toggles, Shift+click selects a range
  of visible rows, Ctrl+A all visible, Ctrl+Space toggles the focused card, Esc clears. A **Select** command-bar toggle shows a
  checkbox on every card (touch, discoverability); in that mode a click toggles. A group header selects all its members.
- With ≥ 1 selected the command bar shows "3 selected" · **Group…** · **Move to ›** · **Turn on** · **Turn off** · **Delete** · Clear.
  Delete is D-076 for many (one InfoBar "3 flows were deleted." + Undo; off while another running flow calls one of them, with the
  reason line). Run is single-flow only.
- **Context menu** (one flow or the selection): Open · Run · Duplicate · Move to › · Move up/down · Show in tray menu (check) ·
  Line above in tray (check) · Add/Remove desktop shortcut · Delete. **Duplicate** = "X copy" (unique id, D-075 slug), same steps and
  settings, shortcuts **cleared** (a copy would fight for the same keys, D-072), right after the original in its group, left On.
- Narrator: "3 selected"; each action's result is read from the page's live region (as the Delete bar).

# Part G — Data model, migration, footprint, accessibility

- **config.yaml:** no change. `version: 1`, no new keys. Reorder rewrites `flows:` order and `tray.folder`/`tray.separator`; comments are
  not kept on editor save (existing, accepted). Hand edits still load; scattered groups render grouped (B1). Nothing to migrate.
- **editor.json (D-068):** `flowList: { sort, filter, collapsedGroups: [...] }`; missing or unknown keys = defaults.
- **Engine/Core:** no runtime change; `FlowListModel` lives in `Editor.ViewModels` (pure, unit-tested). Tray tests unchanged.
- **Footprint:** editor only; no engine memory or CPU change, no timers, editor closed = not running. `ItemsRepeater` virtualises a long
  list; row facts are computed once per config/history change.
- **Accessibility:** every drag has a keyboard and a menu equivalent (B3); focus is visible on cards, grips and group headers; a group
  header is an expandable item ("Audio, group, 4 flows, expanded"); no colour-only state (badges carry icon and text); theme brushes
  for contrast; the insertion line does not animate under reduced motion.

# Open choices for the user (recommended answer first)

1. **Groups merge with tray folders?** *Yes, one concept* (D-087). Alternative: separate list-only groups (a second field, and the list
   and tray can disagree).
2. **UI name "Group" or "Folder"?** *"Group", caption "Also the folder in the tray menu".*
3. **Empty groups?** *Not kept; a group is created with its first flow.*
4. **Does sort write the order?** *No, view-only, with "Make this my order"* (D-088).
5. **Selection gesture:** *Ctrl/Shift+click plus a "Select" toggle with checkboxes.* Alternative: always-visible checkboxes (noisy for the
   common one-flow case).
6. **Duplicate clears shortcuts?** *Yes.* Alternative: keep them and flag the clash as a problem.
7. **"Not run yet" becomes "No recent run"?** *Yes*, it is true with a 200-run history.
8. **Row extras (step-icon strip, inline F2 rename):** *later, not in M16.*
9. **Bulk Run and flow export/import:** *out.*
