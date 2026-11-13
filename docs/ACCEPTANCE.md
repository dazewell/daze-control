# Acceptance: the live checks in one sitting

One ready config, one sitting (about 60-75 min, 25 min for the quick part). Nothing here edits your installed config.
Answer with the **Report** table at the end, e.g. "all pass except 4 and 7".

## Start (3 lines)

```
pwsh scripts/accept-run.ps1
# ...do the checks below...
pwsh scripts/accept-run.ps1 -Restore
```

`accept-run.ps1` backs up `%LOCALAPPDATA%\DazeControl\dev\config.yaml`, copies `samples/acceptance/config.yaml` over it, stops
your installed engine and starts this checkout (engine + editor) on the kit. `-Restore` closes the dev editor, puts the
previous dev config back and starts the installed app again. Add `-KeepEdits` on a second run to keep what you changed.
First run builds the engine (a few minutes).

Rules: flows named **LIVE-RISK** touch real devices and run only when you start them. Every other flow only shows
messages, waits, checks or asks. Hotkeys are `Ctrl+Alt+F9` / `Ctrl+Alt+F10`. Flows 13a-13f start **off**. Windows
settings you change (text size, contrast, theme) must be put back at the end (checklist after check 16).

How to read a line: **Do** what to click · **Pass** one sentence · **Fail** what to tell me (a few words are enough).

## For a walkthrough session (Claude drives, the user answers)

