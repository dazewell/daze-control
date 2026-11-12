# Step values: what a step gives, used by later steps

Status: **accepted** (D-129; user, 2026-11-12: syntax, missing = fallback else empty, the word "value", scripts via
environment variables, single-assignment values only, all as recommended). Nothing here is built yet. Tasks: PLAN → M23.
Each M23 task moves what it built into ARCHITECTURE / UX / FEATURES and cuts it here (D-053).

## 1. What the user asked

> "I want the actions to produce some information that can then be used as a variable placeholder in some format, for
> example for a show message placeholder like {device} with reference to a particular step. We could even add clickable
> pills with those values for easier templating and composing... maybe even add a new class of working with variables etc."
> (user, 2026-11-12)

So: (a) steps give values, (b) a later step's text names a value of a particular step, (c) the editor composes that
text with clickable pills, (d) later, a fuller way of working with values.

## 2. What exists today (and what it already decided)

| Piece | What it does | Constraint it sets |
|---|---|---|
| Flow inputs `$name` (D-064) | A device setting holds `$name`; `FlowInputs.Apply` swaps in the Library key before the run | Rejected free-text `{x}` "because every ParamKind's validation and pickers would have to accept placeholders, and a typo becomes a run-time failure". Revisit "if users need text inputs or values passed back". |
| Pick result `$result:<id>` (D-117) | `prompt.pick` → `StepContext.SetResult(key)`; `PickResults.Resolve` swaps it into device settings just before the step (before its lock) | Must-wait-for rule (`PickResults.Before`, any edge, through cards; own card excluded). Rejected a generic variable system: "nobody asked … revisit with text prompts". **The user now asks.** |
| Flags (D-029) | In-memory, case-insensitive booleans shared by all flows; `prompt.ask` choose writes `name:answer` | The only cross-run memory. Reset on engine restart, on purpose. |
| `StepContext.SetSummary`, `FactReading.Text` | A sentence per step ("The battery is at 35%") into `NodeResult.Summary` and history | Sentences, not values; wording changes freely. |
| `ProcessRunner.RunAsync(keepOutput)` | Keeps the last N chars of stdout (adb uses it) | Capture costs only when asked for. |
| Text settings that hold braces today | `script.powershell` (`{ exit 1 }`, `@{…}`), `http.request` body (`{ "key": "value" }`), `%VARIABLES%` in `file.check`/`file.space` paths | Any placeholder syntax must leave these untouched. |

## 3. Alternatives

**A. Typed, named values declared per step type in the catalog; `{step.value}` in text settings that take values (recommended).**
Each `ProviderDescriptor` lists the values it gives (`ValueSpec`: name, kind Text / Number / Device(LibraryKind), title,
flags). A text setting opts in (`ParamSpec.TakesValues`). The validator knows every reference before a run: the step
exists, gives that value, always finishes first. The editor lists exactly what can be inserted. Device-kind values keep
D-117's `$result:` form in device settings, so pickers stay pickers.

**B. One free-text result per step (its summary sentence), `{step}`.** Zero catalog work, but the value is a sentence
("Technics is connected", not "Technics"), changes wording at will, can't fill a device or number setting, and can't be
checked beyond "the step exists". It also leaks sentences into history-shaped strings. Lost on checkability (D-064's
reason) and usefulness.

**C. Flow-level mutable variables** (a "Set variable" step, steps "save into" a named variable, `{name}` anywhere).
Most familiar to programmers, but: parallel branches writing one name race (the editor can't say which wins), the
validator can't tell whether a name is set when a step runs (any branch may set it), renaming a variable means a
second rename system next to Library keys, and "variable" is jargon in this product's voice. Its useful half (a named
value you compose once and use many times) is a **single-assignment** step under A (§9, "Make text"), which keeps every
reference checkable and race-free.

Third-party template engines (Scriban, Fluid, Handlebars.Net) are out: reflection over models, AOT/trim warnings (hard
rule), and far more language than a message needs.

## 4. Recommended model (A)

### 4.1 Catalog

