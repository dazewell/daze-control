# Gap analysis: what people expect from a desktop automation engine, and what Daze Control lacks

Snapshot of 2026-11-13 (main at c685f76, v0.54.0 about to ship). Asked for by the user (2026-11-12): a deep look at missing
features against comparable tools, then a ranked roadmap. The roadmap itself is PLAN.md M24 to M28; the decision is D-132.
This file is research: it is not kept up to date. When a gap is built, its facts move to FEATURES / ARCHITECTURE / UX.

How to read a gap: **who expects it** (tools that have it), **how common** (High = nearly every tool, people look for it in
the first hour; Medium = power users; Low = niche), **fit** with GOALS and the hard rules, a **sketch**, the **risk**,
**size** (S ≤ 1 small PR, M one PR, L spike + PR or several PRs) and the **model** that should build it.
Verdicts: **Plan** (task id in PLAN), **Needs your yes** (reverses a recorded decision or costs money: M28), **Later**
(fits, nobody needs it yet), **Rejected** (with the reason).

Win32 API names in the sketches are from memory, not from a build: each task's first step is to check them (CsWin32
metadata, Microsoft Learn) before designing around them.

## 1. What Daze Control is today (from the code, not the docs)

- **Ways a flow starts:** global shortcut (`RegisterHotKey` chords), tray item, `dctl run`, editor Run / Test, desktop shortcut
  (`--run`), a pick in a flow menu, another flow (`flow.run`), a schedule (`at` + days, `every` + window, `once`), engine
  start, and 13 system events: session lock / unlock / logon / logoff, power resume / AC / battery, device added / removed,
  default sound device changed, screens changed, network connected / disconnected (`via`, `ssid`). Trigger conditions: only
  `onBattery` / `onAC`. No event but the network ones says *which* device (`TriggerEvent.Detail` is empty elsewhere).
- **Steps:** 56 types in `ProviderCatalog` (20 of them read-only checks), in 11 palette groups. Audio 4, Bluetooth 3,
  Displays 6, Android 14, Programs 4 (run, close, check, PowerShell), Services 3, Files 2 (checks only), Web 1
  (`http.request`), Network 5 checks, This PC 5 checks (power, battery, idle, locked, time), Flow 9 (wait, message, Make
  text, flags, Run flow, Show menu, Ask, Pick device).
- **Flow control:** free graph with success / failure / always links, cards that run together, per-step Try again / Wait /
  Give up after / If it fails, "Only if" on 6 step types, checks with a failure branch (D-082), nested calls with device and
  text inputs and outputs, `whenRunning` ignore / queue / restart, engine-wide device locks.
- **Values (M23):** typed values per step type, `{step.value|fallback}` in the text settings that opt in, device values
  (`$result:<id>.<value>`), Make text, `DAZE_*` variables for PowerShell. No way to *decide* on a value, no way to take a
  part of one, nothing kept after the run.
- **Editor:** flows list (groups, search, sort, bulk actions, undo), canvas with auto layout, inspector rendered from the
  catalog, Library, pickers and examples, Dry run, Test this step, live run view, Runs timeline (last 100), Problems,
  backup / restore, three New flow templates.
- **CLI / ops:** `dctl` run / cancel / list / validate / status / history / devices / check / shortcut / backup / restore /
  update; per-user install with Stable and RC channels; logs 7 days; no admin; unsigned; win-x64; English only.

## 2. The benchmark, by capability

| Tool | What defines it | What its users would look for here |
|---|---|---|
| AutoHotkey v2 | Hotkeys with context (`#HotIf`), hotstrings, `Send` / `Click`, `WinActivate` / `WinMove` / `WinWait`, clipboard (`A_Clipboard`, `OnClipboardChange`), files, registry, `SoundSetVolume`, `SetTimer`, `InputBox`, loops, RegEx [S1] | Keys to an app, window moves, per-app shortcuts, text expansion, clipboard |
| Power Automate Desktop | ~50 action groups: Variables, Conditionals, Loops, Flow control, Error handling, System (run / terminate / wait for process, Ping), Workstation (lock, log off, shut down, screenshot, sound, resolution), Mouse and keyboard, Clipboard, Message boxes (message, input, lists, files, custom forms), File / Folder, Text, Date time, HTTP, UI / browser automation, OCR, Excel… [S2] | If on a value, loops, input dialogs, lock / shut down, send keys, file actions, a debugger with "run from here" |
| PowerToys | 31 utilities, among them Keyboard Manager (remap), FancyZones and Workspaces (window layouts, "launch a set of applications to custom positions"), Awake, Light Switch (theme by time of day), Power Display (monitor brightness, input source), Command Palette, Advanced Paste [S3] | Window layouts, keep awake, dark mode on a schedule, monitor input and brightness |
| Home Assistant | Trigger → conditions → actions. 14 trigger types (state, numeric state, time, time pattern, sun, zone, webhook, MQTT, template, event, tag…), trigger ids and variables, modes single / restart / queued / parallel, per-step `enabled: false` and `continue_on_error`, traces, blueprints [S4] | Conditions on every automatic start, "why didn't it run", turn a step off, remote start, recipes to import |
| Stream Deck | Keys run actions: Hotkey, Open, Website, Multimedia, Multi Action (sequence with delays), Run Command; Smart Profiles switch by the app in front; a marketplace of plugins and profiles [S5] | Press a shortcut, open things, media keys, per-app behaviour, a gallery; a way to call Daze Control from a key |
| Task Scheduler | Triggers: time, daily, weekly, monthly, monthly day-of-week, idle, boot, logon, session lock / unlock, event log; conditions (idle, AC, network), "wake the computer to run this task" [S6] | Monthly schedules, idle, conditions, wake to run |
| Keyboard Maestro | 39 trigger kinds: Hot Key, Typed String, Application, Focused Window, Folder, USB Device, Wireless Network, Audio Output Changed, Display Layout Changed, Clipboard Changed, Idle, Sleep, Wake, Unlock, Login, Time of Day, Cron, Periodic, URL, Web Server, Remote, Public Web, MIDI, Status Menu, Macro Palette… [S7] | App and window events, a named USB device, folder events, a URL that runs a flow, remote start |
| Hammerspoon | Watchers for application, audio device, battery, caffeinate (sleep / lock), path, USB, Wi-Fi, screen; `hs.window` / `hs.layout`, `hs.urlevent`, `hs.httpserver`, `hs.pasteboard`, `hs.brightness` [S8] | The same events; window layout; URL events |
| Apple Shortcuts | Personal automations on time of day (sunrise / sunset too), Wi-Fi, Bluetooth device, app opened / closed, battery level, charger, Focus…; If, Repeat, Ask for Input, a gallery [S9] | A named Bluetooth device as a trigger, app events, ask for text, a gallery |
| IFTTT / Zapier class | "When this happens there, do that here": webhooks in and out (not fetched; general knowledge; Home Assistant's webhook trigger and Keyboard Maestro's web triggers [S4][S7] stand in as sources) | A URL or message from outside that starts a flow |

