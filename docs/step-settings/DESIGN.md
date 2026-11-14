# Step defaults, checks and "Only if" (M14) — design

Features (Parts A–D), designed 2026-11-04, **not built yet**. Decisions: D-080 (defaults), D-081..D-082 (conditions).
Tasks: [PLAN.md](../PLAN.md) → M14. Strings below are drafts; the task that builds a part moves the as-built facts
into ARCHITECTURE/UX/FEATURES and cuts them here (D-053). User text never says node, DAG or OSD.

# Part A — Default timing and failure handling per step type

## A1. Scenario and scope
User (2026-11-04): "give up after 1 s for every *Set default device* step" without repeating it on each step.
Covers the four per-step knobs of the inspector: **Try again** (retry count), **Wait between tries** (retry delay),
**Give up after** (timeout) and **If it fails** (stop / skip / ignore). Out: defaults per area ("all Bluetooth
steps") or per flow, defaults for cards (a card has no timeout or retry; its policy stays per card), defaults for
step *params* (a different feature), per-PC overrides.

## A2. Where they live: `settings.stepDefaults` in config.yaml (D-080)
The engine must honour them, and the engine reads only config.yaml (`editor.json` is this PC's editor view state, D-068).
Keyed by step id; every field optional:
```yaml
settings:
  popup: { position: bottomCenter, seconds: 3 }
  stepDefaults:
    audio.setDefault:   { timeout: 1s, retry: { count: 2, delay: 500ms } }
    bluetooth.connect:  { timeout: 30s, onFailure: skip }
    android.input:      { retry: { count: 1 } }          # delay omitted = inherits the next level
```
No schema bump (`version: 1`): the key is additive and a file without it means what it meant before.

## A3. Precedence, per field
`step's own value` > `stepDefaults[type]` > `built-in`. Each field resolves alone (a step may set only the
count and inherit the delay).

| Field (YAML) | Inspector label | Built-in |
|---|---|---|
| `timeout` | Give up after | descriptor's `DefaultTimeout` |
| `retry.count` | Try again | 0 |
| `retry.delay` | Wait between tries | today's `RetryPolicy` delay default (the task confirms the number in code) |
| `onFailure` | If it fails | `stop` |

- A step's own value is stored only when the user set it. Model: `StepNode.Timeout?` (exists), `Retry` becomes
  two nullable fields, `Node.OnFailure` becomes `FailurePolicy?` (null = inherit). YAML omits null; an explicit
  `onFailure: stop` is **kept** when written (today's save drops it as "default"), so "Stop the flow" can override a
  `skip` default.
- Resolution is one pure function, `StepSettings.Resolve(node, descriptor, defaults)` in Core. The runner calls it
  once per run (`StepDefaults.Apply(flow, defaults)` returns the flow with every field filled, like
  `FlowInputs.Apply`), so the Scheduler, retries and timeouts stay unchanged. The defaults are captured with the
  library at trigger (D-021): a default edited mid-run changes the next run only.
- Dry run and "Test this step" send the editor's on-screen `stepDefaults` with the config text (D-040, D-042).
- Existing steps with no explicit value **follow** the default as it changes (that is what inherit means). A step
  with an explicit value never moves.
- Run history keeps what happened (attempts, timeouts), not the settings, so old runs read the same.

## A4. Validation (settings problems never disable a flow; they drop the bad part)
| Case | Result |
|---|---|
| Unknown step id (a card/`group` is not a step id either) | Problem "`xyz` isn't a step type"; entry dropped |
| `timeout` ≤ 0 or > 24 h, `retry.count` outside 0–10, negative delay, unknown `onFailure` | Problem; that field dropped, the rest of the entry kept |
| Resolved timeout shorter than a step's own duration (`wait.duration`, `menu.show` closeAfter) | The existing flow error, on that flow, using the resolved value |

A default timeout can break many steps at once (`process.run: { timeout: 1s }`): the Settings page shows "Used by n
steps" before the user saves (A5); nothing is blocked.

## A5. Editor
**Inspector (step selected), Timing and "If it fails" sections.** Each of the four rows is in one of two states:
- *Default*: the value is shown in secondary text colour with a small "Default" label; below the section a Caption
  says "From the default for Set default device steps" when the type has an entry, else nothing (built-in values
  need no explanation).
- *Set*: normal text and a "Reset" link (icon E777) with tooltip "Use the default (1 s)". Reset removes only that
  field from the step. Typing or picking a value always sets it, even when equal to the default.
- Section footer button **"Use these for all Set default device steps…"**: a flyout lists the four values, "Applies
  to n other steps in m flows that don't set their own", and Apply / Cancel. Apply writes the step's effective values to
  `stepDefaults[type]`, removes nothing from steps, and shows an InfoBar "Saved as the default for Set default
  device steps" with Undo (the D-076 pattern). Steps that set the same value as the new default stay "Set".
- The "If it fails, run…" menu's switch from Stop to Skip (D-059) uses the *effective* policy: if the effective
  policy is Stop (own or inherited), the editor **sets** Skip on the step.
- Canvas chips (retries ×3, timeout 30 s, skip/May-fail badges) show **effective** values, dimmer when inherited,
  and only when different from the built-in, so a 20 s Bluetooth timeout stays chip-free.

**Settings page, new card "Step defaults"** (below the pop-up card): a list, one row per type with an entry: glyph,
title ("Bluetooth › Connect"), summary ("Give up after 30 s · Skip what depends on it"), "Used by 5 steps in 2
flows", Edit (flyout with the same four rows and Reset), Remove. "Add a step type…" opens the palette's
area-grouped list (types without an entry). Empty state: "Set how long and how often steps try, once for every
step of a type." Autosave 300 ms like the rest of Settings.

## A6. Migration and edge cases
- Nothing migrates: files without `stepDefaults` load, run and save byte-identical (a test saves `full.yaml` and the
  user's `samples/user-config.yaml` and diffs them). Hand-written `onFailure: stop` stays as explicit.
- A step type removed from the product leaves its entry as a Problem + dropped (a newer file opened by an older
  engine loses the entry on save; accepted).
- `cad validate` and the Problems list report settings problems under "Settings".
- `flow.run`/`menu.show`: defaults apply to the calling step only; the called flow's steps resolve their own.

**Not done:** defaults per area/flow, conditional defaults, defaults for params, a CLI to edit them (edit YAML).

# Part B — Checks and "Only if" on steps

## B1. Scenario and scope
User (2026-11-04): (a) *Check* actions per step type, e.g. "is this Bluetooth device connected?": go on when yes,
fail when not. (b) An **Only if…** setting on action steps: Connect "Only if it isn't connected / Always", so the
step is skipped straight to the next when the device is already connected, and "Always" forces it.
Scope: one small, shared concept; no general if/else (B9).

## B2. Model: one "fact" probe, two ways to use it (D-081)
A provider area may implement **fact probes**: `IFactProbe { string Fact; ReadAsync(params, ct) → (bool Value,
string Text) }`, e.g. `bluetooth.connected` → `(true, "Technics is connected")`. Probes only read (no side effect),
run only when a step that uses them runs, and use the same queries as the editor's pickers (device states,
D-052), so there is no resident object or timer (idle = zero).

1. **Per-step condition, `only:`** on an action step. The descriptor lists `Conditions` (id, label, probe fact,
   expected value). `only` absent or `always` = run. Otherwise the step runs when the condition holds and is **skipped
   with a note** when not.
2. **Check step**: a normal step type per area (`bluetooth.check`, ...) that reads the same fact and **fails when
   the fact isn't what the user chose**. It composes with everything existing: failure edges give the "else"
   branch (D-024/D-065), retries make it "wait until connected", `flag.check` is the precedent.

| | `only:` on the action | Check step |
|---|---|---|
| Unmet means | step skipped, flow carries on (Succeeded + note) | step **fails** (Failed, failure edge fires) |
| Use for | "don't redo what's done" | "go on only if", branching, "wait until" (with Try again) |
| Cost | none extra in the graph | one more step |

Both are needed: `only:` is the quiet no-op the user asked for; a check is the way to *branch or stop*.

## B3. YAML
```yaml
nodes:
  - { id: bt,  step: bluetooth.connect, with: { device: technics }, only: disconnected }   # skip if already connected
  - { id: bt2, step: bluetooth.connect, with: { device: sony }, only: always }             # explicit "always" (matters once defaults exist, B8)
  - { id: is,  step: bluetooth.check,   with: { device: technics, is: connected }, onFailure: skip }
  - { id: out, step: audio.setDefault,  with: { device: technics }, after: [is] }
  - { id: alt, step: audio.setDefault,  with: { device: speakers }, after: [{ id: is, when: failure }] }
```
`only` is a scalar id from the step's descriptor `Conditions`. Later it can also take `{ check: <step id>, with: {…} }`
(B9) without breaking the scalar form.

## B4. Semantics
- **Evaluation:** at the start of the step's first attempt, after the resource lock is taken (so a concurrent step on the
  same device is serialised and the answer is fresh), inside the attempt's timeout. Once per step, not per retry.
- **Cannot tell** (probe throws, times out, device missing): log it and **run the step** as if `always`; its own
  validation and errors then speak. Never skip on doubt.
- **Skipped by condition = Succeeded with a note** (`NodeResult` note, e.g. "Technics was already connected"; reuse
  `Summary` if it fits). Not `NodeOutcome.Skipped` (that fires only `always` edges and skips dependents, F1) and not
  `NotNeeded` (D-065, fallback bookkeeping). So `success` dependents run, `failure` fallbacks don't fire, the flow is
  Succeeded, the pop-up counts it as done and says nothing extra (D-081).
- **Check unmet = Failed** with the probe's text as the error ("Technics isn't connected"), after the step's retries.
  Policy, fallback and `onFailure` behave exactly as for any failure (and Part A's defaults can set `onFailure: skip`
  for all checks). The pop-up sentence for a handled failure (#48, "X didn't work, so Y ran instead") needs a variant
  for checks: "Technics isn't connected, so Speakers ran instead".
- **Cards:** a member skipped by condition is Succeeded for the card.
- **Dry run:** assumes every `only` is met and every check passes (dry steps do nothing, and the preview is the full
  path). The canvas shows the dry result as normal; the inspector row says "Decided when it runs".
- **Test this step (real, D-040):** runs the probe for real: a skipped step ends "Skipped: Technics was already
  connected" and does nothing; a check says "Yes: Technics is connected" or "No: Technics isn't connected" (a normal
  answer, not an error InfoBar).
- **Run history / pop-up / tray:** a skipped step is a Succeeded record with the note; no failure text anywhere.
  A run whose only changes were skips is still "Done".

## B5. Which steps get which check (survey of the catalog, kept cheap)
All probes are read-only calls the provider already makes or the pickers already show; none starts a process except
the last row.

| Area | Fact | `only:` on | Check step | Cost |
|---|---|---|---|---|
| Bluetooth | device connected | Connect → `disconnected`; Disconnect → `connected` | `bluetooth.check` "Check connection": *Connected / Not connected* | paired-device state, as the picker |
| Audio | device is the default output/input (role) | Set default device → `notDefault` | `audio.check` "Check default device" | endpoint enumeration on the existing lazy COM worker |
| Process | a program is running (process name from the exe) | Run program → `notRunning` (only when the exe name is known) | `process.check` "Check a program is running" | `Process.GetProcessesByName` |
| Service | running / stopped | Start → `stopped`; Stop → `running` | `service.check` "Check a service" | `ServiceController.Status` |
| File | a file or folder exists / is missing | — | `file.check` "Check a file or folder" (new area "File") | `File.Exists` |
| Flag | flag is on/off | — | `flag.check` (exists; unchanged) | none |
| Display | the screens match a saved layout / outputs entry (reuses `DisplayCheck`, D-077) | outputs, layout → `notApplied` | `display.check` | CCD query; later task |
| Android | adb reports the device connected | — | `android.check` | spawns adb: last, optional |

Not given a check: `audio.setVolume`/`mute` (already cheap to redo), `display.topology` (no "current preset"),
`script.powershell` / `http.request` (arbitrary; a script already fails with an exit code), `wait`, `notify`,
`menu.show`, `flow.run`, windows (title matching is ambiguous; "program running" covers the need).

## B6. Editor
- **Only if** row at the end of the step's Settings section, only for types with `Conditions`. ComboBox: "Always
  (default)" and the conditions: Connect → "It isn't connected"; Disconnect → "It's connected"; Set default device →
  "It isn't the default"; Run program → "It isn't running". Help text: "Otherwise this step is skipped and the flow
  carries on." Choosing "Always" stores `only: always` when the type has an `only` default (B8), else removes the key.
- **Check steps** appear in the palette under their area ("Bluetooth › Check connection"); the inspector shows the
  device picker and an *Is* ComboBox ("Connected / Not connected"). The "If it fails" section is relabelled "If it
  isn't" for check steps; an added "If it isn't, run…" is the existing fallback menu. The palette calls them "Check",
  not "Test", because "Test this step" already means running a step.
- **Canvas:** a step with `only` gets a small chip "Only if…" (icon E71C) next to the retry/timeout chips.
  After a run, a condition-skipped step shows the Succeeded ring **dashed** (not the grey Skipped ring) with the
  check mark, and a Caption under the pill "Skipped: already connected". Nothing red or caution-coloured.
- **Runs page:** hatched success-coloured bar; log line "Skipped: Technics was already connected". A check's unmet
  line reads "No: Technics isn't connected" (red only if nothing handled it).
- **Accessibility/high contrast:** the dashed ring + caption carry the meaning, not the colour (UX §5.3 rule).

## B7. Validation
`only` not in the descriptor's `Conditions` → flow error naming the step ("Connect can't be 'Only if it's running'").
`only` on a card, or on a type with no conditions → flow error. A check step's `with:` validates like any step; a
missing library device behaves as for any step today.

## B8. Interaction with Part A
`stepDefaults` gains an `only` field once conditions exist (T14.10): `bluetooth.connect: { only: disconnected }`
makes every Connect skip when connected, and a step that must always run sets `only: always`, which is why
`always` is an explicit, storable value. Built-in is `always` for every type: **no existing config changes meaning**.
Check steps take the same timing defaults (a check is a normal step type, default timeout 10 s).

## B9. What is NOT done, and how it would extend
- No general if/else, no `else:` keys, no expressions. Branching already exists: check step + `success` and
  `failure` edges (a two-sided choice is one check, two dependents). A quiet "stop here if unmet" (no red failure)
  would be a check policy "Stop quietly" reusing the quiet end `flow.run` has (D-062); parked.
- Later, `only: { check: bluetooth.check, with: { device: sony, is: connected } }` lets any step depend on any
  check; the scalar form stays as the shorthand. Not in M14.
- No polling "wait until" step: a check with Try again/Wait between tries is it, bounded by the timeout.
- No condition on cards or whole flows (flows already have `whenRunning`; a leading check step does the rest).

# Part C — Drop on the left of a step: "Run before" (D-083)

User (2026-11-04, via the M13 session): drag a step onto the **left** part of another step to insert it *before*
that step, mirroring the right third's "Run after" (UX §5.2). Small, independent of Parts A and B.

- **Zones** (`FlowCanvasViewModel.ZoneAt`): left third → new `DropZone.RunBefore`; right third → `RunAfter` (as now);
  middle keeps "Run together" with its top/bottom bands; a card's left/right thirds work like a pill's, its body is
  "Run together". Highlight: the same 2 px accent bar as the right third, mirrored, over the left third, label
  "Run before" (§5.7 string, mirrors "Run after"). A refused drop shows no highlight (as today).
- **Rewiring** `FlowEdits.RunBefore(flow, dragged, target)` (a card target is its unit, a dragged member is taken
  out first, exactly like `RunAfter`):
  | Step | After the drop |
  |---|---|
  | dragged | waits for what the target waited for: **all** of the target's incoming edges, kinds kept (`success`, `failure`, `always`), replacing the dragged step's own incoming edges |
  | target | waits only for the dragged step (`success`) |
  | dragged's old dependents | unchanged (still wait for it), same as `RunAfter` leaves them; Delete's re-linking is not applied |
  | others that waited for the target | unchanged |
  A `failure` link into the target moves to the dragged step: the dragged step becomes the fallback and the target
  runs after it; the policy tip for a new fallback (D-059) applies to the source as for any new fallback.
  A removed incoming edge of the dragged step that was `failure`/`always` is named in a tip like #45.
- **Cycles:** refused with the loop tip when the new edges close a loop (e.g. the dragged step has a dependent that the
  target's predecessors wait for); nothing changes. Dropping onto itself or onto a step of its own card: no zone.
- **Undo:** one step (one `Drop` call, one history entry), like the other zones.
- **Keyboard/Narrator:** pointer-only gestures need the equivalent: the step's context menu and the Outline's gain
  **"Run before…"** (and **"Run after…"** if missing), opening the same step picker as "If it fails, run…" (no loops
  offered); the Outline reads "Waits for …" after the change. No new shortcut.
- **Tests:** `FlowEdits.RunBefore` (first step, middle step, target with `failure`/`always` incoming, target in a card,
  dragged member of a card, loop refusal, same-card no-op), `ZoneAt` thirds, `Drop` undo as one step, Outline text.
- **Not done:** carrying the dragged step's dependents along, dropping before a whole branch.
# Part D — Edit several steps at once (M15, D-084)

User (2026-11-04, via the M13 session): select several steps and change the settings they share in one go. Comes
after M14: it reuses its Default/Set rows and the "Only if" row.

## D1. What exists
Selection already works (UX §5.2): click, Ctrl+click, marquee, Ctrl+A; `FlowCanvasViewModel.SelectedIds`. With more than
one selected the inspector is in `InspectorMode.Many` and shows only "n selected" (`ManyText`). Nothing to add to
selection except one optional shortcut: context menu **"Select all <type> steps"** (T15.2).

## D2. What the inspector shows for a selection
| Selection | Shown |
|---|---|
| Steps of one type | "If it fails", Timing (Try again, Wait between tries, Give up after), "Only if" (when the type has conditions), and every provider param (T15.3) |
| Steps of several types | "If it fails", Timing, and params only when name, kind and library kind (`RefersTo`) are identical on every selected type (e.g. a text `name`) |
| Steps and cards | "If it fails" only (cards have no timing or params) |
| Cards only | "If it fails" only |

Header: "3 steps selected" (plus " · Set default device" for one type); it never lists ids. "Test this step", fallback
menus and Connections stay single-step. Params that need a per-step choice (`device` picks differ by step) show Mixed
and are left alone until the user picks one value for all.

## D3. Row states (extends A5)
Each row looks at the selected steps' own values and effective values:

| Own values | Row shows |
|---|---|
| all unset | Default state, the effective value, "Default" |
| all set to the same value | Set state, the value, "Reset" |
| some set and some not, or different values | **Mixed**: NumberBox placeholder "Mixed", radios none chosen with Caption "Mixed", ComboBox "Mixed", text empty with placeholder "Mixed"; "Reset" shown when at least one step sets it |

- **Set:** typing/picking a value writes it as **Set on every selected step** (a value equal to the default still counts
  as set, as for one step). **Reset:** removes that field from every selected step ("Reset all"). Text fields apply on
  commit (focus lost or Enter), not per key, so one edit is one Undo.
- **Params:** an optional param can be cleared for all with its existing clear; a required one cannot be emptied.
- **Validation:** each step is validated after the change, as when edited alone (library refs, `wait` duration against
  the resolved timeout, …); problems appear in the Problems list per step. The change is never partly applied: it
  lands on all selected steps or none.
- **Undo:** the whole change is one history entry through the same `UpdateDraft` path as a single edit.

## D4. Keyboard and Narrator
Same controls as the single-step inspector, same tab order; accessible names carry the state: "Give up after, mixed",
"Give up after, 10 seconds, default". The header is a heading announcing the count and type. "Reset all" has the
tooltip and name "Use the default for all 3 steps".

## D5. Not done
Editing edges (Connections) or step names for several steps, bulk Test, bulk "Use these for all <type> steps" (it keeps
meaning the one selected step's type), copying settings from one step to others (paste-style).
