# Steps review (approved 2026-11-06, D-102)

Requested 2026-11-06: are the steps consistent inside and between palette groups, and what is missing? Facts below come from the code at
`origin/main` 88c2bc0 (v0.45.0): `Core/Catalog/ProviderCatalog.cs` (the single list of step types, D-006), `Providers/*`,
`Editor.ViewModels/Canvas/{StepPaletteViewModel,CanvasNodeViewModel}.cs`, `Runtime/Control/EngineController.cs` (dry run), the inspector XAML.
Nothing here is built. No id is renamed. Strings never say node, DAG or OSD; no Narrator work (D-091); T14.9 stays parked (D-096).
Decision proposed: D-102. Follow-up work: PLAN → M18, only after the user answers the open choices (§6).

## 0. How the palette is built (what a fix can touch)

- A step type = one `ProviderDescriptor` (id, title, glyph, default timeout, params, lock names, "Only if" conditions). Palette order =
  catalog order; palette group = the id's first segment (`Area`), shown through `CanvasNodeViewModel.Area()`, which also feeds the card
  pill fallback, the inspector breadcrumb ("Android › Android keys"), accessibility names and the Step defaults list.
- Palette titles, group labels, group order, choice captions and error text are **not saved**: changing them needs no migration. Only
  step ids (`step:`) and param names (`with:`) are saved. A card with no `name:` shows the catalog title.
- Every step has Name, Try again, Wait between tries, Give up after, If it fails (Stop / Skip what depends on it / Keep going) and Connections.
  "Only if" exists on 6 types (§1). Dry run (`DryProvider`): every step does nothing; a missing or unset Library device fails; `menu.show`
  says "Would show n choices"; **checks always pass** and "Only if" counts as met (D-081). Test this step runs for real.
- Choice params show their YAML word in the dropdown and in the card pill (`Spec.Choices` used as is): users see `notDefault`,
  `notRunning`, `communications`, `extend`.

## Adding a step: checklist (standing rule, D-102/D-105)

`StepConventionTests` enforces 1 (verb list, case, articles), 2 (id shape), 3, 4 (checks last) and Android captions; the rest is on you. Keep §1 below in step only when a group changes shape.
1. **Title**: imperative verb + bare object, sentence case, no article, group name not repeated ("Set volume", "Open address"). Checks are
   "Check <noun>" and the only ones. Verb not in the test's list = add it there on purpose. Same title in two groups is fine, twice in one is not.
2. **Id**: `area.verb` in camelCase (`android.setVolume`). Never rename an id; add new ones to `RetitledStepsKeepTheirSavedIds`.
3. **Group**: one of the nine themed groups (`CanvasNodeViewModel.Groups`); the id's area maps to it. A step targeting the PC goes in Audio/Displays/…;
   one targeting a device goes with that device's group. A tenth group needs a decision.
4. **Order** inside the group (catalog order): connect/start, then act, then undo/stop, **Check last**.
5. **Params**: device is `device` ("Android device"); a Choice has a `Captions` entry per choice (users never see camelCase); numbers carry `Min`/`Max`;
   help text is one short sentence; a percent is "(%)" and 0 to 100.
6. **Behaviour**: default timeout (Short, 20 s for adb, 30 s services/programs), a `Resources` lock, idempotent where it can be (set, not toggle),
   fail with a plain sentence, no polling, honours cancellation.
7. **Tests**: provider test with a fake (no device), validation range test, catalog id list; docs: FEATURES line, UX list if the inspector shows it.

## 1. Inventory (snapshot at v0.45.0: 29 step types, 14 palette groups; `ProviderCatalog.cs` is the live list, the Android rows predate D-105)

Timeout = default "Give up after". Lock = resource name held while the step runs. "Only if" ids in brackets. All durations are text like
`20s`; percent is a whole number.