Run `pwsh scripts/accept-run.ps1` once (it prints the kit's flow names and hotkeys), then go through the checks in order,
one at a time: say the number and the flow to open, give the **Do** actions, ask "pass or fail?" and if fail "what did you
see?". Record one line per number in the Report table; on a fail, note its **Blame** line. Never run a LIVE-RISK flow
without asking. At the end: Cleanup, `-Restore`, post the filled Report and the failing numbers with their Blame lines.
Each check is `**N title** (flow ...)` then Do, Pass, Fail, Blame, so it can be iterated mechanically.
Computer use cannot see the engine's pop-ups, the tray menu, dropdown lists or Windows settings: ask the user for those (do not guess).

## Quick checks (flows open in the editor; no setup)

**1 Pan and zoom** (flow "1 Pan and zoom: 95-step probe"; open it, press Fit)
- Do: right-drag on empty canvas; middle-drag; hold Space and left-drag; mouse wheel (zoom under the pointer);
  touchpad two-finger pan and pinch zoom; a still right-click on empty canvas.
- Pass: every gesture pans/zooms smoothly, the point under the pointer stays put, a still right-click opens the menu,
  Fit shows the whole flow.
- Fail: name the gesture + device (mouse/touchpad), what happened (nothing, jumps, stuck after release).
- Blame: `Editor/Canvas/FlowCanvas.Pointer.cs`, `FlowCanvas.Camera.cs`, `Camera.cs` (D-119, D-126; T21.9, T22.1, T22.12).

**2 Join layout** (flow "2 Join layout 3 in -> 1")
- Do: Arrange (try every Layout choice); drag one input step away; Undo; press Fit.
- Pass: the 3 inputs sit in one column and the join step lines up in the next one, lines attached, gaps even, Undo
  glides back.
- Fail: which layout choice, what overlaps or detaches.
- Blame: `Core/Model/FlowLayout*`, `FlowPlacement`, `EdgeCurve`; `Editor/Canvas/FlowCanvas.Edges.cs`.

**3 Step values: Show message** (flow "3 Step values: Show message"; click the last step, "Show message")
- Do: click **Insert value** next to the message box: pick "Wi-Fi network"; the caret stays and the box regains focus.
  Run the flow: the pop-up shows "Battery …% on <your Wi-Fi>, flow … at …". Then: delete the "Check Wi-Fi" step and press
  Undo once; duplicate the "Check battery" step and paste it (Ctrl+C / Ctrl+V).
- Pass: the flyout lists only earlier steps; the "Shows:" pills row appears without the layout jumping; after Delete the
  text shows its fallback words, one Undo brings the pill back; the pasted copy's pill points at the copy.
- Fail: which action (Insert, Delete, Undo, Duplicate/paste) and what the text/pills showed.
- Blame: `Editor.ViewModels/Inspector/ValueText`, `ParamFieldViewModel*`; `Core/Model/StepValues`, `FlowCommands` (T23.3/T23.4).

**4 Pick device default** (flow "4 Pick device default"; click "Pick a device")
- Do: open the **Default** picker; choose another device; save; close the flow and reopen it. Run it, pick one.
- Pass: the picker lists your devices (not only the fake one), the choice survives reopening, the run's last pop-up says
  "You picked …".
- Fail: what the picker listed, what was blank after reopening.
- Blame: `Editor/Inspector/*` Pick fields (D-128); `Core/Model/PickResults`.

**5 Text input and flow output** (flows "5 Text input and flow output" and "5b Text callee")
- Do: run flow 5.
- Pass: two pop-ups, 5 s apart (D-132): "Callee got: Hello from flow 5", then "Callee said: Hello from flow 5 (received) | <date time>".
- Fail: the exact text of the second pop-up.
- Blame: `Core/Model/FlowOutputs`, `StepValues`; `Runtime/FlowRunner.cs` (D-131, T23.7).

**6 Make text** (flow "6 Make text")
- Do: run it.
- Pass: two pop-ups, 5 s apart, "One: Kit run …" and "Two: Kit run …" show the same date, time and flow name.
- Fail: the two texts.
- Blame: `Core/Model/StepValues`; `Runtime/FlowRunner.cs` (T23.7).

**7 Wi-Fi network dropdown round trip** (flow "7 Wi-Fi network dropdown round trip"; click "Check Wi-Fi")
- Do: look at the Network box (it holds "Daze Not In Range", a network that doesn't exist). Pick your real Wi-Fi in it,
  save, close and reopen the flow; then pick "Daze Not In Range" again by typing it if the list allows.
- Pass: the unknown name shows as "Not available right now", never blank and never rewritten; your real choice survives
  reopening.
- Fail: what the box showed after reopening (blank? changed?).
- Blame: inspector param box (X-014); PR 234 `fix/param-roundtrip`.

**8 Tray folders** (flows 8a, 8b in "Kit A", 8c in "Kit B")
- Do: right-click the tray icon; open Kit A, run 8a; open Kit B. In the Flows list collapse/expand a group header, use
  its `…` menu (Rename…, Turn all off), then Turn all on.
- Pass: two sub-menus Kit A (2 flows) and Kit B (1 flow); running one shows its pop-up; the tray menu changes at once
  after the group is turned off or on; the collapse state survives closing and reopening the editor.
- Fail: what the tray menu showed vs the Flows list.
- Blame: `Runtime/Tray/*`; flow list groups in `Editor/ShellWindow.xaml(.cs)` (M16, D-087).

**9 Hotkey F-chord flow**
- Do: press `Ctrl+Alt+F9` anywhere (try with another app focused).
- Pass: pop-up "Hotkey Ctrl+Alt+F9 fired" and the other app keeps focus.
- Fail: nothing / focus stolen / which message.
- Blame: `Engine/Input/*`, `Core/Model/Hotkey*`.

## Menus, prompts, triggers

**10 Controller menu** (flow "10 Controller menu", hotkey `Ctrl+Alt+F10`; also "10b Menu as a list")
- Do: press `Ctrl+Alt+F10`; move with arrow keys (or your pad: D-pad/stick, A to pick, B to cancel); pick one; press
  again and cancel with Esc; run 10b from the editor.
- Pass: grid appears without taking focus, the highlight slides smoothly (about a third of a second, no stutter,
  consistently smooth rather than choppy), picking runs that flow's pop-up, cancel ends it quietly.
- Fail: which input (pad/keys), what felt choppy or slow, whether a game/app lost focus.
- Blame: `Runtime/Menus/*`, `Engine/Input/*`; docs/flow-menu.

**11 Injection-shaped device name** (flow "11 Injection-shaped device name")
- Do: run it and pick the Bluetooth item named `'; $(calc) "q" %TEMP%` (it's the default after 60 s).
- Pass: the last pop-up shows the name as plain text three times (name, "PS got: …", "cmd …") and **Calculator never opens**.
- Fail: calc opened, or any text was cut or changed (paste the pop-up).
- Blame: `Core/Model/StepValues` (`EnvironmentName`, `PercentGuard`); `Providers/Process/*` (D-129/D-130).

**12 Ask me** (flow "12 Ask me: yes/no and choose")
- Do: run it from the tray while another app (a video, a game window, a browser in F11) is in front; answer with the mouse,
  then run again and let it time out.
- Pass: the question shows over the app without taking focus, timing out uses the default (No / Focus) and the follow-up
  message says so.
- Fail: focus was stolen / no prompt / wrong default.
- Blame: `Runtime/Popup/*`, `prompt.ask` provider (M20, D-107).

**13 Schedule and events (M20 live check)** (flows 13a-13d are off; in the Flows list turn on the ones you test)
- Do: (a) turn on **13a**, wait for "13a schedule fired" (about 1 min); then put the PC to sleep for 3+ minutes with 13a on
  and wake it. (b) turn on **13b** and wake the PC from sleep/Modern Standby. (c) turn on **13c** and toggle Wi-Fi off and on,
  and plug or unplug a network cable with Wi-Fi up. (d) turn on **13d**, lock (Win+L) and unlock. The Flows card for a
  flow with a trigger shows a clock/bolt chip; the "When this runs" card shows a plain sentence and "Next: …"; its
  "Test this trigger" runs the flow. Turn all four off after.
- Pass: each fires once with its pop-up; the schedule runs again after wake; the cable event fires even with Wi-Fi up;
  no wording says trigger/cron/node.
- Fail: which of a/b/c/d, what fired or didn't, the time.
- Blame: `Runtime/Scheduling/*`, `Runtime/Triggers/*`, `Engine/Triggers/*` (T20.2-T20.12).

## M24 quick wins (flows 13e, 13f, LIVE-RISK 24 and 25, the dev showcase)

Everything here was tested **with fakes only** (nothing ran against real hardware or the real shell). One line each:
- Stop flow: showcase flows "Guard then stop" and "Decide on an answer" (answer Off): no pop-up after the stop, history "Stopped here"; the failed variant says "Needed folder is missing".
- Check value: "Decide on an answer": Loud and Quiet take different branches; `dctl check value.check value=5 is=atLeast to=3` exits 0; a word where a number is needed says so.
- Events: turn on **13e**, turn the screens off and on again: one pop-up per change, none at start; stay away until Windows says away and come back. Turn on **13f**, plug a USB device in and out: the pop-up names it.
- Open file or address: run "LIVE-RISK 24 Open a folder" (opens `%WINDIR%` in Explorer); then try `https://` and `ms-settings:display` as the target in the inspector; a missing file fails with a sentence.
- Control media (not in the kit): add it to a flow with music playing; Play or pause, Next and Previous act on the player, no key is pressed.
- Wake over network: edit "LIVE-RISK 25" to your sleeping PC's hardware address and `host:port`; it wakes a real PC and the second step waits until it answers and shows the time in ms.
- Check address: `dctl check network.reach address=nas:445` (or a web page, a host) exits 0 / 1; no secret in a failure sentence.
- Inputs: `dctl run <flow> name=value` with a Library key (device input) and with text; a wrong name exits 2 with the flow's input names; a Stream Deck key calling `dctl.exe run ...` (FEATURES F8 recipe).

## Canvas gate (M21)

**14 Drag, drop, auto-pan, Arrange** (flow "14 Arrange and shapes: Morning check", plus flow 1)
- Do: drag a step, a card and a 3-step selection (marquee then drag); drop a library step from the Steps pane onto the
  canvas at 50 %, 100 %, 200 % zoom and onto a step; drag a step or a connect line toward the canvas edge (hold 2 s) and
  past it; Esc during a drag; Arrange then Undo; in flow 1 drag above the first row and to the right edge, Fit.
- Pass: dots, lines and arrowheads stay attached every frame and releasing moves nothing; the drop lands under the pointer;
  near the edge the canvas pans faster the nearer you are, stops away from it, never more than one screen beyond the last
  step; Esc cancels; Arrange and Undo glide with lines attached; with Windows "Animation effects" off there is no motion.
- Fail: which gesture + zoom + what moved wrongly.
- Blame: `Editor/Canvas/FlowCanvas.Pointer.cs`, `Camera.cs`, `FlowCanvas.Motion.cs` (T21.9).

## Windows settings and window shape (one pass over flows 1, 3, 8, 14)

**15a Light theme**: Windows Settings > Personalization > Colors > Light. Pass: pills, chips, edge labels readable;
caption buttons (minimize/maximize/close) clearly visible.
- Blame: `Editor/ShellWindow.xaml(.cs)` (F-034, F-039, F-021, F-022, F-041), `Canvas/NodeViews.cs`.
**15b High contrast**: Settings > Accessibility > Contrast themes. Pass: steps/pills/"Shows:" row/chips readable, no clipped
text, run states distinguishable without colour. (Put back to None after.)
**15c 225 % text**: Settings > Accessibility > Text size 225 %. Pass: pills grow taller, nothing clips on cards, the
inspector, the pop-up (run flow 3) or the flows list rows. (Put back to 100 % after.)
**15d Narrow window**: drag the editor to its minimum width. Pass: the Steps pane hides, a flow card keeps its name,
the canvas keeps most of the width. Fail wording: which page, what is cut.
**15e Cards and menu**: right-click a flow card: Move to, shortcuts shown (Alt+Up/Down). The card's buttons read
"Run <name>", not "Run Run others". Pass if sensible. (Narrator not needed.)

## Library Test buttons on real devices (touches real devices: ask yourself first)

**16 Library Test** — in the Library, the kit's items are fakes (Audio "Edit me: sound device A/B", Bluetooth
"Edit me: your Bluetooth device"). Add your real device by hand (or use the "Add" paths) and press **Test** on one audio,
one Bluetooth, one display item.
- Pass: Test says plainly what it did, changes nothing permanent, and the card shows live state.
- Fail: the item type and the sentence Test showed. Skip a type you don't have and write "n/a".
- Blame: `Editor/Library/*`; `Providers/Audio|Bluetooth|Display`.

## LIVE-RISK (opt in; change real devices; skip if you'd rather not)

Before: lower the volume and turn off the speakers' loud sources. Note your current default sound device.
Edit the Library: Audio `accept-a` = your current default (type part of its name), `accept-b` = another device you own;
Bluetooth `accept-bt` = a device you can connect safely.

**17 Show message with a Bluetooth device name**: run "LIVE-RISK 17 …".
Pass: pop-up "Connected to <the Library name of your device>"; unplug it and run again: "Connected to nothing (it failed)".
- Blame: `Providers/Bluetooth`, `Core/Model/StepValues` (D-130).
**18 Switch back to the previous default sound device**: run "LIVE-RISK 18 …".
Pass: the default switches to `accept-b` for a moment, then back to what it was; the pop-up said "Was <that device>".
Fail: the pop-up text, the default after the run. After 17/18 set the Library items back or delete them, check the default
sound device and volume are as you had them.
- Blame: `Providers/Audio`, `Core/Model/StepValues` (D-130).

## Cleanup

Windows: theme back, contrast None, text size 100 %. Then `pwsh scripts/accept-run.ps1 -Restore`.

## Report (copy, fill in, paste back)

```
1  pan/zoom mouse+touchpad+pinch   P/F/n-a  note:
2  join + arrange                   P/F/n-a  note:
3  step values / pills / remap      P/F/n-a  note:
4  pick device default              P/F/n-a  note:
5  text input + flow output         P/F/n-a  note:
6  make text                        P/F/n-a  note:
7  wi-fi dropdown round trip        P/F/n-a  note:
8  tray folders                     P/F/n-a  note:
9  hotkey F-chord                   P/F/n-a  note:
10 controller menu feel             P/F/n-a  note:
11 injection-shaped name            P/F/n-a  note:
12 ask me, no focus steal           P/F/n-a  note:
13a schedule / sleep-wake rerun     P/F/n-a  note:
13b wake from Modern Standby        P/F/n-a  note:
13c wi-fi + cable events            P/F/n-a  note:
13d unlock                          P/F/n-a  note:
14 drag/drop/auto-pan/arrange       P/F/n-a  note:
15a light  15b contrast  15c 225%  15d narrow  15e cards:   (P/F each)
16 library test (audio/bt/display)  P/F/n-a  note:
17 bt name in message               P/F/n-a  note:
18 switch back default sound        P/F/n-a  note:
```

## Where a failing number goes (for the coordinator)

| Number | Area | Look in |
|---|---|---|
| 1, 14 | canvas camera, input, drop, auto-pan | `Editor/Canvas/FlowCanvas.Pointer.cs`, `FlowCanvas.Camera.cs`, `FlowCanvas.Motion.cs`, `Camera.cs`; D-119, D-126; T21.9, T22.1, T22.12 |
| 2 | layout / arrange / join | `Core/Model/FlowLayout*`, `FlowPlacement`, `EdgeCurve`; `FlowCanvas.Edges.cs` |
| 3 | Insert value, pills, remap | `Editor.ViewModels/Inspector/ValueText`, `ParamFieldViewModel*`; `Core/Model/StepValues`, `FlowCommands` (Delete/Paste); T23.3/T23.4 |
| 4 | Pick device Default picker | `Editor/Inspector/*` Pick fields, D-128; `Core/Model/PickResults` |
| 5, 6 | text inputs/outputs, Make text | `Core/Model/FlowOutputs`, `StepValues`, `Runtime/FlowRunner.cs`; D-131; T23.7 |
| 7 | saved values the live list lacks | inspector param box, X-014; PR 234 (`fix/param-roundtrip`) |
| 8 | tray folders | `Runtime/Tray/*`, flow list groups in `ShellWindow.xaml(.cs)`; M16, D-087 |
| 9 | hotkeys | `Engine/Input/*`, `Core/Model/Hotkey`; F-chord rule in PLAN notes |
| 10 | controller menu | `Runtime/Menus/*`, `Engine/Input` (gamepad); docs/flow-menu |
| 11 | values vs scripts / program args | `Core/Model/StepValues` (`EnvironmentName`, `PercentGuard`), providers `Process/*`; D-129/D-130 |
| 12 | prompts / pop-up focus | `Runtime/Popup/*`, `prompt.ask` provider; M20, D-107 |
| 13a-d | schedule and event triggers | `Runtime/Scheduling/*`, `Runtime/Triggers/*`, `Engine/Triggers/*`; T20.2-T20.12 |
| 15a-d | theme, contrast, text scale, narrow | `ShellWindow.xaml(.cs)` (F-034, F-039), `NodeViews.cs`, UX §5.9 |
| 15e | card names and menus | `ShellWindow.xaml` card template (F-021, F-022, F-041), `FlowCard.cs` |
| 16 | Library Test | `Editor/Library/*`, `Providers/Audio|Bluetooth|Display` |
| 17, 18 | device values in messages / `previous` | `Providers/Bluetooth`, `Providers/Audio`, `Core/Model/StepValues`; D-130 |