```csharp
public enum ValueKind { Text, Number, Device }          // Duration later if a step needs it
public sealed record ValueSpec(string Name, ValueKind Kind, string Title)
{
    public LibraryKind? Library { get; init; }          // Device values
    public string? Help { get; init; }                  // "The Library name of the device it connected"
    public bool Sensitive { get; init; }                // never in logs, history, summaries (§4.8)
    public bool OnFailure { get; init; }                // still given when the step fails (exit code)
}
// ProviderDescriptor.Values : ValueList<ValueSpec>;  ParamSpec.TakesValues : bool;  ParamSpec.Fill : FillRule (None | Url | Argument), stage 3
```

Every step also gives `error` ("Why it failed", Text, OnFailure only), added by `StepValues.Of(descriptor)`, so a
failure edge can say "Couldn't connect: {bt.error}". Run values (no step): `@flow` (flow name), `@trigger` ("Alt+Z",
"Schedule · 22:30"), `@time` (HH:mm, local), `@date` (yyyy-MM-dd). A flow input in text: `{$output}` = its device's name.

### 4.2 What steps give (stage 1 = T23.2, stage 3 = T23.5/T23.6)

| Step | Value (name · kind · title) | Stage |
|---|---|---|
| `prompt.pick` Pick device | `device` · Device(kind) · Device | 1 (its `$result:` already exists) |
| `bluetooth.connect` / `disconnect` | `device` · Device(Bluetooth) · Device | 1 |
| `audio.setDefault` | `device` · Device(Audio) · Device; `previous` · Device(Audio) · Previous default | 1 / 3 (`previous` fills a device setting: "put back what was default") |
| `power.battery` Check battery | `percent` · Number · Battery (%) (OnFailure: a failed check still read it) | 1 |
| `network.wifi` Check Wi-Fi | `network` · Text · Wi-Fi network (OnFailure, as battery) | 1 |
| `prompt.ask` Ask | `answer` · Text · Answer ("Yes"/"No" or the choice) | 1 |
| `process.run`, `script.powershell` | `exitCode` · Number · Exit code (OnFailure) | 1 |
| `process.run`, `script.powershell` | `output` · Text · Output (last 1,000 chars of stdout, trimmed; Sensitive; captured only when referenced) | 3 |
| `http.request` | `status` · Number · Status code (OnFailure); `body` · Text · Response (Sensitive, 1,000 chars) | 3 |
| `menu.show` | `picked` · Text · Picked flow | 3 |
| every check step (`*.check`, `network.*`, `power.*`, `pc.*`, `file.*`, `display.count`, `time.check`) | `result` · Text · Result (its `FactReading` sentence) | 3 |
| `pc.idle`, `file.space`, `display.count` | `idle` · Text, `free` · Number (GB), `count` · Number | 3 |
| `flow.run` (wait) | the called flow's declared outputs | 3 (T23.7) |

### 4.3 Reference syntax

In a text setting that takes values:

```yaml
- { id: bt,   step: bluetooth.connect, with: { device: $output } }
- { id: note, step: notify, after: [bt],
    with: { text: "Connected to {bt.device} at {@time}. Battery {batt.percent|unknown}%" } }
```

Grammar (hand-written scanner, `StepValues.Parse`):
- `{` *step* `.` *value* [`|` *fallback*] `}` — step = `[A-Za-z0-9][A-Za-z0-9_-]*`, value = `[a-z][A-Za-z0-9]*`, fallback = any
  text without `{`, `}` or a line break.
- `{$` *input* [`|` fallback] `}` — input = `[a-z][a-z0-9-]*` (D-064's name rule; so PowerShell's `{$_}` never matches).
- `{@` *run value* [`|` fallback] `}`.
- `{{` before a reference shape writes it literally: `{{bt.device}` shows `{bt.device}`. Anything that isn't a reference
  shape is plain text, so `{ exit 1 }`, `{"a":1}`, `@{x=1}` and `%TEMP%` mean what they meant.

