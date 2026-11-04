# Features

What Daze Control does, stack-independent. Source: the 2026-10-31 plan §2. Status per feature lives in [PLAN.md](PLAN.md).

**F1 Flows (DAG)**
- Nodes: *step* (one provider operation + params) and *group* (card: members start together; card finishes
  when all members finish). Edges connect cards/steps only (never into a member of a card).
- Edge `when`: `success` (default) · `failure` (fallback branch, e.g. buds if headphones fail) · `always`.
- A node runs when **all** incoming edges are satisfied; if one can never be satisfied it is **Skipped**,
  and skips propagate (only `always` edges fire from a skipped node).
- Node failure policy: `stop` (default: cancel the flow) · `skip` (skip what depends on it, other branches
  go on) · `ignore` ("may fail": counts as done, dependents run). Retries (count, delay), timeout
  (provider default, overridable). A card fails if a member fails with `stop`/`skip`; its own policy then
  applies to what follows it. No nested cards.
- A failed node with an outgoing `failure` edge has *handled* its failure: its `stop`/`skip` policy is not
  applied, the fallback runs and the flow goes on; its `success` dependents are still skipped (the fallback
  replaced them). A failure its fallback fixed ends the flow **Succeeded** ("Done", the pop-up says "Connect
  Technics didn't work, so Connect Sony ran instead.") (D-065).
- A **fallback step** (an incoming `failure` edge, or an incoming `success` edge from a fallback step) that wasn't
  needed ends **Not needed**: no warning, no bar on the Runs timeline, and the pop-up's "n of m" leaves it out until
  it starts (D-065).
- Outcomes: node Succeeded / Failed / FailedIgnored / Skipped / Cancelled / NotNeeded; flow Succeeded /
  PartiallySucceeded / Failed / Cancelled.
- Flow settings: name, glyph, tray (show, folder, separator), hotkeys, `whenRunning` (ignore / queue /
  restart), pop-up on/off, enabled.
- Validation: no cycles, refs exist, params match descriptors, cards hold ≥ 2 members, no edges into members.
  An invalid flow is disabled with its problems listed; every other flow keeps working.

**F2 Triggers** — v1: global hotkey, tray item, CLI (`dctl run`), editor Run/Test, desktop shortcut per flow
(`DazeControl.exe --run <flow>`, starts the engine when needed, D-049). M12 (designed): controller buttons
(`gamepad: [View+Menu]`, D-057) and a pick in a flow menu (D-054). Later: device events (BT
connected, display change), schedule, session unlock/resume, app start.

**F3 Providers (step types)** — each declares id, glyph, title, typed params, default timeout, resources it
locks, idempotency check ("already in state → no-op"), and an optional enumerator for editor pickers.
- `audio.setDefault` (output/input, roles), `audio.setVolume`/`mute` · `bluetooth.connect`/`disconnect`
  (audio devices, KS one-shot, no admin) · `display.topology` (Win+P presets), `display.outputs` (only these
  ports on), `display.layout` (captured CCD layout) · `process.run` (exe, args[], cwd, wait, exit codes,
  kill tree on timeout) · `script.powershell` · `http.request` (Sunshine API etc., credentials by name) ·
  `android.input`/`android.keys`/`android.sleep` (adb, connect retries, one chained shell call; any adb device, D-045) ·
  `service.start`/`stop` (fails clearly without rights) · `wait` · `notify` (pop-up text) ·
  `flag.set`/`flag.check` (check fails when unmet → combine with `when: failure`) · `flow.run` (Run a flow, D-061):
  waits for the called flow by default (its result is the step's, one pop-up and one history record, loops refused
  and stopped at 8 deep) or just starts it (`wait: false`); the editor picks the flow by name · M12 (designed): `menu.show` — a
  no-focus menu of flows over every window, controller/keyboard/mouse, picking launches the flow (D-054, D-055).

**F4 Library** — named, reusable resources referenced by steps: audio devices (match text + flow), Bluetooth
devices (address + name), display layouts/outputs, Android devices + inputs, programs, credentials (secret in
Windows Credential Manager, only the name in YAML). Each has a Test action.

**F5 Runtime** — DAG scheduler with parallel branches, engine-wide resource locks, timeouts + cancellation
on every step, retries, run events stream, run history (last 100, persisted), dry run.

**F6 Feedback** — run pop-up (no-activate, acrylic, flow name, progress, failure summary, position/duration
settings, honours "Animation effects"); tray tooltip/glyph shows the last run's failure.

**F7 Editor** — flows list (Run / Stop on each card, D-070; Delete with Undo, D-076; a renamed flow's id follows its name, D-075), DAG canvas, inspector, library, settings, about; Save/Cancel for flows, autosave elsewhere (D-047); live pickers and Test
via the engine; suggestions and examples for every free-text value (screens, services, Android keys and inputs,
flags, credentials, programs, script snippets; D-050, D-051); "Add to desktop" per flow (D-049); live run view and history timeline; undo/redo; long simple chains wrap onto rows and what follows starts a new line under them (D-060, D-066); right-drag pans the canvas (a still right click opens the menu); accessible outline view. Spec: §5.

**F8 CLI** — `dctl run <flow> [--wait] [--dry-run]`, `list`, `validate [file]`, `status`,
`devices audio|bluetooth|displays|adb|services|credentials`, `capture-display-layout <name>`, `history [flow]`,
`shortcut <flow> [--remove]`; M12: `devices gamepads`. `devices bluetooth` = the PC's paired devices, as Settings lists them (D-052).

**F9 Ops** — per-user install + in-app updates, start with Windows (HKCU Run), single instance, rolling logs
(7 days), crash capture, no admin, config backups (last 10).