| Group (label) | Title | Id | Params in order: name "label" (type, default) | Timeout | Lock | Only if |
|---|---|---|---|---|---|---|
| Audio | Set default device | `audio.setDefault` | `device` "Device" (Audio ref, req) · `roles` "Use for" (all/console/communications, all) | 10 s | audio | [notDefault] |
| Audio | Check default device | `audio.check` | `device` "Device" (Audio ref, req) · `is` "Is" (default/notDefault, default) | 10 s | – | – |
| Audio | Set volume | `audio.setVolume` | `device` "Device" (Audio ref, empty = default output) · `percent` "Volume (%)" (0–100, req) | 10 s | audio | – |
| Audio | Mute | `audio.mute` | `device` "Device" (as above) · `muted` "Muted" (bool, true) | 10 s | audio | – |
| Bluetooth | Connect | `bluetooth.connect` | `device` "Device" (Bluetooth ref, req) | 20 s | bluetooth:{device} | [disconnected] |
| Bluetooth | Disconnect | `bluetooth.disconnect` | `device` "Device" | 20 s | bluetooth:{device} | [connected] |
| Bluetooth | Check connection | `bluetooth.check` | `device` "Device" · `is` "Is" (connected/disconnected, connected) | 10 s | bluetooth:{device} | – |
| Displays | Project | `display.topology` | `mode` "Mode" (internal/clone/extend/external, req) | 10 s | display | – |
| Displays | Use only these screens | `display.outputs` | `outputs` "Screens" (list, req, live suggestions) | 10 s | display | – |
| Displays | Restore screen layout | `display.layout` | `layout` "Layout" (Display ref, req) | 10 s | display | – |
| Displays | Check layout | `display.check` | `layout` "Layout" (Display ref, req) · `is` "Is" (matches/differs, matches) | 10 s | display | – |
| Programs | Run program | `process.run` | `path` "Program" (file, req) · `args` "Arguments" (list) · `cwd` "Start in" (folder) · `wait` "Wait until it exits" (bool, true) · `exitCodes` "Exit codes that mean success" (list, 0) | 30 s | – | [notRunning] |
| Programs | Check a program is running | `process.check` | `program` "Program" (text, req) · `is` "Is" (running/notRunning, running) | 10 s | – | – |
| Scripts | Run PowerShell | `script.powershell` | `script` "Script" (multiline, req, 5 examples) | 60 s | – | – |
| Web | Web request | `http.request` | `url` "Address" (req) · `method` "Method" (GET/POST/PUT/PATCH/DELETE, GET) · `headers` "Headers" (map) · `body` "Body" · `credential` "Sign in with" (saved credential name) | 10 s | – | – |
| Android | Android input | `android.input` | `device` "Android device" (Android ref, req) · `input` "Input" (text, req, from the device's inputs) | 20 s | adb:{device} | – |
| Android | Android keys | `android.keys` | `device` · `keys` "Keys" (ordered list, req) | 20 s | adb:{device} | – |
| Android | Android sleep | `android.sleep` | `device` | 20 s | adb:{device} | – |
| Android | Check connection | `android.check` | `device` "Android device" · `is` "Is" (connected/disconnected, connected); asks a running adb only | 20 s | adb:{device} | – |
| Android | Start Android app | `android.start` | `device` · `app` "App" (text, req, searchable list or package) | 20 s | adb:{device} | – |
| Android | Connect Android device | `android.connect` | `device` | 20 s | adb:{device} | – |
| Services | Start service | `service.start` | `name` "Service name" (text, req, live list) | 30 s | service:{name} | [stopped] |
| Services | Stop service | `service.stop` | `name` "Service name" | 30 s | service:{name} | [running] |
| Services | Check a service | `service.check` | `name` "Service name" · `is` "Is" (running/stopped, running) | 10 s | – | – |
| Files | Check a file or folder | `file.check` | `path` "File or folder" (text, req) · `is` "Is" (exists/missing, exists) | 10 s | – | – |
| Wait | Wait | `wait` | `duration` "How long" (duration, req) | 1 h | – | – |
| Message | Show a message | `notify` | `text` "Message" (req) | 10 s | – | – |
| Flags | Set a flag | `flag.set` | `name` "Flag" (req, suggestions) · `value` "On" (bool, true) | 10 s | – | – |
| Flags | Check a flag | `flag.check` | `name` "Flag" · `value` "Is on" (bool, true) | 10 s | – | – |
| Flows | Run a flow | `flow.run` | `flow` "Flow" (req) · `wait` "Wait for it to finish" (bool, true) · `inputs` "Inputs" (map) | 10 min | – | – |
| Menu | Show a menu | `menu.show` | `items` "Flows" (ordered, 1–12, req) · `title` "Title" · `layout` "Layout" (list/grid) · `size` "Size" (normal/large) · `closeAfter` "Close after" (5 s–10 min, 30 s) · `wait` "Wait for it to finish" (bool, **false**) | 10 min | – | – |

Glyph: one per area; the check steps reuse their area's. Palette search also finds checks by "check".

## 2. Findings inside groups and between groups, ranked

"Saved?" = changes saved YAML. Every finding below is **No** unless stated; the one saved-name case (F11) is only an option.

| # | Where | What | Why it matters | Proposed fix | Saved? |
|---|---|---|---|---|---|
| F1 | Android | Five title shapes in one group: "Android input", "Android keys", "Android sleep" (noun), "Start Android app", "Connect Android device" (verb phrase). The group name is already "Android", so the breadcrumb reads "Android › Android keys". Bluetooth does it right ("Connect"). | The group where the user just asked for a missing step; the most visible drift. Quick-add shows "Connect, Bluetooth" vs "Connect Android device, Android". | Verb + object, group name not repeated: **Connect**, **Start app**, **Choose input**, **Send keys**, **Sleep** (and **Wake** if added). Quick-add already disambiguates by group. | No |
| F2 | all | Mixed title grammar: article ("Set a flag", "Check a service", "Run a flow", "Show a message", "Show a menu", "Check a file or folder") vs none ("Run program", "Set volume", "Start service"); noun titles ("Web request", "Project", "Wait", "Mute", "Use only these screens"). | The palette is 240 px wide and read by scanning; "Run program" next to "Run a flow" looks like two authors. | One scheme: imperative verb + bare object, no article, sentence case: Set flag, Check flag, Run flow, Show message, Show menu, Check service, Check file, Check program, Run program, Send web request, Project screens. | No |
| F3 | checks | Six check titles, six shapes: "Check default device", "Check connection", "Check a program is running", "Check a service", "Check a file or folder", "Check a flag". Their `is` values use five vocabularies (default/notDefault, connected/disconnected, running/notRunning, running/stopped, exists/missing) and `flag.check` has no `is` at all (a bool `value` "Is on"). | Checks are the M14 branching tool (D-082); the user meets them in the palette and in "if it fails" wording. | Titles "Check <noun>" (see F2). Keep the YAML words (each reads naturally); fix how they are *shown* (F4). Keep `flag.check value`: flags are on/off. | No |
| F4 | inspector, card pill | Choices show YAML spelling: `notDefault`, `notRunning`, `communications`, `external`. | camelCase in a dropdown and in a card pill ("technics · notDefault"). | Optional caption per choice in `ParamSpec` ("It isn't the default", "Not running", "Calls only"); YAML values unchanged. | No |
| F5 | palette | Group labels: plural (Displays, Programs, Flags, Flows) and singular (Audio, Bluetooth, Web, Menu, Message, Wait, Scripts); three groups hold one step named like the step (Wait, Message, Menu), plus Web, Scripts, Files (check only), Flows. 14 groups for 29 steps. | A search-less user scans 14 headers; "Menu" and "Flows" are one idea. | Fewer, themed groups, in this order: **Audio · Bluetooth · Displays · Android · Programs** (run, PowerShell, check) **· Services · Files · Web · Flow** (wait, flags, run flow, show menu, show message). Mapping only (new `Group` on the descriptor or a table in the editor); ids and areas unchanged. | No |
| F6 | palette order | Order inside groups is historical. Audio puts Check second; Android puts Connect last; elsewhere Check is last. Group order is catalog order (Android after Web, Files after Services). | Lifecycle order helps scanning: connect first, check last. | Rule: setup/connect → act → undo/stop → **Check last**. Android: Connect, Start app, Choose input, Send keys, Sleep. Audio: Set default device, Set volume, Mute or unmute, Check. | No |
| F7 | Audio | "Mute" also unmutes (`muted` off) and no title says so. | Users look for "Unmute" and don't find it. | Title **Mute or unmute**. No toggle step (not repeatable, D-081's idempotent style). | No |
| F8 | all | One error, four wordings: "No audio device called "x" in the library.", "No Bluetooth device called…", "There's no Android device called … in the library.", "No screen layout called …"; dry run says "x isn't in your Library."; Library is "Library" in the UI, "library" in messages. Check texts: BT adds a final ".", others don't. | The pop-up is where users read these. | One helper: `There's no <kind> called "x" in your Library.`; checks end with or without a full stop everywhere (pick: no stop, like Spotify isn't running). | No |
| F9 | params | Same concept, different label/name: Device picker "Device" (audio, Bluetooth) vs "Android device"; program `path`/"Program" (run) vs `program`/"Program" (check); `name` "Service name" vs "Flag"; wait toggles "Wait until it exits" vs "Wait for it to finish" with default true in `process.run`/`flow.run` and **false** in `menu.show`; `Muted`/`On`/`Is on` for booleans. | Mostly deliberate; only the picker labels are confusing in a flow that has all three kinds. | Labels only: "Audio device", "Bluetooth device", "Android device"; "Service" for `name`. Leave param names and the `menu.show` default (D-054). Do **not** rename `path`/`program`. | No (labels) |
| F10 | "Only if" | Present on six types only, with labels "It isn't the default", "It isn't connected", "It isn't running", "It's running", **"It is connected"**. None on Android, display, volume/mute (idempotent) or flag steps. | The gaps are right (T14.9 parked); the one contraction slip is cosmetic. | "It's connected". Nothing else. | No |
| F11 | ids | Id patterns: mostly `area.verb` (`bluetooth.connect`, `service.start`, `flag.set`) but also `audio.setDefault` (camel), `display.topology/outputs/layout` (nouns), `android.input/keys/sleep` (nouns), `script.powershell`, `http.request`, bare `wait`, `notify` (area "Message", title "Show a message"), `flow.run`, `menu.show`. | Ugly, not harmful: users see titles, YAML is rarely hand-edited, the editor writes ids. | **Leave every id.** For new steps: `area.verb`, camelCase. A rename needs an alias table in the config mapper kept forever (§5), so old flows and old backups still load; not worth it for the gain. | **Yes if done** |
| F12 | durations | Defaults: 10 s for everything quick, 20 s Bluetooth/Android, 30 s services/programs, 60 s PowerShell, 10 min flows/menus, 1 h wait. Consistent enough. | – | No change. | – |
| F13 | glyphs | One glyph per area, checks reuse it; Android uses "Tv" (E8B2). | – | No change. | – |

What is already consistent and should be kept: device key is always `device`; checks are always `<area>.check` with an `is`; locks name the device; every
failure text is a plain sentence; durations/percent units are the same everywhere; "If it fails" wording is shared.

## 3. Missing actions by group

Priority H/M/L = how likely the user's flows need it. Effort S ≤ 1 PR small, M one PR, L spike + PR. Risk = could break the hard rules.
"Dup" = a user can already do it with Run program / Run PowerShell (the palette's examples already name some). Recommendation: **Add**, **Later**, **Spike**, **No**.

| Group | Proposal | Pri | Eff | Risk / notes | Rec |
|---|---|---|---|---|---|
| Cross-group | **"Wait until" is already possible**: Check + Try again + Wait between tries (D-081 says so) but no doc tells users. Add a recipe to UX/FEATURES and a hint in the Check steps' help; no new step. | H | S | none | **Add (docs)** |
| Programs | **Close program** (`process.close`: program name or path; ask the main window to close, then end the tree after a "force after" time). The natural pair of Run program; today only the PowerShell example "Close an app". | H | M | closes only the user's own processes; an elevated one fails with a sentence. No new dependency (`Process`). Not idle-costly. | **Add** |
| Programs | **Open** a file, folder or web address with its default app (Steam `steam://`, URL, document). `process.run` can't open a URL (it starts an exe; only the no-wait branch uses shell execute). | M | S | Dup of the PowerShell "Open a website" example, but common and needs no script. Could instead be "Run program accepts an address". | **Later** (ask) |
| Programs | Wait for a program to start/exit | L | M | covered by the "Wait until" recipe | No |
| Scripts | More PowerShell examples: lock PC (present), sleep, power plan (`powercfg /setactive`), dark mode (HKCU), open URL (present). Power/lock/sign-out/restart as steps would be Dup. | M | S | none | **Add (examples)**; no power steps |
| Android | **Stop app** (`am force-stop <pkg>`), pair of Start app, same picker. | M | S | same adb process, lock and timeout | **Add** |
| Android | **Disconnect** (`adb disconnect`), pair of Connect; D-096 said "add it the same way if wanted". | L | S | none | **Later** (ask) |
| Android | **Wake** (`wakeup`), pair of Sleep; Choose input already wakes. Alternatively keys `wakeup`. | L | S | none | **Later** |
| Android | Check (connected / app running) | – | – | T14.9 parked; stays parked | No (parked) |
| Android | Open address / deep link (`am start -d`), type text (`input text`), install | L | S–M | niche; install is scope creep | No |
| Audio | Relative volume (up/down by n), toggle mute, per-app volume/routing, save and restore the previous default device | L | M–L | per-app uses undocumented APIs; "restore previous" needs state between flows (idle = zero, backup surface). The user's flows name devices explicitly. | No |
| Audio | Check volume / muted | L | S | rarely branches | No |
| Bluetooth | Non-audio devices (controllers, keyboards): the step is audio-only by design (KS one-shot, no admin; D-030). HID support needs a spike. | M | L | may need admin or pairing UI; unknown | **Spike** (ask) |
| Bluetooth | Bluetooth / Wi-Fi radio on/off (`Windows.Devices.Radios`) | L | M | may prompt for access in an unpackaged app: unverified; could need admin | Spike, low |
| Displays | **Turn screens off** (monitor power; PC stays on; any input wakes it) | M | S | `SC_MONITORPOWER`, no admin, no focus; fits the TV flows | **Add** (ask) |
| Displays | HDR on/off, resolution/refresh/scale/rotation, brightness | L | M–L | HDR via CCD advanced colour is feasible; layout capture already stores resolution/refresh. Brightness over WMI often needs admin. | Spike HDR, low |
| Displays | Check (layout/topology) | – | – | T14.9 parked | No (parked) |
| Services | Restart service | L | S | needs admin like the rest of the group (D-033) | No |
| Services | Elevated path for Sunshine and friends | – | – | contradicts D-008; D-033's revisit trigger (opt-in scheduled task) | No (user decision) |
| Files | File actions (copy, move, delete, write text, create folder) | L | M | Dup of PowerShell/`cmd`; invites a file manager; not in GOALS | No |
| Web | **Wake on LAN** (UDP magic packet to a MAC) | M | S | no admin, no dependency; fits "start the PC/TV" flows; group name "Web" is then a poor fit (rename to "Network" is label-only) | **Later** (ask) |
| Web | Check a web address answers (health check, e.g. Sunshine up) | L | S | read-only check family member, reuses the HTTP code | Later |
| Web | Webhook helpers, Discord/Telegram steps | – | – | `http.request` already does it; scope creep | No |
| Message | Message options (title, how long, sound); clipboard | L | S | the pop-up duration is a global setting | No |
| Flags | Toggle flag, clear all flags | L | S | flags are in memory only (D-029); toggles are not repeatable | No |
| Flows | **Stop flow** (cancel a named running flow, or end this one quietly) — needed for "check failed, stop here" and for restarting a looped flow | L | S–M | engine `cancel` exists; `StepEndsFlowException` exists | **Later** (ask) |
| Flows | Run after a delay, schedule, if/else | – | – | D-082, schedule triggers are "Later" in FEATURES F2 | No |
| (new) System | Windows toggles (dark mode, night light, focus assist, power plan, lock/sleep/shutdown) as steps | – | – | each is a PowerShell line (Dup); a "System" group would grow forever | No (examples instead) |
| Scope creep | Mouse/keyboard macros, window moving/focus, registry, scheduled tasks, env vars, speech | – | – | focus stealing is a hard rule; the user already runs AutoHotkey; not a successor goal | No |

