# Features

What Daze Control does, stack-independent. Source: the 2026-10-31 plan §2. Status per feature lives in [PLAN.md](PLAN.md).

**F1 Flows (DAG)**
- Nodes: *step* (one provider operation + params) and *group* (card: members start together; card finishes
  when all members finish). Edges connect cards/steps only (never into a member of a card).
- Edge `when`: `success` (default) · `failure` (fallback branch, e.g. buds if headphones fail) · `always`.
- A node runs when **all** incoming edges are satisfied (except edges from a branch that simply wasn't taken, a handled failure or a "Not needed" fallback: those are alternatives, so a step after a check and its fallback runs once if either fires, D-112); if one can never be satisfied it is **Skipped**,
  and skips propagate (only `always` edges fire from a skipped node).
- Node failure policy: `stop` (default: cancel the flow) · `skip` ("Skip what follows": skips the steps after it, other branches
  go on) · `ignore` ("Run the next steps anyway": counts as done, so its "when it works" follow-ups run too). Retries (count, delay), timeout
  (provider default, overridable). A card fails if a member fails with `stop`/`skip`; its own policy then
  applies to what follows it. No nested cards.
- A failed node with an outgoing `failure` edge has *handled* its failure: its `stop`/`skip` policy is not
  applied, the fallback runs and the flow goes on; its `success` dependents are still skipped (the fallback
  replaced them). A failure its fallback fixed ends the flow **Succeeded** ("Done", the pop-up says "Connect
  Technics didn't work, so Connect Sony ran instead.") (D-065).
- A **fallback step** (an incoming `failure` edge, or an incoming `success` edge from a fallback step) that wasn't
  needed ends **Not needed**: no warning, no bar on the Runs timeline, and in the pop-up's "n of m" it simply
  finishes (D-065; the total is every step and never moves, D-127).
- Outcomes: node Succeeded / Failed / FailedIgnored / Skipped / Cancelled / NotNeeded; flow Succeeded /
  PartiallySucceeded / Failed / Cancelled.
- Flow settings: name, glyph, tray (show, folder, separator), hotkeys, `whenRunning` (ignore / queue /
  restart), pop-up on/off, enabled.
- Validation: no cycles, refs exist, params match descriptors, cards hold ≥ 2 members, no edges into members.
  An invalid flow is disabled with its problems listed; every other flow keeps working.

**F2 Triggers** — v1: global hotkey (Win/Ctrl/Alt plus a letter, number, F1–F24 or a key like `[` `]` `\`;
number-pad Num0–Num9 and Num* Num+ Num- Num. Num/ with or without them, D-071; the recorder says why a key is refused, D-073), tray item, CLI (`dctl run`), editor Run/Test, desktop shortcut per flow
(`DazeControl.exe --run <flow>`, starts the engine when needed, D-049). M12: a pick in a flow menu (D-054; a controller drives the open menu, no controller trigger, D-086). M20 (D-107): a flow can start on a **schedule** (`triggers:` `at` + `days`, `every` + window, `once`, `on: engine.start`; local time; per schedule `ifMissed: run|skip`, a late run inside 10 min, else "Skipped: the PC was asleep"; Pause silences it) or on a **system event** (session lock/unlock/logon/logoff, power resume and AC/battery, device added/removed (`device: <part of its name>`, the name read when it comes or goes), default sound device (`device: <Library audio item>`), display changes, screens turned off/on (`display.off/on`) and you stepping away or coming back (`session.away/back`, Windows' own idea of away: only a change raises; our own Turn screens off raises `display.off` too), and each network connection coming or going (Wi-Fi or cable, also while another stays up; `via: wifi|cable`, `ssid: <Wi-Fi name>`); `only: onBattery|onAC`, debounce, cooldown, no loops). A flow can **ask** the person: an input with `ask: true` offers the Library items before the run, and the `prompt.ask` step asks Yes/No or Choose, always with a timeout and default so unattended runs never hang. The **Pick device** step (`prompt.pick`, D-117) offers the Library's audio devices, Bluetooth devices, screen layouts or Android devices mid-run; any later step's device setting (or a value a `flow.run` passes) can be "Result of: <that Pick step>" (`$result:<id>`) or another earlier step's device, e.g. Set default device's "Previous default" to switch back later (`$result:<id>.<value>`, D-130), instead of a fixed item; its Default (used when nobody answers) is picked from the Library, D-128. Later: window, file and flow events, text/number prompts. Program start/stop is not planned (needs WMI or admin).

**F3 Providers (step types)** — each declares id, glyph, title, typed params, default timeout, resources it
locks, idempotency check ("already in state → no-op"), and an optional enumerator for editor pickers.
- `audio.setDefault` (output/input, roles), `audio.setVolume`/`mute` · `bluetooth.connect`/`disconnect`
  (audio devices, KS one-shot, no admin) · `display.topology` (Win+P presets), `display.outputs` (only these
  ports on, in the arrangement Windows saved for them), `display.layout` (captured CCD layout), `display.screensOff` (Turn screens off: SC_MONITORPOWER, PC keeps running, 1 s guard so the triggering key release does not wake them, any input does); the first three fail when
  Windows sets the screens up differently · `process.run` (exe, args[], cwd, wait, exit codes,
  kill tree on timeout), `process.open` (Open file or address: a file, folder, web page, `steam://` or `ms-settings:` link in the app that handles it, no wait; the target takes values, kept part of the name or address, D-134) · `process.close` (Close program: asks the main window to close, ends it and its children after "End it after", default 10 s; not running = done; an elevated program fails with a sentence, D-102) · `script.powershell` · `http.request` (Sunshine API etc., credentials by name) ·
  `android.connect` (just `adb connect`, D-096)/`android.input`/`android.keys` (home/back/d-pad/volume + media transport: play, pause, stop, next/previous, fast forward, rewind, skip, step, record, eject…; any other `KEYCODE_*` name also works)/`android.media` (Control media)/`android.setVolume` (a stream to 0–100 % of the device's range)/`android.changeVolume`/`android.mute` (Mute or unmute)/`android.setBrightness`/`android.open` (Open address)/`android.wake`/`android.sleep` (D-105)/`android.stop` (Stop app: `am force-stop`, same picker as Start app, not running = done)/`android.start` (open an app: pick it from a searchable, name-sorted list of the device's apps or type the package, D-092..D-094; adb, connect retries, one chained shell call; any adb device, D-045) ·
  `service.start`/`stop` (fails clearly without rights) · `wait` · `notify` (pop-up text; "Play the notification sound" off = a silent balloon, F-037) ·
  `flag.set`/`flag.check` (check fails when unmet → combine with `when: failure`) · `flow.stop` Stop flow, quietly (nothing shown, history "Stopped here", not a failure; in a called flow only that flow ends and the caller's step is done) or as failed (with a message that takes values; fails the caller's step; D-133) · read-only checks `audio.check` (default device), `process.check`, `service.check`, `file.check` (exists/missing) fail with a sentence like "Spotify isn't running" (D-081); PC-state checks (D-118):
  Check Wi-Fi (`network.wifi`: any or a named network), network cable, internet (Windows' own check), metered connection,
  VPN, power (plugged in / on battery), battery (%), idle time, locked, screen count (`display.count`), free space
  (`file.space`), time (days + From/To window, past midnight ok); "Only if" on `audio.setDefault` (`notDefault`), `process.run` (`notRunning`), `service.start` (`stopped`), `service.stop` (`running`) · `flow.run` (Run flow, D-061):
  waits for the called flow by default (its result is the step's, one pop-up and one history record, loops refused
  and stopped at 8 deep) or just starts it (`wait: false`); the editor picks the flow by name; a flow can declare
  device **inputs** with defaults (`inputs:`, steps use `$name`) and a call passes devices to them, so one chain serves
  Technics and Sony (D-064); an input can be a **bound device** (`kind: bound`: Bluetooth steps use its Bluetooth side,
  audio steps its audio side, D-125); the editor adds inputs in flow settings, offers them in device pickers, and gives a call
  one device picker per input; the Runs page expands a call into the called flow's steps · M12: `menu.show` — a
  no-focus menu of flows over every window (list or grid, normal or large), keyboard/mouse, and an Xbox pad while it is open; picking launches the flow, or with "wait" runs it nested (D-054, D-055, D-061, D-086). Not over exclusive full-screen games (#32).

**Wait until:** a check step with "Try again" and "Wait between tries" waits for a fact (a program running, a file, a connection) before the next step; no separate step (D-081). The PowerShell step's examples also cover sleep, power plan and dark mode.

**M14 (designed, D-080..D-082):** per-step-type defaults for Try again / Wait between tries / Give up after / If it fails
(`settings.stepDefaults`, set in Settings or "Use these for all <type> steps"); per-step **Only if…** (skips a step that has
nothing to do, e.g. Connect when already connected; the flow carries on) and **Check** steps (Bluetooth, audio, screen layout, Android connection, program,
service, file; the screen and Android checks only read, the Android one asks an adb that is already running and never starts one; they fail when unmet, so an "if it isn't" branch works). Spec: docs/step-settings/DESIGN.md.

**F4 Library** — named, reusable resources referenced by steps: audio devices (match text + flow), Bluetooth
devices (address + name), **bound devices** (a Bluetooth device + its audio device used as one: Bluetooth steps act on
the Bluetooth side, audio steps on the audio side; listed in both pickers, D-116), display layouts/outputs, Android devices + inputs (edited on the card: moves as an Up/Down list, D-078), programs, credentials (secret in
Windows Credential Manager, only the name in YAML). Each has a Test action. Audio, Bluetooth and Android devices can also be **added by hand** (name text / address, D-123), for a device that is off, away or not paired yet; it matches the real one when it shows up.

**F5 Runtime** — DAG scheduler with parallel branches, engine-wide resource locks, timeouts + cancellation
on every step, retries, run events stream, run history (last 100, persisted), dry run.

**F6 Feedback** — run pop-up (no-activate, acrylic, flow name, progress, failure summary, position/duration
settings, honours "Animation effects"); tray tooltip/glyph shows the last run's failure.

**F7 Editor** — flows list (grouped by tray folder with collapse, rename, ungroup, turn all on/off, move, "Move to ›", drag to reorder or into/out of a group, Alt+↑/↓/← and Move up/down, Undo / Ctrl+Z (20 steps), D-087/D-088; Run / Stop on each card, D-070; rows with fixed columns, a status dot for the last run and a hover drag grip; Delete with Undo, D-076; sort / search / filter, multi-select with bulk actions, Duplicate and tray toggles in the card menu, D-090; a renamed flow's id follows its name, D-075), DAG canvas, inspector, library, settings, about; Save/Cancel for flows, autosave elsewhere (D-047); live pickers and Test
via the engine; suggestions and examples for every free-text value (screens, services, Android keys and inputs,
flags, credentials, programs, script snippets; D-050, D-051); "Add to desktop" per flow (D-049); live run view and history timeline; a nav pane that collapses to icons and stays that way on this PC (D-068); the window reopens where and how big you left it, maximized or not, and falls back to a centred default if that place is gone (D-098); undo/redo; long simple chains wrap onto rows and what follows starts a new line under them (D-060, D-066); the mouse wheel zooms at the pointer and any flow can be moved (D-074); right-drag pans the canvas (a still right click opens the menu); accessible outline view. Spec: §5.

**F8 CLI** — `dctl run <flow> [name=value…] [--wait] [--dry-run]` (T24.9: gives the flow's inputs, so one flow serves several shortcuts or deck keys: a Library key for a device input, any text for a text input; an unknown input, a key not in the Library or a text over 1,000 characters stops before the run with exit 2 and the flow's input names; values are data, never run or read as references, and never logged) and the same after `DazeControl.exe --run <flow id> name=value…` (a bad one is the pop-up), `list`, `validate [file]`, `status`,
`devices audio|bluetooth|displays|adb|services|credentials|wifi`, `check <check> [name=value…]` (one check now: exit 0 yes / 1 no, D-118), `capture-display-layout <name>`, `history [flow]`,
`shortcut <flow> [--remove]`, `backup [file]`, `restore <file> [--merge|--replace] [--yes] [--dry-run]`; M12: `devices gamepads`. `devices bluetooth` = the PC's paired devices, as Settings lists them (D-052).

**F9 Ops** — per-user install + in-app updates (Stable by default; About › Updates › "Updates to follow" or `dctl update --channel rc` opts in to release candidates, D-108), start with Windows (HKCU Run), single instance, rolling logs
(7 days), crash capture, no admin, config backups (last 10); **backup and restore** (M19, D-103): one `.dcbackup` file with flows, library, settings and tray order, from Settings › Backup and restore or `dctl`; a preview before restoring (Replace everything or Add what's missing), a safety copy first, Undo, and an automatic copy before updates and after saves (at most one a day, newest 10 kept, D-104). Secrets, shortcuts and autostart are not in it.

**F10 Step values (M23, D-129..D-132)** — a step gives values that later steps use inside their text: a Show message can say "Connected to {bt.device} at {@time}". Connect/disconnect, Pick device, Set default device (also the one it replaced), the checks (battery %, Wi-Fi network, idle time, free space, screens on, each check's result), Ask (the answer), Run program / PowerShell (exit code, output), Web request (status, response), Show flow menu (picked flow) and Make text each give named values; every step also gives "why it failed" for a failure branch. A text can also use this flow's inputs, the flow name, what started it, the device, Wi-Fi or cable name an event brought (`{@event}`, empty when something else started the run), the time and the date. A value missing at run time shows the words written after `|` (`{batt.percent|unknown}`), else nothing; the editor warns ahead when that can happen. Settings that take values: messages, question texts, menu title, Make text, web address, Android address, program arguments. **Pills**: "Insert value" in those settings, a "Shows:" row of pills under the box, "Values it gives" on the step. A later step can use a device an earlier one gave (`$result:<id>.<value>`), e.g. put back the previous default sound device. Flows can take **text inputs** (`kind: text`, with a default; a `Run a flow` step passes text), give **values back** (flow outputs) to a waiting `Run a flow`, and **Make text** once for several steps. PowerShell reads values as `$env:DAZE_<STEP>_<VALUE>`; values are never pasted into scripts, so a device named like code stays text. Messages shown in a row appear one at a time, 5 s each (D-132). Not built: asking for text when a flow starts, values that survive a restart (flags do, D-029), format specifiers.
