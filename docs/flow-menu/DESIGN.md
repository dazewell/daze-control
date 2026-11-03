# Flow menu (M12) — design

A step that pops up a menu of flows over every window, in the run pop-up's look, and a controller-button trigger
that starts a flow. Designed 2026-11-03 as its own sub-project (D-053), **not built yet**. Decisions: D-053..D-057.
Tasks: [PLAN.md](../PLAN.md) → M12. Microcopy numbers (#28–#44) are in [UX.md](../UX.md) §5.7.

When a task builds a part, it moves the as-built facts into ARCHITECTURE/UX and cuts them here, so this file
ends as the spec of what is still open (D-053).

## 1. Scenario and scope

The user (2026-11-03): one controller button combo runs a flow; that flow opens a menu; the user picks one of the
flows listed in it with the controller. Each choice shows that flow's icon and name. Keyboard and mouse work too.

The user's controllers (2026-11-03): an **Xbox controller** (read directly, D-056) and the **Steam Controller**
(reaches other apps only through Steam: Steam's desktop layout turns a combo into a keyboard shortcut and the
D-pad/A/B into arrows/Enter/Esc, which the menu already understands, §4.3). PlayStation pads: not a goal.

In: the `menu.show` step, the `gamepad:` trigger, the menu window, editor support, `dctl devices gamepads`.
Out (v1): menus drawn inside one window (submenus are flows, §2.5), per-item labels or icons other than the
flow's, touch-first layouts, vibration, Guide/Share/PS buttons, keeping the game from seeing presses (§4.6).

## 2. Feature list

### 2.1 The `menu.show` step (Menu › Show a menu)
```yaml
flows:
  couch:
    name: Couch
    glyph: E7FC
    gamepad: [View+Menu]
    whenRunning: ignore
    nodes:
      - id: pick
        step: menu.show
        with:
          items: [couch-tv, desk, connect-technics, tv-sleep]   # flow ids, in menu order
          title: Couch          # optional; default = the flow's name
          layout: list          # list (default) | grid
          size: large           # normal (default) | large ("For a TV across the room")
          closeAfter: 30s       # 5 s..10 min, default 30 s; any input restarts it
          wait: false           # true = the step waits for the picked flow and takes its result (§2.3)
```
- `items`: 1–12 flow ids. Each choice shows the flow's glyph (else its first step's, `FlowGlyph.CodeOf`) and name;
  the caption shows state: "Running", "Needs fixing", "Turned off" (#29). Keys 1–9 pick the first nine.
- Focus starts on the flow last picked from this menu (engine memory, lost on restart), else the first runnable one.
- Cancel = B, Esc, a click outside the menu, the close timer, or the trigger's flow being stopped (tray, `dctl
  cancel`, Stop in the editor). Not cancel: moving focus, a choice that is off (it just doesn't run).

### 2.2 What "run" means (D-054)
- Picking a flow **launches** it the way the tray does: through `EngineController` and `FlowLauncher.TriggerAsync`,
  trigger text "Menu · Couch", its own `whenRunning`, run record and pop-up. The menu closes at once.
- The step succeeds once the launch was accepted; its summary is "Picked Couch TV". Steps after the menu run in
  parallel with the picked flow.
- `wait: true`: the step waits for the picked flow and takes its outcome (Failed when it failed or didn't start,
  with its reason); the step's timeout then covers the picked flow too (default timeout 10 min).
- A pick that `whenRunning: ignore` drops: the step succeeds with "Couch TV was already running, so this pick was
  ignored." (#17 pattern) and the run pop-up says it.
- **Nothing picked** ends the flow quietly: the menu node is Cancelled with "Nothing picked" (#44), nothing after it
  runs (`always` edges included, as with any cancel), the flow outcome is Cancelled, and **no** run pop-up shows (the
  user closed it on purpose). History keeps the run.

### 2.3 Outcome of the step
The step's outcome is the launch (or, with `wait`, the picked flow's outcome), never "which flow": a flow can't
branch on the pick. Branching is what the menu items are. Recorded as `NodeResult.Summary` ("Picked Couch TV").

### 2.4 The controller trigger (D-057)
- Flow key `gamepad: [View+Menu, LB+RB+Y]`, beside `hotkeys:`. A chord is 2–4 buttons from A B X Y LB RB LT RT View
  Menu LS RS Up Down Left Right (Xbox names, `+` between, any order, written in this order). No Guide (Game Bar owns
  it and XInput doesn't report it), no single buttons (a game would start it by accident, #37).
- It fires when the buttons were all held together and then the first of them is let go, if no other button was
  held with them. So `View+Menu` never fires while pressing `View+Menu+A`, and the menu opens with the buttons up.
- Same rules as hotkeys: only enabled flows without errors; a chord in two flows → the later flow gets a problem
  (#36); Pause shortcuts pauses them too; trigger text "Controller · View + Menu".
- Any connected controller can fire it. The editor records a chord through the engine (§3.6).
- Steam Controller: map a Steam chord to a keyboard shortcut in Steam's desktop layout and give the flow that
  hotkey. The docs (README) say how; no code.

### 2.5 Menus inside menus; validation (D-054)
- A submenu is an item whose flow has a menu step. With launch semantics the first menu's run ends right after the
  pick, so menus can point at each other (a "Back" item is just the parent's flow): no runtime recursion.
- "Back" is an ordinary pick, so it follows the parent's `whenRunning`: it reopens the parent only when the parent's
  run has ended, i.e. the menu is its last step and it doesn't `wait`. A parent still running (later steps, or
  `wait: true`) applies its policy (ignore → #17 pop-up; queue → it reopens when that run ends; restart → it reopens
  now). The editor's Back pattern ("Controller menu" template) keeps the menu as the flow's last step.
- Validation (Core `ConfigValidator`):
  - an item that is the menu's own flow → error "A menu can't list its own flow." (#38);
  - an unknown flow id → warning, the item is dropped at run time, editor shows "isn't there anymore" (#40);
    the menu flow itself stays runnable (fail-soft);
  - a duplicate item → warning, the second is dropped;
  - `wait: true` items that reach back to the menu's flow through other `wait: true` menus → error (#39);
  - 0 items or more than 12 → error; `closeAfter` outside 5 s..10 min → error;
  - a `gamepad:` chord that doesn't parse, has < 2 or > 4 buttons or names Guide → error on the flow.
- A menu flow with only disabled or broken items still shows (#28) rather than fail silently.

## 3. Architecture

### 3.1 Parts
```
Core:     Catalog `menu.show` (ParamSpec: items TextList Ordered Suggest=Config(flows); title; layout; size;
          closeAfter; wait) · Model `GamepadChord` (parse/format, button flags) · Flow.Gamepad · validation (§2.5)
Runtime:  Providers/MenuProvider (IStepProvider) ─► IMenuPresenter (seam) ─► IFlowStarter (EngineController)
          Menus/MenuModel (pure: items, focus, grid moves, repeat, released-since-open, close timer; ManualTime)
          Triggers/GamepadChordRecognizer (pure: button states → chord fired) · Triggers/GamepadTable (like HotkeyTable)
Engine:   Shell/FlowMenu (window, drawing) · Shell/AcrylicSurface (shared with RunPopup) · Shell/MenuKeys (temporary
          RegisterHotKey set) · Input/GamepadSource (backend from spike S1, D-056) · Input/DeviceArrival
Ipc/Cli:  `devices gamepads`, `record-gamepad`, events unchanged; `dctl devices gamepads`
Editor:   inspector (flow token list, layout, size, close after, wait), Test shows the menu, flow settings
          "Controller buttons" recorder, "Controller menu" template, run trigger texts
```

### 3.2 Step contract and runner
- `MenuProvider.ExecuteAsync`: builds the `MenuModel` from `items` + the current snapshot (runnable / running /
  off / needs fixing per flow) → `IMenuPresenter.ShowAsync(model, ct)` → `Picked(flowId)` | `Closed(reason)`.
  Picked → `IFlowStarter.StartAsync(flowId, "Menu · <name>", wait, ct)` → Started | Ignored | Refused(reason) |
  Finished(RunResult). The provider sits in Runtime (it needs the controller); Engine fills the presenter.
- Contract additions (both small, both tested in Runtime.Tests):
  - `StepContext.SetSummary(string)` → `NodeResult.Summary` and NodeFinished's text ("Picked Couch TV"). Other
    providers may use it later; null = today's behaviour.
  - `StepEndsFlowException` (quiet stop): the runner marks the node Cancelled with its message, stops the flow
    (Cancelled), and `RunFinished.Quiet = true` tells the pop-up model to show nothing.
- One menu on screen engine-wide: the presenter owns a `menu` gate held **only around `ShowAsync`**, released
  before the picked flow starts or is awaited; a second menu step waits for the gate (counts toward its timeout).
  Not a descriptor resource (D-010): those are held for the whole attempt, so a `wait: true` parent would hold it
  while its child's menu waits for it — a deadlock until the timeout. The same flow pressed again is its
  `whenRunning` (default for new menu flows: ignore).
- Timeout: descriptor default 10 min, validator requires `timeout` > `closeAfter`. The close timer is the
  presenter's (`TimeProvider`), not the step timeout: running out of time is a cancel, not a failure.
- Dry run: doesn't show; summary "Would show 4 choices". Test this step (D-040): shows the real menu; a pick
  isn't run: "You picked Couch TV. Testing doesn't run it." (#42).
- Run pop-up: never shows for a `menu.show` node; a quiet stop shows nothing; the picked flow shows its own.
- Editor live view: the menu node shows Running with "Waiting for a pick".

### 3.3 Menu window (D-055)
- `AcrylicSurface` is cut out of `RunPopup` (D-034): `WS_POPUP` + `WS_EX_NOACTIVATE | TOOLWINDOW | TOPMOST`,
  transient-window backdrop, software D2D HWND target, DirectWrite, DPI per monitor v2, fades, d2d1/DWrite loaded
  only while shown. `RunPopup` and `FlowMenu` both use it; RunPopup's tests and screenshots must not change.
- Never activates (hard rule, user 2026-11-03): `WM_MOUSEACTIVATE` → `MA_NOACTIVATE`; no `SetForegroundWindow`,
  no `SetFocus`. Not `WS_EX_TRANSPARENT` (it takes clicks). The foreground window is the same before and after.
- Keyboard while open: `MenuKeys` registers Up Down Left Right Enter Space Esc Tab Shift+Tab Home End 1–9 with
  `RegisterHotKey` on the message window, and unregisters them on every close path (pick, cancel, timeout,
  step cancel, engine exit; a crash frees them with the process). A key another app owns just doesn't work
  (logged once). Works over elevated windows (hotkeys are system-wide).
- Mouse: hover moves focus, click picks, wheel scrolls; a click outside closes: a pass-through `WH_MOUSE_LL` hook
  only while open (never swallows; returns at once; on the message thread).
- Placement: centred on the monitor of the foreground window (a game on the TV), else of the cursor; work area,
  or the monitor rect when the foreground window covers it. Re-asserts `HWND_TOPMOST` on show.
- Full-screen: borderless and flip-model games (incl. "exclusive" games under fullscreen optimisations) compose
  topmost windows → the menu shows over them. True exclusive full-screen
  (`SHQueryUserNotificationState` = `QUNS_RUNNING_D3D_FULL_SCREEN`) can't show it: the step fails with #32 before
  anything is drawn or any key is taken. Spike S2 gates both.
- Animation effects (`SPI_GETCLIENTAREAANIMATION`) off → no fade, no focus slide, instant. High contrast → system
  colours, solid background, 3-DIP focus ring.
- Narrator (#26 shares this): the window can't take focus, so a minimal UIA provider raises
  `UiaRaiseNotificationEvent` on open ("Couch menu, 4 choices. Connect TV, 1 of 4.") and on each move. T12.9.

### 3.4 Controller input in the AOT engine (D-056)
| | GameInput (inbox `GameInput.dll`) | XInput (`xinput1_4`) | Windows.Gaming.Input | Raw Input / HID |
|---|---|---|---|---|
| Idle cost with a pad on | reading + device callbacks: 0 if it holds (S1) | polling: ~0.1 % CPU at 20 Hz | polling (`GetCurrentReading`) | events, but some pads report at 250 Hz+ nonstop |
| AOT | flat COM-like vtable, hand-written | flat C, `LibraryImport` | CsWinRT projection (D-052's reasons) | `WM_INPUT`, CsWin32 |
| Background input | focus policy to set (S1 checks) | yes, any foreground app | gated on focus for desktop apps (S1 notes) | `RIDEV_INPUTSINK` |
| Pads | Xbox (+ some HID) | Xbox / XInput only | Xbox + raw | any HID, own report parsing per pad |
| Dev PC | present (0.2309.26100, `GameInputSvc` manual, no redist) | present | — | — |
- Decision shape (D-056): GameInput callbacks if spike S1 passes, else XInput polling **only while an XInput pad is
  connected** (user, 2026-11-03: accepted, ~0.1 % CPU and a little battery while a pad is on; 0 with none):
  20 Hz while only chords listen, 60 Hz while a menu is open, only the connected slots (an empty slot stalls).
- Nothing gamepad-related loads unless a runnable flow has a `gamepad:` chord, a menu is open, or a request needs
  it: `record-gamepad` is a temporary consumer (source started for it, released on answer, timeout or client gone),
  and `devices gamepads` enumerates once on demand without starting the reader. With chords,
  `Input/DeviceArrival` registers a device-interface notification on the message window (HID + XUSB interface
  classes) and starts/stops the source as pads come and go; sleep/resume (`RegisterSuspendResumeNotification`)
  stops and restarts it. We never send output (rumble, LEDs), so pads still time out and sleep on their own.
- Readings → `GamepadChordRecognizer` (pure) on the pool → chord events posted to the message thread, like
  `WM_HOTKEY`; while a menu is open the same readings drive `MenuModel` instead (chords off).
- Stick: left stick past 0.5 moves (back under 0.3 to re-arm); D-pad and stick repeat after 400 ms, then every
  120 ms. A button counts in a menu only after it was seen up since the menu opened.
- Steam: with Steam Input on for Xbox pads, its desktop layout may also send arrows/Enter/Esc for the same press;
  a key nav within 100 ms of the same pad nav is dropped (one press = one move). S1 checks it with Steam on.
- Games keep seeing the pad while the menu is open; nothing short of a filter driver (admin, D-008) stops that.
  The menu suits the desktop or a paused game; the README says so.

### 3.5 YAML, model, validation
- `Flow.Gamepad: ValueList<GamepadChord>` (DTO strings, D-023: a bad chord is a problem, not a load failure),
  written like hotkeys; `GamepadChord` keeps a canonical order (A B X Y LB RB LT RT View Menu LS RS Up Down Left
  Right) so equality and clashes ignore the written order.
- `menu.show` params are ordinary `with:` values; `items` is a TextList whose values are flow ids
  (`ParamSpec.Suggest = Config(flows)`, `Ordered`). New: `ParamSpec.RefersTo = Flow` so validation checks the
  refs and the editor's flow rename rewrites them (as `LibraryEdits.Rename` does for library keys).

### 3.6 IPC and CLI
- `devices gamepads` → `DeviceInfo` per connected pad (name, slot, "Connected", battery level when known).
  `dctl devices gamepads`.
- `record-gamepad` (timeout ≤ 15 s, cancel on client gone): starts the source if nothing else holds it (so the first
  chord of a config can be recorded), pauses gamepad chords, answers the next chord
  (canonical text) or "no controller" / timed out. The editor's recorder uses it, so what you record is what the
  engine sees. Additive; `protocol` stays 1.
- `test-step` for `menu.show` shows the menu and answers the pick (§3.2).

### 3.7 Editor
- Inspector for `menu.show` (UX §5.4): **Choices** = the D-050 token list with flows (glyph + name chips, ×, Alt+Up/
  Down, "Add a flow" AutoSuggestBox over the config's flows minus this one); a missing flow's chip is caution
  with #40. **Title** (placeholder = flow name). **Layout** List / Grid radio with a tiny drawing each. **Size**
  Normal / Large "For a TV across the room". **Close after** seconds. **Wait for it to finish** toggle (#41).
- Preview = "Test this step" ("Show the menu"): the real menu on screen, no run (#42).
- Flow settings: "Controller buttons" row under Shortcuts: chips "View + Menu", "Add buttons" → recorder flyout
  (#34; no pad #35; engine off #43; clash #36; one button #37).
- New flow template "Controller menu": a `menu.show` with up to the first four flows, `gamepad: [View+Menu]`,
  `size: large`. Runs page triggers "Controller · View + Menu", "Menu · Couch".

### 3.8 Footprint budget
- No flow with a chord, no menu shown: unchanged (0 ms CPU / 10 min, nothing loaded).
- Chords listening, no pad on: 0 ms CPU / 10 min, ≤ +0.5 MB private.
- Pad on, untouched: GameInput path 0 ms; XInput path ≤ 300 ms CPU / 10 min (user-accepted, D-056).
- After a menu has shown: within D-036's +4.5 MB (shares the pop-up's D2D/DWrite load), flat over 10 menus.
- Menu open: ≤ 60 Hz redraws only on change; 0 when nothing moves.

## 4. UX (designer pass)

### 4.1 Layout and size
- Same surface as the run pop-up (UX §5.6): acrylic, radius 8, padding 16, 4-px grid, DWM shadow and border.
- **List** (default): width 400 DIP (Large 560). Title row min 32: title Subtitle (Large: Title), right-aligned
  Caption "4 choices". Rows 56 DIP (Large 72), spacing 4, radius 6: glyph 24 (Large 32) in a 40 (48) square |
  name Body (Large BodyLarge) one line, ellipsis | caption state right-aligned. Taller than the monitor's work
  area minus 48 → it scrolls, the focused row stays in view with one row of context.
- **Grid**: tiles 136×112 DIP (Large 184×152), spacing 8: glyph 32 (Large 48) centred, name below, two lines.
  Columns: 1–3 items one row; 4 → 2×2; 5–9 → 3; 10–12 → 4. D-pad moves in 2D, stops at edges (no wrap).
- Hint row (Caption secondary, 24): the input used last — pad "Ⓐ Run  Ⓑ Close", keyboard "Enter Run · Esc Close",
  mouse none (#30); circled letters drawn, not a font glyph. Last 5 s of the close timer: "Closing in 5 s" (#31).
- Text-size setting grows rows and tiles (measured layout, like the pop-up); 225 % text must not clip.

### 4.2 Focus visual (readable from a couch)
- Focused: AccentFillColorDefault fill, TextOnAccentFillColorPrimary text and glyph, a 2-DIP two-tone ring outside
  (system focus visual: dark outer/light inner), name semibold. With animations on, the fill slides to the new row
  in 120 ms and the tile scales 1.03; off → instant, no scale. Hover (mouse) = SubtleFillColorSecondary.
- Never colour alone: ring + weight + fill; high contrast: Highlight fill, HighlightText, 3-DIP ring.
- Off choices (needs fixing, turned off): disabled text, caption says why, focus can land on them (so the reason
  can be read) but A/Enter does nothing except a short shake (none with animations off).

### 4.3 Input map
| | Move | Run | Close | Other |
|---|---|---|---|---|
| Controller | D-pad, left stick | A | B | — |
| Keyboard | arrows, Tab / Shift+Tab, Home/End | Enter, Space | Esc | 1–9 pick directly |
| Mouse | hover | click | click outside | wheel scrolls |
| Steam Controller | (its desktop layout) arrows | Enter | Esc | — |

### 4.4 States and wording (D-026 voice; numbers in UX §5.7)
- Empty (all choices off): the rows show their reasons, plus a line "Nothing here can run right now" / "Turn on or
  fix these flows in Daze Control." (#28). B/Esc closes.
- Picked flow already running: its caption says "Running"; picking applies its "If it's already running"; ignore →
  run pop-up #17-style for the menu pick (§2.2).
- Picked flow became invalid between show and pick: the menu closes, pop-up "Couch TV didn't start. It needs
  fixing." (#33). Deleted: dropped from the menu at show time.
- Over an exclusive full-screen game: nothing shows; history and a run pop-up (if it can show later) say #32.
- Editor: #38–#41, #43 (§3.7).

### 4.5 Motion
Open: fade + scale 0.96→1, 150 ms decelerate (the OSD's in is 200; a menu answers a press, so faster). Close:
fade 100 ms. Pick: the focused row flashes once (100 ms) before the fade. Animation effects off → none.

### 4.6 Known limits (say so in README)
Games still see controller presses while the menu is open; true exclusive full-screen hides the menu; keys another
app registered as global shortcuts don't reach the menu; Narrator hears announcements, not a focusable list (T12.9).

## 5. Spikes (throwaway, `spikes/`, results into D-055/D-056 and here)
- **S1 Controller input in AOT (T12.1).** A console AOT probe with three readers: GameInput (callbacks, focus
  policy for background input), XInput (20/60 Hz on connected slots), and device-interface arrival. With the
  user's Xbox pad (USB and Bluetooth if available), Steam running and not.
  **Gate:** (a) 0 AOT/trim warnings; (b) no pad on → 0 ms CPU over 10 min; (c) a pad on and untouched → GameInput
  0 ms over 10 min, or XInput ≤ 300 ms; (d) readings arrive while another app (a game or a full-screen video) is
  in front; (e) arrival/removal and sleep/resume restart reading without leaks; (f) no install, no admin;
  (g) with Steam Input on, list what Steam also sends (keys) for D-pad/A/B. Pass (a–f) with GameInput → GameInput;
  else XInput. Record which and the numbers in D-056 and ARCHITECTURE → Footprint.
- **S2 Menu window over apps and games (T12.2).** Extend a copy of the RunPopup spike into a no-activate menu with
  temporary hotkeys and the pass-through mouse hook, plus a throwaway D3D11 test app (no user games needed) in
  three modes: borderless window, flip-model full screen (fullscreen optimisations), and exclusive
  `SetFullscreenState(TRUE)` with optimisations off.
  **Gate:** (a) shown and navigable with keys and mouse over the first two, the foreground window unchanged before/
  after (`GetForegroundWindow`), the test app keeps rendering; (b) the third is detected through
  `SHQueryUserNotificationState` before showing; (c) every close path unregisters the keys (a second app can
  register Up afterwards); (d) DPI: correct size on 100 % and 175 % monitors; (e) mouse hook never adds input lag
  (hook time < 1 ms). Fail (a) → stop and ask the user (the menu would need focus, which they declined).
- Narrator announcements (T12.9) need no spike of their own: they ride on issue #26's provider work.

## 6. Open questions (decide in the task that meets them)
- Battery level in `devices gamepads`: GameInput may expose it; XInput has `XInputGetBatteryInformation` (wireless
  only). Show it if cheap; not a goal.
- Reopening the parent menu on B in a submenu (needs the "opened from" chain in the trigger). v1: B closes.
- A per-flow "show in menus as" label. v1: the flow's name.