Coexistence: `$name` and `$result:<id>` stay **whole-value** forms for device settings (unchanged YAML, unchanged
validation). Braces are **inside-text** forms for text settings. Stage 3 widens `$result:<id>` to
`$result:<id>.<value>` for any Device value (`$result:<id>` alone keeps meaning the Pick's `device`). A `{…}` in a
device, number, choice or duration setting is an error (stage 1); number settings may take a whole `{x.percent}` in
stage 3 (range checked at run time).

### 4.4 Types and formatting

- Text: as given, trimmed; in a single-line setting line breaks become spaces.
- Number: invariant digits (`35`); units are the person's text: `{batt.percent}%`.
- Device: the Library item's display name (its `name`, else the key); in a device setting, the key.
- No format specifiers in v1 (`{x:0.0}`, upper/lower): nobody needs them yet; the grammar leaves room after `|`.
- Fallback `|text` is used when the value is missing or empty.
- Size: a value is cut at 1,000 chars (with "…"); a filled setting at 4,000; stdout is read only for an `output` that a
  setting references (`StepContext.Wants`).

### 4.5 Scope and run time: what a reference yields

| Situation | Yields |
|---|---|
| Producer always finishes before the consumer (any edge, through cards; `StepValues.Before`, the generalised `PickResults.Before`) | Its value. Anything else is a validation error (§4.6). |
| Producer in the consumer's own card | Not allowed: card members start together. |
| Retries | Values are cleared when an attempt starts; the last attempt's stand. |
| Producer failed, consumer runs anyway ("Run the next steps anyway" or a failure edge) | `error`, plus OnFailure values it set (exit code, status). Others missing. |
| Producer skipped, not needed (fallback), "Only if" not met, cancelled | Missing. |
| Missing, in text | The fallback if written, else empty text. The validator warns ahead when this can happen (§4.6). |
| Missing, in a device setting | The step fails, as today ("Nothing was picked in …", D-117). |
| Parallel branches | No race: values are per run, keyed by step id, written once per attempt, read only from finished steps; the runner's completion path is already serial. |
| Nested `flow.run` | The called flow sees only its inputs; the caller doesn't see its steps. Values cross a call only through inputs (text inputs, T23.7) and declared flow outputs (T23.7). Keeps flows reusable. |
| `flow.run` / `menu.show` that don't wait | No values except `picked`. |
| Loops | None exist (cycles are refused). |
| Dry run (`dctl run --dry-run`), editor Test of one step | Each value shows as its title: "Connected to ‹Device› at ‹Time›". Never a real or sample device. |

### 4.6 Validation before a run (one resolver: `Core/Model/StepValues`, used by validator, runner, dry run, editor)

- Reference-shaped text naming a step id that isn't in the flow: **warning**, shown as written ("There's no step called
  "bt2" in this flow, so {bt2.device} shows as written."). Fail-soft for old text that happened to look like a reference.
- Known step, unknown value: **error** ("Connect gives no value called "nmae" (it gives Device, Why it failed).").
- Not always before: **error**, D-117's sentence ("Connect must run before this step: connect it so this step comes after.").
- Kind: a Device value in a device setting must `Fits` (D-116 bound rules); a Text value can't fill a device setting.
- May be missing and no fallback: **warning** ("Connect may not run (Run the next steps anyway), so Device can be empty.
  Add what to show when it has no value."). Statically known: the producer has `onFailure: ignore`, an `only`, is a
  fallback unit, or the path reaches the consumer through a `when: failure|always` edge. T23.1 made it exact: the value is sure when a chain of success/failure edges from the producer (its first edge
  a success edge, and no Only if / Run the next steps anyway on it or its card) reaches the consumer, so a fallback producer
  followed by success edges is fine; `error` is sure only after a failure edge; OnFailure values whenever the producer surely ran. In a join (D-112) an edge from a branch that may not be taken
  (fallback, a unit with a failure edge, or downstream of one) proves nothing unless it is the unit's only edge.
- Reference in a text setting that doesn't take values: **warning** ("This setting doesn't take values; {bt.device} is
  used as written."). In a script: the same warning naming the environment variable (§4.8).
- Unknown input / run value: error, as for `$name` today.

### 4.7 Run time (runner)

`FlowRunner.RunStepAsync`, at the point where `PickResults.Resolve` runs today (just before the lock): `StepValues.Fill`
fills text settings that take values and `$result:` device settings from the run's `RunValues`; the provider only ever
sees plain text and Library keys (as with inputs). After each attempt the runner copies `StepContext` values
(`SetValue(name, text)`, `SetDevice(name, key)`; `SetResult(key)` stays as `SetDevice("device", key)`) and the attempt's
error into `RunValues`. Unreferenced values are not kept (`StepValues.Wanted(flow)` once per run).

### 4.8 Secrets, logs, injection (D-014)

- No step gives a credential; Credential Manager content never becomes a value.
- YAML only ever holds references, never filled text. History keeps settings as written; summaries and log lines never
  include Sensitive values (a filled text containing one is summarised with titles: "Showed a message with ‹Output›").
- **Scripts never get values spliced into code**: `script.powershell` gets every value of the steps it waits for as
  environment variables (`DAZE_BT_DEVICE`, `DAZE_BATT_PERCENT`; id `-` → `_`, upper case), so a device named
  `'; Remove-Item …` stays data. Stage 3 fill rules for other fields: URL parts percent-encoded (`http.request` url,
  `android.open` address), a `process.run` argument filled as one argv element, headers/body as written (warned in Help).
- Built in T23.5: `ParamSpec.Fill` (`Url` encodes the value only, a `|fallback` stays as written; `Argument` fills list items one by
  one). `process.run` expands `%VAR%` in the written argument, so a value's `%` is swapped for `StepValues.PercentGuard` (U+E000)
  while filling and back after the expansion: a device named `%TEMP%` stays text. Scripts get the values their text *names*
  (`$env:DAZE_BT_DEVICE`, any case: `Wanted` scans the script) as `StepContext.Variables`; PowerShell's stdout is UTF-8 so `output`
  keeps non-ASCII. The runner replaces any Sensitive value of an earlier step found in a step's error or summary with its title
  (`StepValues.Secrets/Mask`). `output` of a failed program is not given (only OnFailure values are). Inspector Insert value
  doesn't list the argument items yet (a list setting; the editor part is T23.4's area): write `{p.output}` by hand.

## 5. Editor UX

Word: **value** ("Insert value", "Values it gives"), never "variable", "node", "DAG", "OSD" (UX.md). Same voice as
"Result of Pick speakers" (D-117) and "This flow's inputs" (D-064).

1. **Insert value.** A text setting that takes values gets a trailing icon button (standard button size, D-121; glyph
   `E943`-style "insert"; tooltip "Insert a value from an earlier step"; name "Insert a value into Message"). It opens a
   Flyout anchored to the button: a grouped list — **From earlier steps** (one header per step the step waits for, its
   name; rows = its values: title + help caption), **This flow's inputs**, **About this run** (Flow name, What started
   it, Time, Date). Steps it doesn't wait for aren't listed; a footer says "Only steps this one waits for are listed."
   Enter/click inserts at the caret (replacing a selection) and gives focus back to the text box. The editor is the
   active window, so this is ordinary focus; nothing else activates.
