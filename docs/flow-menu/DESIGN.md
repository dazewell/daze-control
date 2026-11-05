# Flow menu (M12) — design

A step that pops up a menu of flows over every window, in the run pop-up's look, driven by a controller too.
Designed 2026-11-03 as its own sub-project (D-053). **Chords were removed 2026-11-04 (D-086):** a controller only
drives an open menu; wherever below a `gamepad:` chord, `record-gamepad` or "Controller buttons" appears, it is gone.
Decisions: D-053..D-057, D-086.
Tasks: [PLAN.md](../PLAN.md) → M12. Microcopy numbers (#28–#44) are in [UX.md](../UX.md) §5.7.

When a task builds a part, it moves the as-built facts into ARCHITECTURE/UX and cuts them here, so this file
ends as the spec of what is still open (D-053).

## 1. Scenario and scope

The user (2026-11-03): one controller button combo runs a flow; that flow opens a menu; the user picks one of the
flows listed in it with the controller. Each choice shows that flow's icon and name. Keyboard and mouse work too.

The user's controllers (2026-11-03): an **Xbox controller** (read directly, D-056) and the **Steam Controller**
(reaches other apps only through Steam: Steam's desktop layout turns a combo into a keyboard shortcut and the
D-pad/A/B into arrows/Enter/Esc, which the menu already understands, §4.3). PlayStation pads: not a goal.

In: the `menu.show` step, the menu window, editor support, `dctl devices gamepads`.
Out (v1): menus drawn inside one window (submenus are flows, §2.5), per-item labels or icons other than the
flow's, touch-first layouts, vibration, Guide/Share/PS buttons, keeping the game from seeing presses (§4.6).

## 2. Feature list

### 2.1 The `menu.show` step (Menu › Show a menu)
```yaml
flows:
  couch:
    name: Couch
    glyph: E7FC
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
- `wait: true` (D-061): the picked flow runs **nested** inside the menu flow's run, as `flow.run` does
  ([flow-calls/DESIGN.md](../flow-calls/DESIGN.md) §3): no `whenRunning`, no record or pop-up of its own (the menu
  flow's pop-up shows it), and the step takes its outcome; the step's timeout covers it (default 10 min).
- Launch picks only (`wait: false`): a pick that `whenRunning: ignore` drops: the step succeeds with "Couch TV was already running, so this pick was
  ignored." (#17 pattern) and the run pop-up says it.
- **Nothing picked** ends the flow quietly: the menu node is Cancelled with "Nothing picked" (#44), nothing after it
  runs (`always` edges included, as with any cancel), the flow outcome is Cancelled, and **no** run pop-up shows (the
  user closed it on purpose). History keeps the run.

### 2.3 Outcome of the step
The step's outcome is the launch (or, with `wait`, the picked flow's outcome), never "which flow": a flow can't
branch on the pick. Branching is what the menu items are. Recorded as `NodeResult.Summary` ("Picked Couch TV").

### 2.4 The controller trigger — removed (D-086)
A flow is started by a shortcut, the tray or the CLI; the menu then opens and the pad drives it. A pad button the user
wants as a trigger is mapped to a key in Steam Input and used as a shortcut (README).

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
  - `wait: true` items that reach back to the menu's flow (through `wait: true` menus or `flow.run` steps) → error
    (#39), checked with flow calls on one call graph (D-063);
  - 0 items or more than 12 → error; `closeAfter` outside 5 s..10 min → error;
  - a v0.40.0 file's `gamepad:` key → one warning, ignored, dropped on the next save (D-086).
- A menu flow with only disabled or broken items still shows (#28) rather than fail silently.

## 3. Architecture

### 3.1 Parts
```
Core:     Catalog `menu.show` (ParamSpec: items TextList Ordered Suggest=Config(flows); title; layout; size;
          closeAfter; wait) · Model `GamepadButtons` flags · validation (§2.5)
Runtime:  Providers/MenuProvider (IStepProvider) ─► IMenuPresenter (seam) ─► IFlowStarter (EngineController)
          Menus/MenuModel (pure: items, focus, grid moves, repeat, released-since-open, close timer; ManualTime)
Engine:   Shell/FlowMenu (window, drawing) · Shell/AcrylicSurface (shared with RunPopup) · Shell/MenuKeys (temporary
          RegisterHotKey set) · Input/GamepadSource (backend from spike S1, D-056) · Input/DeviceArrival
Ipc/Cli:  `devices gamepads`, events unchanged; `dctl devices gamepads`
Editor:   inspector (flow token list, layout, size, close after, wait), Test shows the menu, flow settings
          "Controller menu" template, run trigger texts
```

### 3.2 Step contract and runner
- `MenuProvider.ExecuteAsync`: builds the `MenuModel` from `items` + the current snapshot (runnable / running /
  off / needs fixing per flow) → `IMenuPresenter.ShowAsync(model, ct)` → `Picked(flowId)` | `Closed(reason)`.
  Picked → `IFlowStarter` (shared with `flow.run`, D-061): Start → Started | Ignored | Refused(reason); Wait → a
  nested run's result (D-062). The provider sits in Runtime (it needs the controller); Engine fills the presenter.
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

### 3.3 Menu window (D-055) — built in T12.7, now in ARCHITECTURE (Menu window)
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
- Full screen: borderless only (D-055, user 2026-11-04). Borderless and windowed games compose topmost windows, so
  the menu shows over them. Any DXGI full screen (exclusive mode, or "exclusive" under FSO) leaves full screen as
  soon as any window of ours shows (S2, X-010). So the menu checks `SHQueryUserNotificationState` first. If it
  reports `QUNS_RUNNING_D3D_FULL_SCREEN` (3, for FSO too), the step fails with #32 before anything is drawn or any
  key is taken: one engine log line, run history, no pop-up.
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
  60 Hz while a menu is open (nothing otherwise, D-086), only the connected slots (an empty slot stalls).
- Nothing gamepad-related loads unless a menu is open (D-086): the first menu start loads XInput and the reader thread,
  closing the menu stops it. `devices gamepads` enumerates once on demand without starting the reader. While the
  source runs, `Input/DeviceArrival` (HID + XUSB interface classes) wakes the parked reader when a pad is plugged in.
  We never send output (rumble, LEDs), so pads still time out and sleep on their own.
- Readings drive `MenuModel` directly (marshalled to the message thread).
- Stick: left stick past 0.5 moves (back under 0.3 to re-arm); D-pad and stick repeat after 400 ms, then every
  120 ms. A button counts in a menu only after it was seen up since the menu opened.
- Steam: with Steam Input on for Xbox pads, its desktop layout may also send arrows/Enter/Esc for the same press;
  a key nav within 100 ms of the same pad nav is dropped (one press = one move). S1 checks it with Steam on.
- Games keep seeing the pad while the menu is open; nothing short of a filter driver (admin, D-008) stops that.
  The menu suits the desktop or a paused game; the README says so.

### 3.5 YAML, model, validation
- `menu.show` params are ordinary `with:` values; `items` is a TextList whose values are flow ids
  (`ParamSpec.Suggest = Config(flows)`, `Ordered`). New: `ParamSpec.RefersTo = Flow` so validation checks the
  refs and the editor's flow rename rewrites them (as `LibraryEdits.Rename` does for library keys).

### 3.6 IPC and CLI
- `devices gamepads` → `DeviceInfo` per connected pad (name, slot, "Connected", battery level when known).
  `dctl devices gamepads`.
- `test-step` for `menu.show` shows the menu and answers the pick (§3.2).

### 3.7 Editor
- Inspector for `menu.show` (UX §5.4): **Choices** = the D-050 token list with flows (glyph + name chips, ×, Alt+Up/
  Down, "Add a flow" AutoSuggestBox over the config's flows minus this one); a missing flow's chip is caution
  with #40. **Title** (placeholder = flow name). **Layout** List / Grid radio with a tiny drawing each. **Size**
  Normal / Large "For a TV across the room". **Close after** seconds. **Wait for it to finish** toggle (#41).
- Preview = "Test this step" ("Show the menu"): the real menu on screen, no run (#42).
- New flow template "Controller menu": a `menu.show` with up to the first four flows, `size: large`, no trigger yet
  (the user adds a shortcut). Runs page trigger "Menu · Couch".

### 3.8 Footprint budget
- No menu shown: unchanged (0 ms CPU / 10 min, nothing loaded, no thread).
- Menu open with a pad on: XInput at 60 Hz for as long as it is open (user-accepted, D-056).
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
- Picked flow already running: its caption says "Running"; a launch pick (`wait: false`) applies its "If it's already running", a nested one (`wait: true`) just runs; ignore →
  run pop-up #17-style for the menu pick (§2.2).
- Picked flow became invalid between show and pick: the menu closes, pop-up "Couch TV didn't start. It needs
  fixing." (#33). Deleted: dropped from the menu at show time.
- Over a full-screen game (QUNS = 3: exclusive mode or FSO): nothing shows, no pop-up either; history and the engine
  log say #32 (it should suggest switching the game to borderless).
- Editor: #38–#41, #43 (§3.7).

### 4.5 Motion
Open: fade + scale 0.96→1, 150 ms decelerate (the OSD's in is 200; a menu answers a press, so faster). Close:
fade 100 ms. Pick: the focused row flashes once (100 ms) before the fade. Animation effects off → none.

### 4.6 Known limits (say so in README)
Games still see controller presses while the menu is open; the menu shows only over borderless or windowed games,
not over a game in its own full-screen mode (set the game to borderless); keys another
app registered as global shortcuts don't reach the menu; Narrator hears announcements, not a focusable list (T12.9);
with Steam running, Steam turns View+Menu into Alt+Tab, so unbind that in Steam's desktop layout (D-057).

## 5. Spikes (throwaway, `spikes/`, results into D-055/D-056 and here)
- **S1 Controller input in AOT (T12.1).** A console AOT probe with three readers: GameInput (callbacks, focus
  policy for background input), XInput (20/60 Hz on connected slots), and device-interface arrival. With the
  user's Xbox pad (USB and Bluetooth if available), Steam running and not.
  **Gate:** (a) 0 AOT/trim warnings; (b) no pad on → 0 ms CPU over 10 min; (c) a pad on and untouched → GameInput
  0 ms over 10 min, or XInput ≤ 300 ms; (d) readings arrive while another app (a game or a full-screen video) is
  in front; (e) arrival/removal and sleep/resume restart reading without leaks; (f) no install, no admin;
  (g) with Steam Input on, list what Steam also sends (keys) for D-pad/A/B. Pass (a–f) with GameInput → GameInput;
  else XInput. Record which and the numbers in D-056 and ARCHITECTURE → Footprint.
  **Result 2026-11-04 (probe `spikes/GamepadProbe`, Steam Controller on its wireless puck; no Xbox pad that day):**
  XInput (D-056 accepted). (a) holds; GameInput fails, since its reading callbacks are E_NOTIMPL (X-011); (b) 0 ms;
  (e) arrival and removal hold (incl. the Xbox pad dropping and coming back by itself), sleep/resume not run; (f) holds.
  Xbox pad = Flydigi Vader 4 Pro on its wireless adapter (an Xbox 360 XInput pad): (c) 234 ms over 600 s untouched, but
  it slept after about 4 min; always on, 20 Hz would be about 410 ms, so T12.6 may need 10 Hz for chords only (D-056);
  (d) every button read with Steam's window in front, Steam on and off; Raw Input showed the pad sending about 500
  reports/s nonstop (D-056's "why not HID" confirmed). (g) Xbox pad: Steam injects nothing for single buttons, but
  **View+Menu becomes Alt+Tab** (Steam on: injected Alt, VK_GAMEPAD_VIEW/MENU, Tab; the task switcher opens). Steam
  off: View+Menu gives one injected VK 0x07, nothing else. The default stays `View+Menu` (user, D-057).
  (g) The Steam Controller never shows up as an XInput or GameInput pad. With Steam on, Steam injects keys
  (`LLKHF_INJECTED`, no device): A Enter, B Esc, X Space, Y nothing, D-pad arrows, View+Menu Tab+Esc, right pad
  injected mouse moves. With Steam off, the puck's own HID keyboard/mouse (VID_28DE PID_1304) sends the same keys
  except X (nothing), plus mouse moves, clicks and wheel. So the menu's arrows/Enter/Esc work with this pad in both
  states, chords can't come from it, and the §4 dedupe only matters for Xbox pads with Steam Input on.
- **S2 Menu window over apps and games (T12.2).** Extend a copy of the RunPopup spike into a no-activate menu with
  temporary hotkeys and the pass-through mouse hook, plus a throwaway D3D11 test app (no user games needed) in
  three modes: borderless window, flip-model full screen (fullscreen optimisations), and exclusive
  `SetFullscreenState(TRUE)` with optimisations off.
  **Gate:** (a) shown and navigable with keys and mouse over the first two, the foreground window unchanged before/
  after (`GetForegroundWindow`), the test app keeps rendering; (b) the third is detected through
  `SHQueryUserNotificationState` before showing; (c) every close path unregisters the keys (a second app can
  register Up afterwards); (d) DPI: correct size on 100 % and 175 % monitors; (e) mouse hook never adds input lag
  (hook time < 1 ms). Fail (a) → stop and ask the user (the menu would need focus, which they declined).
  **Gate as amended after S2 (user, 2026-11-04: "Borderless only"):** (a) applies to borderless and windowed
  games only. (b) refuses every DXGI full screen (QUNS = 3: exclusive mode and FSO) before showing anything, with
  #32 logged. Both hold, so M12 continues.
  **Result (2026-11-04, dev PC: one 2880×1800 panel at 175 %, Win 11 26300, FSO on by default; probe
  `spikes/menu-window`, not in the solution; GDI drawing, so acrylic is untested):**
  - (a) **Fails for mode 2.** Borderless (mode 1) passes. Keys (arrows, Tab/Shift+Tab, Home/End, Enter, Space,
    1–9, Esc) and the mouse (hover, wheel, click, click outside) all work. The foreground is the game before,
    during and after, and the game keeps 113–121 fps on a 120 Hz panel
    ([keys](s2/borderless-keys.png), [mouse](s2/borderless-mouse.png)). Flip-model full screen with FSO on
    (mode 2): QUNS = 3, so the check refuses it. Shown anyway, the menu works, but ~100 ms later the game is
    deactivated, the foreground goes to 0, DXGI leaves full screen and `Present` returns `DXGI_STATUS_OCCLUDED`.
    The game drops to the desktop ([shot](s2/fso-forced.png)). The same happens with a layered window, one that
    isn't topmost, and a 210×70 one in a corner. Hotkeys + hook with no visible window leave the game alone (X-010).
  - (b) Holds, and catches more than planned: QUNS = 3 for exclusive mode **and** for FSO; windowed borderless
    gives 2 (BUSY). Exclusive mode shown anyway behaves like mode 2 ([shot](s2/exclusive-forced.png)).
  - (c) Holds: every close path freed 20/20 keys, and then a second process registered all 20. Paths tested: Enter,
    Space, digit, click, click outside, Esc, close timer, step cancel, and the process killed (the OS frees them).
  - (d) Partly: at 175 % the menu is 700 px wide for 400 DIP, correct. 96 DPI was seen only once, when a test
    app's 1024×768 mode briefly switched the panel: 400 px wide, correct. No second monitor, so moving between
    100 % and 175 % monitors is untested (`WM_DPICHANGED` is handled in the probe).
  - (e) Holds: the hook's own time averages 5.6–6.4 µs, p99 ≤ 245 µs, max 245 µs (non-AOT build). The rest of the
    chain (`CallNextHookEx`: the user's AutoHotkey, PowerToys) takes 140 µs on average and up to 1 ms. That cost
    is theirs and exists without us.
  - Also: the run pop-up uses the same kind of window and doesn't check QUNS, so it may kick a full-screen game
    out too (ARCHITECTURE → Open questions).
- Narrator announcements (T12.9) need no spike of their own: they ride on issue #26's provider work.

## 6. Open questions (decide in the task that meets them)
- Battery level in `devices gamepads`: GameInput may expose it; XInput has `XInputGetBatteryInformation` (wireless
  only). Show it if cheap; not a goal.
- Reopening the parent menu on B in a submenu (needs the "opened from" chain in the trigger). v1: B closes.
- A per-flow "show in menus as" label. v1: the flow's name.