Admin or new dependency: nothing recommended needs either. Possible admin: Bluetooth HID, radios, brightness, service restart.
Possible new dependency: none (HDR, screen-off, WoL, process close, adb are Win32/BCL/adb). All proposed steps are event-driven and hold nothing when idle.

## 4. Migration and compatibility

- Titles, group labels, group order, choice captions, error text, examples: **no migration**. A card without `name:` follows the new title; one
  with `name:` keeps the user's text. Tests that assert old titles change in the same PR.
- New steps and params: additive; old files unchanged; a build that doesn't know a new id marks that flow's step unknown (existing rule), so a
  config using new steps opened by an older engine fails only that flow (fail-soft, D-021).
- Id/param renames (only if the user insists, choice 9): `ProviderCatalog.Aliases` (old id → new id, old param → new param), applied in
  `ConfigMapper` on load, saved with the new name; **kept forever**, because old backups and hand-edited files exist. No rename is in the plan.

## 5. Notes for the backup/restore task (not started)

What holds user state, from the code and ARCHITECTURE:
- `%APPDATA%\DazeControl\config.yaml` — everything the engine needs: `settings` (popup, `stepDefaults`), `library` (audio match text, Bluetooth
  addresses, display layouts as base64 CCD data, Android devices with inputs and adb path) and `flows` (ids are YAML keys; `inputs`, `after`, `with`).
  `backups\` beside it holds one `.bak` per editor session (newest 10), the only backup that exists today. `--config` redirects both.
- `%LOCALAPPDATA%\DazeControl\editor.json` — this PC's view state (window place, nav pane, per-flow layout, flow-list sort/collapsed groups).
  Optional to restore. `history.jsonl`, `logs\`, `android-apps\` cache are regenerable.
- **Not in the files:** saved web credentials (Windows Credential Manager; YAML has only their name, D-014, so a restore must say which names to
  re-save); flags (memory only, D-029); desktop `.lnk` shortcuts (D-049, rebuildable from flow ids); autostart (HKCU Run).
- **Machine-specific values inside config.yaml:** Bluetooth addresses (the device must be paired on the new PC), display layouts (re-resolved by
  monitor, fail with "n screen(s) aren't connected" elsewhere), audio `match` text, Android `adb` path, absolute paths in `process.run`,
  `file.check` and `cwd`. A restore on another PC should load, validate and list what will not work, never rewrite.
- Compatibility: `version: 1` stays; the mapper keeps unknown step ids as problems per flow (fail-soft), so a backup made with a newer build restores
  with those flows disabled and listed. Any future id alias must stay forever (§4).
- Flow ids and step ids are the stable part; titles are not stored, so this review's title changes never affect a backup.

## 6. Answers (user, 2026-11-06)

All 12 open choices answered "yes, as recommended"; recorded in D-102. Not built: Open file/address, Android Disconnect and Wake, Wake on LAN, web address check, Stop flow (later); HDR and non-audio Bluetooth spikes (no for now); no id renames; Services unchanged.