2. **Display form.** The text box shows `{Connect Technics: Device}` (step **name**: value **title**); YAML stores
   `{bt.device}`. A pure codec (`Editor.ViewModels/Inspector/ValueText.ToDisplay/FromDisplay(flow, text)`) maps both ways on
   load/commit; two steps with one name read "Connect Technics (2)" by flow order. Renaming a step never breaks a reference
   (ids don't change). A hand-typed display token that maps to nothing is saved as plain text; the pills row shows it
   as text with "Not a value from this flow" under the box until it's fixed or removed.
3. **Pills.** Under the box, a one-line row that is always reserved (D-104: no jumps). With no values it holds the
   setting's help (or stays empty); with values it reads **Shows:** "Connected to [Device · Connect Technics] at [Time]"
   where each bracket is a pill (small Button, badge-chip style UX §5.3, 20 DIP). A pill is a tab stop after the box;
   click/Enter opens: "Change value…" (the Insert list), "If it has no value, show" (a text box → `|fallback`), "Remove".
   Delete on a focused pill removes it. Name: "Device from Connect Technics, in Message. Press to change or remove." A
   may-be-missing pill carries the warning glyph and its sentence as tooltip.
4. **Values it gives.** A producing step's inspector lists its values as pills under a "Values it gives" heading (title,
   help caption, "Used by 2 steps"). Click → "Copy" puts the display token on the clipboard ("Copied" tip), so it can be
   pasted into any text box that takes values.