Reading across the rows, the expectations fall into five groups. Daze Control is **ahead** on device orchestration
(Bluetooth audio, default device, screens, Android / Fire TV), failure handling (fallback branches, retries, policies), the
no-focus pop-up and menu, footprint and fail-soft. It is **level** on schedules, session / power / network events,
subflows and the editor basics. It is **behind** on five things, in this order:

1. **Deciding.** Values exist but nothing can branch on one, and an automatic start has two conditions to choose from.
2. **Reacting to the right thing.** Events don't say which device; there are no app, file or flow events.
3. **Everyday glue.** Open a file or address, stop a flow quietly, media keys, power, mute toggle, clipboard, keys, windows.
   Most of these are recorded "No" (REVIEW §3, 2026-11-06): they were judged before schedules, events and values existed.
4. **Getting started and sharing.** Three templates, no gallery, no way to hand one flow to someone.
5. **Being called from outside.** No inputs on `dctl run`, no link, nothing remote.

## 3. Gap matrix

`Plan` cells name the PLAN task. Sketches for everything planned or awaiting a yes follow in §4; rejections in §5.

### 3.1 Triggers and conditions

| # | Gap | Who expects it | Common | Size · model | Verdict |
|---|---|---|---|---|---|
| G-01 | Conditions on automatic starts ("Only run if": Wi-Fi, time, program, any check) | Home Assistant conditions, Task Scheduler Conditions tab, Shortcuts | High | M–L · Opus | Plan T25.1 |
| G-02 | Stop a flow here, quietly (a guard after a check) | PAD Stop flow, HA `stop`, Keyboard Maestro Cancel | High | S · Sonnet | Plan T24.1 |
| G-03 | *Which* device on device and sound events; the event's detail as a value | Keyboard Maestro USB Device / Audio Output Changed, Shortcuts Bluetooth, Hammerspoon `usb.watcher` | High | M · Sonnet | Plan T24.6 |
| G-04 | Screens turned off / on, stepped away / came back | Task Scheduler idle, Keyboard Maestro Idle / Sleep / Wake, Hammerspoon `caffeinate.watcher` | Medium | S · Sonnet | Plan T24.4 |
| G-05 | A program opened, closed, came to the front | Keyboard Maestro Application / Focused Window, Stream Deck Smart Profiles, Shortcuts App, Hammerspoon, AHK `WinWait` | High | L · Opus | Plan T26.1, T26.2 |
| G-06 | A flow finished or failed (one place to handle failures) | Home Assistant events, PAD "On block error" | Medium | S · Sonnet | Plan T26.3 |
| G-07 | A file or folder changed | Keyboard Maestro Folder, Hammerspoon `pathwatcher`, HA folder watcher | Medium | M · Sonnet | Plan T26.5 |
| G-08 | Day of the month, every N days, sunrise / sunset | Task Scheduler monthly, Keyboard Maestro Cron, HA sun, Shortcuts, PowerToys Light Switch | Medium | M · Sonnet | Plan T26.6 |
| G-09 | A shortcut that works only while one program is in front | AHK `#HotIf`, Keyboard Maestro groups, Stream Deck Smart Profiles | Medium | M · Opus | Plan T26.4 |
| G-10 | A link that starts a flow (`dazecontrol://run/…`) | Keyboard Maestro URL trigger, Hammerspoon `urlevent`, Shortcuts URL scheme | Medium | S–M · Sonnet | Plan T26.7 |
| G-11 | "Idle for N minutes" with any N | Task Scheduler, Keyboard Maestro Idle | Medium | M | Later: needs a re-armed timer; G-04 gives Windows' own away signal for free |
| G-12 | Wake the PC to run a schedule | Task Scheduler | Low | M | Later (D-107 rejected Task Scheduler for v1; a resume timer is unproven on Modern Standby) |
| G-13 | Typed-text triggers, key remapping, key sequences, mouse-button triggers | AHK, Keyboard Maestro Typed String, PowerToys Keyboard Manager | High (in AHK's world) | L | Rejected (§5) |
| G-14 | Controller, MIDI or deck-device triggers | Keyboard Maestro, Stream Deck | Low | – | Rejected (D-086); a deck key calls `dctl` or a link |
| G-15 | Clipboard changed | Keyboard Maestro, AHK `OnClipboardChange` | Low | S | Rejected (§5) |
| G-16 | Location, NFC, calendar, mail | Shortcuts, Home Assistant | Low on a PC | – | Rejected (§5) |

### 3.2 Steps

| # | Gap | Who expects it | Common | Size · model | Verdict |
|---|---|---|---|---|---|
| G-20 | Open a file, folder or address with its own app | Stream Deck Open / Website, PAD Run application, Shortcuts Open URL, Keyboard Maestro Open | High | S · Sonnet | Plan T24.2 |
| G-21 | Windows: move to a screen, maximize / minimize, bring to front, close | PowerToys Workspaces / FancyZones, AHK `Win*`, Hammerspoon `hs.layout`, Keyboard Maestro | High | L · Opus | Needs your yes T28.5 |
| G-22 | Click a button inside an app, read its text, OCR, browser, Excel, mail, PDF | PAD | High in RPA | XL | Rejected (§5) |
| G-23 | Press keys / type text into the app in front | Stream Deck Hotkey / Text, AHK `Send`, PAD Send keys, Keyboard Maestro | High | M · Opus | Needs your yes T28.4 |
| G-24 | Mouse moves and clicks, a macro recorder | AHK, PAD recorder, Keyboard Maestro | Medium | L | Rejected (§5) |
| G-25 | Play / pause / next on this PC | Stream Deck Multimedia, AHK media keys, PAD Send keys (media keys) | Medium–High | S · Sonnet | Plan T24.5 |
| G-26 | File actions: copy, move, delete, zip, write text | PAD File / Folder / Compression, AHK `File*` | Medium | M | Rejected (§5): examples instead |
| G-27 | Copy text to the clipboard, read the clipboard as a value | PAD Clipboard, AHK `A_Clipboard`, PowerToys Advanced Paste | Medium | S · Sonnet | Needs your yes T28.3 |
| G-28 | Wake on LAN | Home Assistant `wake_on_lan`, Stream Deck plugins | Medium | S · Sonnet | Plan T24.7 |
| G-29 | Check that an address answers (host, port or web page) | PAD Ping, Home Assistant ping | Medium | S · Sonnet | Plan T24.8 |
| G-30 | Remote start and publishing state (MQTT) | Home Assistant MQTT trigger, Keyboard Maestro Remote / Web Server / Public Web, IFTTT-class webhooks | Medium (High for smart-home users) | L · Opus | Needs your yes T28.11 |
| G-31 | A web server in the engine for webhooks from the network | Keyboard Maestro Web Server, Hammerspoon `httpserver` | Medium | M | Rejected (§5) |
| G-32 | Ready-made Telegram / Discord / ntfy steps | IFTTT-class tools | Medium | S each | Rejected as steps: gallery recipes over `http.request` (T27.1) |
| G-33 | Lock, sleep, hibernate, shut down, restart, sign out | PAD Workstation, Task Scheduler, Stream Deck System, AHK `Shutdown` | High | S · Sonnet | Needs your yes T28.1 |
| G-34 | Keep the PC awake (on / off / for a while) | PowerToys Awake, Hammerspoon `caffeinate` | Medium | S–M · Sonnet | Needs your yes T28.8 |
| G-35 | Dark / light theme | PowerToys Light Switch, Shortcuts Appearance | Medium | S · Sonnet | Needs your yes T28.7 |
| G-36 | Volume up / down by steps, toggle mute, check muted (one key for the microphone) | Stream Deck, AHK `SoundSetVolume +5`, `SoundSetMute -1` | High | S · Sonnet | Needs your yes T28.2 |
| G-37 | Volume of one app | Stream Deck plugins, AHK | Low–Medium | M | Later (documented session API, but sessions come and go: needs a design) |
| G-38 | Monitor brightness and input source (DDC/CI) | PowerToys Power Display, Hammerspoon `hs.brightness` | Medium | M · Opus | Needs your yes T28.9 (spike) |
| G-39 | Night light, do not disturb / focus, wallpaper | Shortcuts Focus, PowerToys | Medium | – | Rejected (§5) |
| G-40 | Play a sound file; speak text | PAD Play sound, AHK `SoundPlay` | Low | S | Later (sound file); speech rejected (REVIEW §3) |
| G-41 | Registry, environment variables, printers, screenshots | PAD Workstation / System, AHK `Reg*` | Low | – | Rejected (§5) |
| G-42 | Restart a service, an admin path | PAD Windows services | Low | – | Rejected (D-008, D-033) |
| G-43 | Bluetooth: non-audio devices, radio on / off | Shortcuts, Stream Deck plugins | Medium | L | Later (REVIEW: spike "no for now") |
| G-44 | Send one app's sound to another device | Stream Deck plugins | Low | L | Rejected (undocumented API, REVIEW §3) |
| G-45 | HDR, resolution, refresh rate, scale as steps | PAD Set screen resolution, PowerToys | Low | M–L | Later (layouts already store resolution and refresh; HDR spike "no for now") |
| G-46 | Android Disconnect | pair of Connect (REVIEW "Later") | Low | S · Sonnet | Later: any quiet hour, same file as Connect |

### 3.3 Messages and questions

| # | Gap | Who expects it | Common | Size · model | Verdict |
|---|---|---|---|---|---|
| G-50 | Ask for text or a number | PAD input dialog, AHK `InputBox`, Keyboard Maestro Prompt, Shortcuts Ask for Input | Medium–High | M–L · Opus | Needs your yes T28.6 |
| G-51 | Message with a title, its own duration, a button that runs a flow | PAD Display message, Home Assistant actionable notifications | Low–Medium | S–M | Later (REVIEW "No": duration is a global setting) |
| G-52 | Windows notification-centre toasts | PAD, AHK `TrayTip` | Low | M | Rejected (D-012) |
| G-53 | File / date pickers, custom forms | PAD | Low | L | Rejected (§5) |

### 3.4 Flow control and data

| # | Gap | Who expects it | Common | Size · model | Verdict |
|---|---|---|---|---|---|
| G-60 | Decide on a value: "Check value" (is, contains, at least…) | Every tool: PAD If, HA conditions, AHK `if`, Shortcuts If | High | S–M · Sonnet | Plan T24.3 |
| G-61 | Repeat: N times, until it works | PAD Loops, HA `repeat`, Keyboard Maestro Repeat, AHK `Loop` | Medium | M · Opus | Plan T27.6 |
| G-62 | For each item of a list | PAD For each, HA `for_each` | Low–Medium | L | Later (inputs + calls cover "the same chain for two devices") |
| G-63 | Turn one step off without deleting it | HA `enabled: false`, PAD Disable action, Keyboard Maestro | Medium | M · Opus | Plan T25.4 |
| G-64 | if / else blocks, expressions, variables that change, arithmetic | PAD, AHK, HA templates | High in scripting tools | L | Rejected (§5: D-082, D-129) |
| G-65 | Wait inside a flow for an event, without polling | HA `wait_for_trigger`, Keyboard Maestro Pause Until, PAD Wait for process | Medium | M | Later: a check with Try again does it today (polls only while the flow runs) |
| G-66 | Several runs of one flow at the same time | HA mode `parallel` | Low | M | Later (device locks would serialize most of it anyway, D-010) |
| G-70 | Take a part of a text: JSON field, pattern, between two texts | HA `value_template`, PAD Text / JSON, Shortcuts Get Dictionary Value / Match Text | Medium | M · Sonnet | Plan T25.2 |
| G-71 | A value in a number setting ("Set volume to {x}") | PAD variables everywhere | Low–Medium | M · Sonnet | Plan T25.3 |
| G-72 | Number and date formats, date arithmetic | PAD Date time, HA templates | Low | M | Later (D-129: format specifiers not proposed); PowerShell |
| G-73 | Flags that survive a restart | HA helpers, Keyboard Maestro variables | Low–Medium | S · Sonnet | Needs your yes T28.10 (D-029 revisit) |
| G-74 | Lists, tables, CSV / Excel data | PAD | Low here | L | Rejected (§5) |

### 3.5 Editor and UX

| # | Gap | Who expects it | Common | Size · model | Verdict |
|---|---|---|---|---|---|
| G-80 | A gallery of recipes with "pick your devices" | HA Blueprints, Shortcuts Gallery, Stream Deck Marketplace, PAD examples | High | M · Sonnet | Plan T27.1 |
| G-81 | Share one flow: export to a file, import with the Library items it needs | HA blueprint import, Keyboard Maestro `.kmmacros`, Stream Deck profiles, Shortcuts links | Medium | L · Opus | Plan T27.2 |
| G-82 | Runs show what each step knew: its values and the text it really used | Home Assistant traces | Medium | M · Sonnet | Plan T25.5 |
| G-83 | Run from this step | PAD "Run from here", Keyboard Maestro "Try action" | Medium | M · Opus | Plan T27.4 |
| G-84 | Step through with pause and breakpoints | PAD debugger, Keyboard Maestro debugger | Low–Medium | L | Later (Dry run, Test this step and T27.4 cover most) |
| G-85 | Notes on a flow and on a step | PAD Comments, HA description, Keyboard Maestro Comment | Medium | S · Sonnet | Plan T27.3 |
| G-86 | "Why didn't it run?": last time a trigger fired, started or was held back | HA traces and logbook, Task Scheduler History | Medium | S | Plan: inside T25.1 |
| G-87 | A mistyped setting in YAML is ignored silently | (reliability; ARCHITECTURE open question) | – | S–M · Sonnet | Plan T27.5 |
| G-88 | Search across all flows ("which flows run PowerShell?") | PAD, Keyboard Maestro | Low | S | Later (Library "Used by" covers devices) |
| G-89 | Free positioning, YAML comments kept | – | – | – | Non-goals (D-020, D-004) |

### 3.6 Reliability, security, packaging, CLI, accessibility, languages

| # | Gap | Who expects it | Common | Size · model | Verdict |
|---|---|---|---|---|---|
| G-90 | One-click report for a bug (versions, problems, log tail, device lists; no secrets) | PowerToys bug report tool, most tray apps | Medium once there are other users | S · Sonnet | Plan T27.7 |
| G-91 | Signed installer (no SmartScreen warning) | Every mainstream tool | High for strangers, zero for you | cost + identity | Needs your yes T28.12 |
| G-92 | `winget install`; ARM64 build | PowerToys, AHK | Medium / Low | S / M | With T28.12 / Later |
| G-93 | An imported flow can hold scripts: show them before importing | Shortcuts "untrusted shortcuts", HA blueprint review | High once sharing exists | – | Plan: inside T27.2 |
| G-94 | Run steps as administrator | PAD (elevated), Task Scheduler | Medium | – | Rejected (D-008) |
| G-95 | Inputs on the command line: `dctl run flow name=value` | Shortcuts `shortcuts run -i`, Keyboard Maestro trigger value, Stream Deck arguments | Medium | S–M · Sonnet | Plan T24.9 |
| G-96 | `--json` output, `dctl flag`, `dctl pause` | scripting users | Low–Medium | S · Sonnet | Plan T27.8 |
| G-97 | A Stream Deck plugin, a PowerToys Command Palette extension | Stream Deck Marketplace | Medium | M each | Rejected for now: a deck key runs `dctl.exe` or a link (recipe in the T24.10 gate) |
| G-98 | Screen-reader pass | Windows apps in general | – | – | Rejected by D-091 (your decision); keyboard, outline, high contrast and 225 % text are done |
| G-99 | Other languages | Every mainstream tool | High for a public tool, zero today | L | Rejected for now (§5) |

## 4. Sketches (planned and "needs your yes" gaps)

**G-01 Only run if (T25.1).** A flow gets `onlyIf:`, a list of check steps with their settings (any of the 20 checks: they
carry their own device, program, network or time). Schedules and events evaluate it before the run; unmet = no run, no
pop-up, one log line and "Held back: Wi-Fi isn't Home" on the flow card and the "When this runs" card (G-86). Starting it
yourself (shortcut, tray, CLI) ignores the list unless the flow says "also when I start it". `only: onBattery` keeps
loading as a power check. Fail-open like D-081: a check that can't be read lets the run start. Risk: checks that take a
device lock (Bluetooth) must time out; evaluation must not create history rows for every Wi-Fi blip.

**G-02 Stop flow (T24.1).** `flow.stop`: "Quietly" (nothing shown, not a failure) or "As failed" with a message that takes
values. Placed on a check's failure link it is today's guard for automatic starts. In a called flow, a quiet stop ends
only that flow and the caller's step counts as done (unlike a menu closed with nothing picked, which ends both, D-054);
"As failed" fails the caller's step. Risk: the Runs wording ("Stopped here", not "Cancelled") and the tray glyph.

**G-03 Which device (T24.6).** `device:` on `device.added` / `device.removed` (the name contains this text) and on
`audio.default-changed` (the new default is this Library item). The source reads the name of what came or went and puts it
in `TriggerEvent.Detail` (cooldown is already per detail); `{@event}` gives it to messages ("Xbox Wireless Controller").
Risk: one physical device raises several interface arrivals with different names; the debounce must fold them.

**G-04 Screen and away events (T24.4).** `display.off` / `display.on` and `session.away` / `session.back` from the
power-setting notifications the power source already listens to (console display state, user presence). Push only, no
timer: Windows decides "away" (it follows the screen-off time). Risk: our own "Turn screens off" raises `display.off`.

**G-05 App events (T26.1 spike, T26.2).** `app.opened`, `app.closed`, `app.front`, `app.back` with `program:` (exe name).
One shell-hook registration on the existing message window reports top-level windows created, destroyed and activated; the
window's process gives the exe; "closed" = that process ended (a wait on its handle, no polling). Programs with no window
are out (documented limit; D-107: no WMI, no ETW). Risk: the registration wakes the engine on every Alt+Tab (microseconds;
the gate is `measure.ps1` at 0 ms over 10 idle minutes) and elevated processes can't be opened by name.

**G-06 Flow events (T26.3).** `flow.finished` / `flow.failed` with an optional `flow:`; `{@event}` = the flow's name. A
"When any flow fails → send me a message" flow is the global error handler. The chain guard already stops loops.

**G-07 File events (T26.5).** `file.created` / `file.changed` / `file.deleted` with `path:` (a folder) and `filter:`
(`*.pdf`); overlapped `ReadDirectoryChangesW`, debounce per file, `{@event}` = the path. A folder that is missing or on a
drive that left disables only that trigger and is retried on `device.added` and resume, never on a timer.

**G-08 Schedules (T26.6).** `at` gains `dayOfMonth: 1..31 | last` and `every: 3d`; `at: sunset+00:30` with a latitude and
longitude typed once in Settings (pure calculation in `ScheduleTable`, no location API, no consent prompt).

**G-09 Shortcut only in one program (T26.4).** A hotkey gets `in: Spotify.exe`. The key is registered only while that
program is in front (using G-05's activation events), so other apps keep the key. Risk: a register / unregister on every
focus change must stay cheap and never leave a key registered after a crash of the tracked app.

**G-10 Links (T26.7).** `dazecontrol://run/<flow>?name=value` registered under HKCU by the engine (no admin) only while at
least one flow has "Can be started from a link" on; other flows refuse. Any web page can show such a link, so the opt-in
and the browser's own "Open Daze Control?" question are the two gates; inputs go through the same check as `flow.run`.

**G-20 Open (T24.2).** `process.open` "Open file or address": one target (path, folder, `https://…`, `steam://…`,
`ms-settings:…`) handed to the shell as the thing to open, never as a command line; values are allowed and are escaped so
they can't change the scheme or add arguments. No wait. Risk: none new (same activation as Run program).

**G-21 Windows (T28.5, needs your yes).** `window.move` (a program's window to screen N as Maximized / Left half / Right
half / a position saved from "where it is now"), `window.state` (minimize, restore, close), `window.focus`. Completes the
TV flows (switch screens, then put the game there). Why it reverses REVIEW's "No": the focus rule is about *our* windows;
a step the user placed is their intent, and moving, sizing and minimizing need no focus at all. Risk: `window.focus` only
works reliably when a shortcut started the flow; elevated windows refuse; multi-window apps need a "which window" rule.

**G-23 Press keys (T28.4, needs your yes).** `input.keys`: an ordered list of chords (Ctrl+Shift+M, F11, Win+P) sent to
whatever is in front, with the recorder the shortcut box already has; "Type text" as a second step later. No recording, no
mouse, no hotstrings, no remapping: that stays AutoHotkey's. Risk: keys reach the wrong window if focus moved; an elevated
window ignores them (say so); held modifiers from the triggering shortcut must be released first.

**G-25 Control media (T24.5).** `audio.media`: Play or pause, Play, Pause, Stop, Next, Previous for this PC, as app
commands to the shell (no key simulation). Windows passes them to the app that owns media. It can't tell whether anything
played, so it only fails when the shell is absent. Agents don't run it live (it would touch the user's music).

**G-27 Clipboard (T28.3, needs your yes).** `clipboard.set` (text that takes values) and `clipboard.get` (gives `text`,
marked Sensitive so it stays out of logs). "Send the link I copied to the TV" = get → Android Open address. Reverses
REVIEW's "clipboard: No", which predates values. Risk: the clipboard can hold a password: never logged, never in history.

**G-28 Wake over network (T24.7).** `network.wake`: MAC address, sent as a magic packet on each connected network's
broadcast address. With G-29 and Try again: "wake the NAS, wait until it answers, then…".

**G-29 Check address (T24.8).** `network.reach`: `host:port` (a TCP connect), a web address (any answer counts) or a bare
host (ping); gives `ms`. Read-only, in `dctl check`.

**G-30 MQTT (T28.11, needs your yes).** Library item "MQTT broker" (address, credential by name); events `mqtt.message`
(`topic:`), step `mqtt.publish`, optional state topics (flow started / finished, locked). Outbound connection only, so no
firewall rule and no admin. Idle = zero needs MQTT keep-alive off and TCP keep-alive (kernel-side) instead, to be proven
by a spike. A small hand-written MQTT 3.1.1 client avoids an AOT question about a library. Risk: a resident socket is new
ground for the footprint gate; reconnects must be event-driven (network connected, resume), not timed.

**G-33 Power (T28.1, needs your yes).** `pc.power`: Lock, Sleep, Hibernate, Shut down, Restart, Sign out; the last three
show a 10 s notice with Cancel first. Reverses REVIEW's "No (examples instead)": schedules and events made "at 23:30
sleep", "battery under 10 % → hibernate" first-class flows, and a PowerShell line costs a process start and reads as code.
One step, six choices: the "System group grows forever" worry is met by not adding a second one. Agents never run it live.

**G-34 Keep awake (T28.8).** `pc.keepAwake`: On / Off / For a time, held as a power request (visible in `powercfg
/requests`), dropped on Off, at engine exit, or by one due-time timer. For the PC as a streaming host. State between flows
is the new part (flags are the precedent), hence the question.

**G-35 Theme (T28.7).** `pc.theme`: Dark / Light for apps, Windows or both (the documented registry values plus the
settings-changed broadcast). With G-08's sunset it replaces PowerToys Light Switch for this one job.

**G-36 Sound (T28.2, needs your yes).** `audio.mute` gets "Toggle" and gives `muted`; new `audio.changeVolume` (louder /
quieter by n %), new check `audio.muted`. One key for the microphone is the most common shortcut automation there is, and
it can't be built today without a script. Reverses REVIEW ("toggles are not repeatable"): on the PC the state can be read
first, so a toggle is as deterministic as a set.

**G-38 Monitor brightness and input (T28.9, spike first).** `display.brightness` and `display.input` (HDMI 1, DisplayPort…)
over DDC/CI for external monitors. Input switching fits the screen flows (hand the monitor to the console). Risk: monitors
differ and some lie about what they support; the spike decides whether it is dependable enough to ship.

**G-50 Ask for text (T28.6, needs your yes).** `prompt.ask` mode "Text" / "Number" and `ask: true` on text inputs. The
prompt shows without taking focus, as now; a click on its box (the person's own action) gives it focus and a plain edit
control. Timeout and default as for the other modes. Reverses "text later" (D-107, D-131). Risk: the first window of ours
that ever takes keyboard focus; over a full-screen game it stays unanswered and uses its default.

**G-60 Check value (T24.3).** `value.check`: a text that takes values, a comparison (is, isn't, contains, doesn't contain,
starts with, ends with, is empty, isn't empty, at least, below) and what to compare with; fails with a sentence when it
doesn't hold, so the failure link is the "else" (D-082 untouched). Branch on Ask's answer, a web status, a script's
output, the battery, the Wi-Fi name. Also runs from `dctl check`.

**G-61 Repeat (T27.6).** `flow.run` gains "Repeat": up to N times (≤ 50), until it works or until it fails, with a wait
between. The graph gains nothing new (D-063's own revisit note asks for "a bounded repeat step, not calls").

**G-63 Step on / off (T25.4).** `enabled: false` on a step: it does nothing and counts as done, what follows runs, the
canvas dims it, "n of m" leaves it out. Values it would give are missing (fallbacks; the editor warns as it does today).

**G-70 Get from text (T25.2).** `text.extract`: from a text that takes values, take a JSON field (`data.items[0].name`), a
pattern's first group, what stands between two texts, or line N; gives `text` and, when it is one, `number`. System.Text.Json
documents and interpreted Regex with a time limit: both AOT-clean. The web step keeps more of the answer when a Get from
text reads it (today 1,000 characters).

**G-71 Value in a number setting (T25.3).** A number box gets "Use a value"; the range is checked when the step runs and
fails with a sentence. Was designed in M23 and not built (step-values/DESIGN.md).

**G-73 Lasting flags (T28.10).** A flag marked "Keep after a restart" is written to a small `flags.json` (D-029's own
revisit note). Off by default: a reboot resetting "gaming mode" is still the right default.

**G-80 Recipes (T27.1).** New flow › "From a recipe": 20+ built-in flows (headphones connect → default device; 23:00 →
screens off; unlock at home → …; mic key; wake the TV and open an app), each with a two-field form for the devices it
needs. Recipes are YAML in the editor, validated by a test against the live catalog so they can't rot.

**G-81 Share a flow (T27.2).** Export = one `.dcflow` file with the flow, the flows it calls and the Library items they
use (never secrets). Import previews it, maps each Library item to one of yours or adds it, lists every script and
program before anything is saved (G-93), and reuses the backup merge code. `dctl export` / `dctl import`.

**G-82 Values in Runs (T25.5).** A finished step's row shows the values it gave and the text it used after filling
(Sensitive ones masked); stored in history with a per-value size cap.

**G-83 Run from this step (T27.4).** Starts the on-screen flow at the selected step; steps before it count as done and
their values are missing (fallbacks, or a device asked for once). For long flows whose first half works.

**G-85 Notes (T27.3).** `note:` on a flow and on a step: shown in the inspector and as a tooltip, found by the list search.

**G-87 Mistyped settings (T27.5).** An unknown key becomes a warning in Problems ("afer isn't a setting. Did you mean
after?") instead of nothing.

**G-90 Report (T27.7).** `dctl doctor` and About › "Save a report": versions, validate output, the last 500 log lines,
device lists, trigger states; the config only if ticked; never secrets.

**G-91 Signing (T28.12).** A code-signing identity (for example Azure Trusted Signing) in `release.yml`, then a winget
manifest. Costs money and needs your identity check, so it is yours to decide; nothing technical blocks it.

**G-95 Inputs from outside (T24.9).** `dctl run <flow> name=value…` and the same after `--run`, checked like a call's
inputs. One flow then serves several desktop shortcuts or deck keys ("Connect: Technics", "Connect: Sony").

**G-96 Scripting output (T27.8).** `--json` on list / status / history / devices / check; `dctl flag <name> [on|off]`;
`dctl pause [on|off]`.

## 5. Rejected, with reasons

- **Hotstrings, key remapping, key sequences, mouse-button triggers (G-13).** They need a resident low-level keyboard hook:
  every key press on the PC would pass through the engine, anti-cheat and antivirus tools dislike it, and a stuck hook
  slows typing system-wide. AutoHotkey and PowerToys Keyboard Manager do this well and the user already runs AutoHotkey.
- **Controller, MIDI and deck-device triggers (G-14).** D-086: built once, removed. A deck key calls `dctl` or a link.
- **Clipboard-changed trigger (G-15).** A flow per copy is a privacy hazard (passwords pass through) for a niche need.
- **Location, NFC, calendar, mail triggers (G-16).** Not PC state; calendar and mail need accounts and tokens.
- **UI automation, OCR, browser, Office, mail, PDF, databases (G-22, G-74).** That is RPA: a different product, several
  times this one's size, built on reflection-heavy libraries the AOT engine can't take. `process.run`, PowerShell and
  `http.request` are the door to those worlds.
- **Mouse macros and a recorder (G-24).** Position-based automation breaks on the first window move; REVIEW §3 said no.
- **File actions (G-26).** REVIEW §3: PowerShell does them and a Files group "invites a file manager". Add three
  examples (copy, move, delete older than N days) to the PowerShell step instead. File *events* are planned (G-07).
- **A web server for webhooks from the network (G-31).** Listening beyond this PC needs a firewall rule (admin, D-008) and
  puts a port on the LAN that can run flows. Outbound MQTT (G-30) and links (G-10) cover the need without a listener.
- **Night light, do not disturb / focus, wallpaper (G-39).** The first two have no public API (an undocumented registry
  blob and private state); a step that breaks on a Windows update is worse than none. Wallpaper: low demand, one script line.
- **Registry, environment variables, printers, screenshots (G-41).** REVIEW §3: "a System group would grow forever".
- **Admin anything (G-42, G-94).** D-008, D-033.
- **Per-app sound routing (G-44).** Undocumented API.
- **Notification-centre toasts, file / date pickers, custom forms (G-52, G-53).** D-012 chose the native pop-up; forms
  are an RPA habit and need focus.
- **if / else blocks, expressions, changing variables, arithmetic (G-64).** D-082 and D-129, both recent and reasoned:
  races between parallel branches, a second validation and rename system. Check value (G-60), Get from text (G-70), Make
  text and flags give the same flows without them. Revisit only with a real flow that can't be drawn.
- **Runtime plugins, a scripting API for other languages.** D-006. PowerShell is the escape hatch.
- **A Stream Deck plugin or Command Palette extension (G-97).** A second codebase in another stack for what one command
  line does. Revisit when someone other than the author asks.
- **Screen-reader work (G-98).** D-091, the user's decision.
- **Other languages (G-99).** Step titles and captions live in C# descriptors and XAML; a resource pass is large and
  doubles the cost of every string change for the agents that write this app. Revisit at a public release, together with
  signing (G-91): the two belong to the same "for strangers" stage.

## 6. Ranking

Scored as value to the people in §2 × fit with GOALS ÷ cost, and ordered so each stage is useful alone.

1. **Check value** (G-60): turns M23's values from decoration into decisions. Small.
2. **Stop flow** (G-02): the guard every event-driven flow needs. Small.
3. **Which device on events** (G-03): "when my headphones connect", not "when anything connects".
4. **Open file or address** (G-20): the most common action in every tool, missing here.
5. **Only run if** (G-01): conditions as a property of the flow, with "why didn't it run".
6. **App events** (G-05): a headline trigger in Keyboard Maestro, Stream Deck and Shortcuts; unlocks per-app shortcuts (G-09).
7. **Recipes** (G-80): the first hour of a new user.
8. **Power, mute toggle, media keys** (G-33, G-36, G-25): everyday glue; two of the three wait for your yes.
9. **Get from text** (G-70) with **a value in a number setting** (G-71): answers from the web and scripts become usable.
10. **Press keys and window steps** (G-23, G-21): the widest expectation across all tools, and the one recorded "No".

Order of work: M24 quick wins (nothing reversed, nothing resident, all Sonnet-sized) → M25 deciding and seeing → M26 more
ways to start → M27 starting faster and sharing → M28 only as you answer its rows.

## 7. Sources

- [S1] AutoHotkey v2 function index: https://www.autohotkey.com/docs/v2/lib/index.htm
- [S2] Power Automate for desktop, actions reference (updated 2026-07): https://learn.microsoft.com/en-us/power-automate/desktop-flows/actions-reference ·
  Workstation: https://learn.microsoft.com/en-us/power-automate/desktop-flows/actions-reference/workstation ·
  System: https://learn.microsoft.com/en-us/power-automate/desktop-flows/actions-reference/system ·
  Message boxes: https://learn.microsoft.com/en-us/power-automate/desktop-flows/actions-reference/display ·
  Mouse and keyboard: https://learn.microsoft.com/en-us/power-automate/desktop-flows/actions-reference/mouseandkeyboard ·
  shortcuts: https://learn.microsoft.com/en-us/power-automate/desktop-flows/run-desktop-flows-via-keyboard-shortcuts
- [S3] Microsoft PowerToys, utilities list (updated 2026-10): https://learn.microsoft.com/en-us/windows/powertoys/
- [S4] Home Assistant: triggers https://www.home-assistant.io/docs/automation/trigger/ · modes
  https://www.home-assistant.io/docs/automation/modes/ · scripts (`enabled`, `continue_on_error`)
  https://www.home-assistant.io/docs/scripts/ · conditions https://www.home-assistant.io/conditions/
- [S5] Elgato Stream Deck guides (Smart Profiles, Hotkey, Multi Actions, Run Command):
  https://www.elgato.com/explorer/products/stream-deck/how-to-be-more-productive-with-stream-deck/ ·
  https://www.elgato.com/explorer/products/stream-deck/how-to-use-stream-deck-for-gaming/
- [S6] Windows Task Scheduler trigger types: https://learn.microsoft.com/en-us/windows/win32/taskschd/trigger-types
- [S7] Keyboard Maestro triggers: https://wiki.keyboardmaestro.com/Triggers
- [S8] Hammerspoon API index: https://www.hammerspoon.org/docs/index.html
- [S9] Apple Shortcuts automation triggers (read through search summaries only, not the pages themselves):
  https://support.apple.com/guide/shortcuts/apde31e9638b/ios · https://support.apple.com/guide/shortcuts/apd932ff833f/ios

Not fetched: IFTTT and Zapier (named from general knowledge), Elgato's own Smart Profiles and Multi Action manuals, the
Home Assistant building-blocks page (404). Also from general knowledge, not from a page read for this document: Home
Assistant's `wake_on_lan`, ping, folder watcher, `repeat` / `for_each` / `wait_for_trigger` and actionable notifications;
Shortcuts' If / Repeat / Ask for Input, command line and "untrusted shortcuts" switch; PowerToys' bug report tool; Azure
Trusted Signing. Counts such as "39 trigger kinds" are of the page as read on 2026-11-13.
