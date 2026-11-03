# Flow calls (M13 P6) — design

A step that runs another flow, so typical things compose into one step, and flow **inputs** so one chain serves
several devices. Designed 2026-11-03 (D-061..D-064), **not built yet**. Tasks: [PLAN.md](../PLAN.md) → M13 (T13.8–
T13.10). Microcopy numbers (#47–#66) are in [UX.md](../UX.md) §5.7. Shares its model with the flow menu
([flow-menu/DESIGN.md](../flow-menu/DESIGN.md), D-054 as amended by D-061).

As with M12 (D-053), each task moves what it built into ARCHITECTURE/UX/FEATURES and cuts it here.

## 1. Scenarios (user, 2026-11-03)

**A. TV ready, then a key.** "Wake the TV; connect to Android if not connected; once connected, send this key."
```yaml
flows:
  tv-ready:                       # a building block; tray: { show: false } keeps it out of the tray
    name: TV ready
    glyph: E8B2
    inputs:
      tv: { kind: android, name: TV, default: living-room }
    nodes:   # android.* steps connect first (with retries, a sleeping TV answers late), so "connect if not
             # connected" is built in; the input switch runs only once the wake worked
      - { id: wake,  step: android.keys,  with: { device: $tv, keys: [wakeup] }, retry: { count: 2, delay: 3s } }
      - { id: input, step: android.input, with: { device: $tv, input: hdmi1 }, after: [wake] }
  tv-home:
    name: TV home
    hotkeys: [Alt+H]
    nodes:
      - { id: ready, step: flow.run,     with: { flow: tv-ready } }          # waits by default
      - { id: home,  step: android.keys, with: { device: living-room, keys: [home] }, after: [ready] }
```
**B. Make it the default, else connect it, else the next device.** "Make this audio device default; if that fails,
connect it over Bluetooth, and if that works make it default; if not, try another device (with the same chain)."
```yaml
flows:
  make-default:
    name: Make it the default
    glyph: E767
    tray: { show: false }
    inputs:
      output:    { kind: audio,     name: Output,           default: technics }
      bluetooth: { kind: bluetooth, name: Bluetooth device, default: technics }
    nodes:
      - { id: set,     step: audio.setDefault,  with: { device: $output }, onFailure: skip }
      - { id: connect, step: bluetooth.connect, with: { device: $bluetooth }, after: [{ id: set, when: failure }] }
      - { id: again,   step: audio.setDefault,  with: { device: $output }, after: [connect] }
  headphones:
    name: Headphones
    hotkeys: [Alt+Z]
    nodes:
      - id: technics
        step: flow.run
        with: { flow: make-default, inputs: { output: technics, bluetooth: technics } }
        onFailure: skip
      - id: sony
        step: flow.run
        with: { flow: make-default, inputs: { output: sony, bluetooth: sony } }
        after: [{ id: technics, when: failure }]
```
Runs: Technics already connected → `set` works, `connect`/`again` are "Not needed" (D-065) → make-default Succeeded →
`technics` Succeeded, `sony` Not needed → **Done**. Technics off → `connect` fails (no fallback, stop) → make-default
Failed → `technics` Failed, handled by `sony` → Sony's chain works → **Done**, "Technics didn't work, so Sony ran
instead." (#48). Both fail → "Didn't finish" with Sony's reason (#56).

## 2. The `flow.run` step (Flow › Run a flow)

### 2.1 Params
- `flow` (Text, required; `Suggest = Config(flows)`, `RefersTo = Flow` — the same ParamSpec extension M12 adds for
  `menu.show` items): the flow to run.
- `wait` (Boolean, default `true`):
  - **true — "Wait for it to finish" (nested run, D-062):** the flow runs *inside* this run, and its result is this
    step's result, so edges branch on it.
  - **false — "Start it" (launch, D-054):** the flow starts the way the tray starts it (own `whenRunning`, run record,
    pop-up; trigger "Flow · Headphones"); the step succeeds once the start was accepted, and this flow carries on.
- `inputs` (TextMap): input name → a Library key of that input's kind, or `$name` of one of *this* flow's inputs of
  the same kind (pass-through). Inputs not given use the called flow's defaults (D-064).
- Descriptor: no `Resources` (D-062: the step holds no lock while it waits); default timeout 10 min ("Give up
  after" covers the whole nested run); retries re-run the whole flow.

### 2.2 Outcome → edges (wait: true; D-062 with D-065)
| Called flow ended | This step | Edges / this run |
|---|---|---|
| Succeeded (incl. a failure its fallback fixed) | Succeeded, summary "Ran Make it the default" | success, always |
| PartiallySucceeded (a may-fail or skipped part) | Succeeded, with `Called.Outcome` = PartiallySucceeded (see below) | success, always |
| Failed | Failed, error #56 (its first failure in its words) | failure, always; policy as any step |
| Ended quietly (a menu inside it: nothing picked, D-054) | Cancelled; this run ends quietly too | none (as any cancel) |
| Timed out / this run cancelled | Failed #57 / Cancelled; the nested run is cancelled (process trees killed) | as any step |
| Not runnable (turned off, never valid, missing) | Failed with #52/#33/#51 | failure, always |
- Start mode: Started → Succeeded; ignored by its `whenRunning` → Succeeded with #58 (not a failure, as for menu
  picks); refused → Failed with the reason.
- **A partial nested result** stays a problem without becoming a failure: the call node's `NodeResult` is Succeeded
  (success edges fire, no failure edge, no policy), and `Outcomes.Summarize` counts a Succeeded node whose
  `Called.Outcome` is PartiallySucceeded as a problem, so the caller ends PartiallySucceeded unless it fails for its
  own reasons (a fixed failure in the caller doesn't hide it). Pop-up "Done, with a problem", row 3 #14's pattern
  with the called flow's name first ("Make it the default: Connect didn't work, so it went on without it."); the
  tray's problem notification and the Runs chip follow the caller's outcome; history keeps both outcomes.

### 2.3 Which version runs
The called flow's runnable version from the current `ConfigSnapshot` with the `Library` that version was validated
with: the file's version if valid, else its last valid version (D-021 applies to an **invalid definition** only). A
turned-off flow has no runnable version and is never run by a call, old version or not: the step fails with #52
(same as menus, #29 "Turned off"); its On switch means "don't run", whoever asks. Building blocks stay out of the tray with the
existing `tray: { show: false }`.

## 3. Nested runs (D-062)

- `FlowRunner.RunNestedAsync(flow, library, inputs, parent, ct)`: a new `Scheduler` and RunId on the same runner, with
  `ParentRunId`/`ParentNodeId` and the call chain. No `FlowLauncher` slot: **`whenRunning` doesn't apply** (it is about
  a person pressing a shortcut twice; two calls of one building block with different devices are normal), and the
  tray's running bullet, Stop and `dctl cancel` act on the top run. The same flow may run nested and top-level at
  once; the resource locks (D-010) keep them off the same device.
- Locks: the `flow.run` step declares none, so it holds none while waiting; each nested step takes its own per
  attempt, exactly as at top level. A step never waits on another step while holding a lock (menus release their
  gate before waiting, D-054), so nesting adds no deadlock.
- Events: RunStarted/NodeStarted/NodeFinished/RunFinished of the nested run carry `ParentRunId` (additive in the IPC
  protocol, `protocol` stays 1). History: **one record** for the top run; the call node's `NodeResult.Called`
  (`CalledRun`: RunId, FlowId, FlowName, Outcome, Nodes) holds the nested results, recursively (≤ 8 deep).
  `HistoryJson` stays source-generated; old lines load (field absent).
- Pop-up (D-062): only the top run's. The call step counts as one step in "n of m"; while it runs, row 3 says the
  nested current step (#59) and the bar advances by the nested fraction within that step's share. The called flow's
  own `popup:` setting doesn't apply while nested. Start mode: the started flow shows its own pop-up as any run.
- Dry run (D-042): doesn't descend; "Would run Make it the default." (#65). Test this step (D-040): runs the called
  flow nested for real (it is the step's effect) with the on-screen config; result as any test.
- Footprint: nothing idle; a nested run costs what a run costs.

## 4. Loops (D-063)

- **Validation** (Core `ConfigValidator`), on the call graph "flow F calls G": every `flow.run` (both modes; a start
  loop would restart itself forever) and every `menu.show` item with `wait: true`. Menu items without `wait` don't
  count (a person picks each time; "Back" items are loops on purpose, D-054).
  - calls its own flow → error #49 on the step; a cycle through others → error #50 on every step in the cycle
    (the flows are disabled, D-021 keeps their last valid versions);
  - unknown flow → error #51; called flow turned off → warning #52; called flow with errors → warning ("needs
    fixing; runs its last working version").
- **Runtime guard** (validation can't see every mix: D-021 keeps an old version of one flow beside a new version of
  another): each run carries its call chain (flow ids from the top run). A call whose flow is already on the chain
  fails at once with #53; a chain longer than 8 fails with #54. Start-mode calls pass the chain on to the launched
  run; triggers by a person (shortcut, tray, menu pick, desktop shortcut, CLI, editor) start a fresh chain.

## 5. Inputs (D-064)

- A flow may declare `inputs:` — name → `{ kind: audio | bluetooth | android | display, name: <label>, default:
  <library key> }`. `default` is required, so a flow with inputs still runs from its shortcut, the tray, a menu or a
  desktop shortcut (with its defaults). Names: `[a-z][a-z0-9-]*`, unique in the flow.
- A step uses an input in a `LibraryRef` param of the same kind: `device: $output`. Validation: unknown input,
  wrong kind, or `$` anywhere else → error on the step. Resource names (`bluetooth:{device}`) are filled from the
  resolved key, so two calls with the same device share the lock and different devices don't.
- The runner resolves `$name` before the step starts: caller's value, else the default. Problems name the input
  ("Output (from Headphones: sony) isn't in your Library anymore.", #7 pattern).
- Library rename (D-046's `LibraryEdits.Rename`) rewrites input defaults and `inputs:` values too; removing a Library
  item that an input's default uses counts as "used by" that flow.
- Not in v1: text/number inputs (a key, a message), inputs on menu items or desktop shortcuts, outputs back to the
  caller. Revisit if the user asks (D-064).

## 6. Editor (UX §5.4, §5.5)

- Palette: area **Flow** › **Run a flow** (glyph: pick in the task from the Segoe Fluent gallery; the pill then shows
  the called flow's own glyph, `FlowGlyph.CodeOf`).
- Pill: title = the step's name, else "Run <flow name>"; summary = the given inputs ("Sony") or "Doesn't wait" in
  start mode; Outline: "Run Make it the default. Flow. Waits for … ." Live: the pill is Running with the nested current
  step as its summary (#59).
- Inspector: **Flow** ComboBox (glyph + name + caption "Takes an output and a Bluetooth device" / "Turned off" /
  "Needs fixing"); its own flow and flows that would make a loop are listed off with #49/#50 as the reason. **Wait for
  it to finish** toggle (#55). **Inputs**: one device picker per input of the chosen flow (Library kind), first item
  "Its default: Technics" (#60), then — if this flow has inputs of that kind — a group "From this flow's inputs".
  **Open this flow** link (leaves with the D-047 prompt).
- Flow settings: **Inputs** section with "Add an input" (#60): rows name, kind (Audio device / Bluetooth device /
  Android device / Screen layout), default (device picker), ×. Removing an input that steps use writes its default
  into them in the same undo step, with tip #61. Device pickers in a flow with inputs show a group "This flow's
  inputs" ("Output · Technics unless the caller picks another").
- A flow open in the editor while it runs nested shows the live states with InfoBar #64 and is locked like any
  running flow.
- Runs page: one entry per top run. In the timeline the call bar has a chevron; expanded, the nested lanes appear
  indented under a bracket labelled with the called flow (like a card's), and selecting a nested bar shows its log
  lines. "Open in editor" on a nested lane opens the called flow showing that nested run.

## 7. Open questions (decide in the task that meets them)
- Show nested runs as their own (dimmed) rows on the Runs list too? v1: no, they live in the parent's detail.
- A flow that has inputs but is used only by calls: hide its On switch? v1: no, it behaves like any flow.