5. **Canvas.** A step's pill summary uses the display form with titles ("Connected to Device at Time"). No extra edges:
   the must-wait rule already makes the edge exist.
6. **Delete, duplicate, paste.** Deleting a step whose values are used: the existing confirmation adds "Show message
   uses its Device"; on confirm the references become their fallback text (or go), one Undo. Duplicate/paste remaps
   references to steps inside the pasted set to their new ids; others stay and are re-checked.
7. **Several steps selected (M15).** Insert value is off (values depend on each step's upstream); the text still edits.
8. **Scripts.** The PowerShell box has no Insert button; its help line says "Values from earlier steps are in
   $env:DAZE_… variables" and the Insert list (from a small "Show values" link) copies `$env:DAZE_BT_DEVICE`.

## 6. AOT and runtime cost

- No reflection, no new serializer: catalog records, a hand-written scanner (no Regex needed), a per-run
  `Dictionary<string, StepValue>`; history JSON unchanged in stages 1-2.
- Idle unchanged: nothing resident, no timer, no thread; the dictionary lives only during a run. Stdout is read only when
  an `output` is referenced. Engine image grows by a few KB (measure in T23.2's PR).
- One resolver in Core (`StepValues`: Parse, Before, Problems, MayBeMissing, Fill, Wanted) used by the validator, the
  runner, the dry run and the editor; `PickResults.Before/Resolve` delegate to it, so there is one reachability rule.

## 7. Relation to D-064 and D-117; existing flows

- Existing YAML is unchanged and means the same: `$name` and `$result:<id>` work as today, and no setting takes values
  until the catalog marks it. Stage 1 marks only `notify.text`, `prompt.ask.question`, `prompt.pick.question`,
  `menu.show.title`; a reference-shaped string already in one of those that names no step only warns (§4.6).
  `samples/*.yaml` and the test fixtures must round-trip byte-identical (T23.1 test).
- D-064's "Why not free-text substitution" is answered by opt-in fields + catalog-declared values (the validator still
  sees every reference). D-117's "Why not a generic result system" is revisited because the user asked; its Pick rules
  stay and become the Device case of §4.5. D-129 amends both (their status lines say so).

## 8. Stages (tasks in PLAN → M23)

1. **Values in messages** (T23.1 Core, T23.2 runtime + 8 producers): YAML-written `{bt.device}` works, checked before
   the run, shown in dry runs. Ships alone.
2. **Pills** (T23.3 Insert value + display form, T23.4 pills row, "Values it gives", delete/paste): composing without YAML.
3. **More values and fields** (T23.5), **device values beyond Pick** (T23.6: "put back the previous default"),
   **text inputs, flow outputs, Make text** (T23.7), gate (T23.8).

## 9. Further: working with values (stage 3, T23.7)

Not mutable variables (§3 C). Instead, three single-assignment pieces that stay checkable:
- **Text inputs**: `inputs: { greeting: { kind: text, default: "Hi" } }`; `{$greeting}` in text; a `flow.run` passes text
  that may itself hold values (`{bt.device}`), filled in the caller. Asking for text at start needs a typing prompt,
  which needs focus: out until a no-focus way exists (D-107 "text later").
- **Flow outputs**: `outputs: { device: "{bt.device}" }` on a flow; a waiting `flow.run` gives them as its values.
- **Make text** step (`text.make`, Values group): one `text` setting that takes values; gives `text`. A named, composed
  value used by several later steps, set once per run.
- Memory across runs stays flags (D-029). A "remember this text" store is not proposed.
