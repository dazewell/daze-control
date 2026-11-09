# Triggers and prompts: scheduled, event-based, input prompts (M20)

Status: ACCEPTED 2026-11-08 (D-107): §7 answered: engine timer; events = session, power, device/audio/display, network; prompts = Yes/No + choose; missed runs chosen per schedule at creation (`ifMissed: run|skip`). Same rules as the other sub-projects: design first,
one PR per task (D-079), cut this file into ARCHITECTURE/FEATURES/UX at the gate.

## 1. Goal and what already exists
A flow can start from a hotkey, tray, menu, desktop shortcut, `dctl`, or another flow (`TriggerRouter`,
`FlowLauncher.TriggerAsync`, one slot per flow, `whenRunning`, chain guard). New: start it **on a schedule**, **on a system
event**, and let a flow **ask the person something** (at start or mid-run). Hard limits that shape everything: idle = zero
(D-009), no admin (D-008), no focus stealing, Native AOT, fail-soft (a bad trigger disables only itself, D-021), no secrets in YAML.

One YAML shape for all of it (the hotkey stays as is, so no migration):
```yaml
flows:
  evening-lights:
    triggers:
      - { at: "22:30", days: weekdays }            # schedule
      - { every: 2h, between: ["09:00", "18:00"] }  # interval
      - { on: session.unlock, only: onBattery }     # event; `only` reuses the M14 checks
```
History shows the trigger text ("Schedule · 22:30", "Event · PC unlocked"), like "Desktop shortcut" today.

## 2. Scheduled
Forms: `at` time + `days` (daily, weekdays, weekends, a list), `every` interval (+ optional window), `once` date-time,
`on: engine.start` (logon). Pure `ScheduleTable.Next(rules, now, TimeProvider)`: tested with `ManualTime`, DST gaps and
overlaps, month ends. Local time.

Mechanism options:
- **A. In the engine (recommended).** One kernel waitable timer armed to the *next due time* of all rules; between due times
  the engine does nothing (0 ms CPU). Re-armed on config reload, `WM_POWERBROADCAST` resume and `WM_TIMECHANGE`. D-009's wording
  needs amending: "no periodic timers; one due-time timer is allowed". The engine already starts at logon (Autostart).
- B. Windows Task Scheduler task per rule running `dctl run`. Fires with the engine closed and can wake the PC, but leaves OS
  objects to clean on uninstall/restore, per-rule registration is its own failure mode, and history, Pause and `whenRunning`
  must be rebuilt around an outside launcher. Rejected for v1 (revisit if "wake the PC to run" is wanted).

Missed runs (PC asleep or engine closed at the due time): run once if late by less than a grace (default 10 min, a setting),
else skip and log "Skipped: the PC was asleep". Never replay several missed intervals. Pause (tray) silences schedules too.

## 3. Event-based
Each source is lazy like `GamepadSource`: loaded only while a flow subscribes, unloaded after the last one goes; each has a
measured footprint row (`measure.ps1`). All are push notifications, no polling:

| Event (`on:`) | Backend | Admin | v1 |
|---|---|---|---|
| `session.lock/unlock/logon/logoff` | WTS session notifications | no | yes |
| `power.resume`, `power.ac/battery`, lid | WM_POWERBROADCAST, power-setting notify | no | yes |
| `device.added/removed` (USB, Bluetooth connected/disconnected) | `DeviceArrival` (M12), BT device watcher | no | yes |
| `audio.default-changed`, `display.changed` | IMMNotificationClient, WM_DISPLAYCHANGE (already used by waits) | no | yes |
| `network.connected(ssid?)/disconnected` | NotifyNetworkConnectivityChange / IP-interface notify | no | yes |
| `window.opened/closed/focused(app)` | out-of-context `SetWinEventHook`, no injection | no | v2 |
| `file.changed(path)` | ReadDirectoryChangesW | no | v2 |
| `flow.finished/failed(flow)` | `FlowLauncher.RunCompleted` | no | v2 |
| program started/stopped | nothing without WMI (X-011) or ETW (admin) | - | not planned |

Guards on every event trigger: debounce (default 2 s, a burst counts once), per-trigger cooldown, `whenRunning` per flow, the
existing chain guard (a flow started by `flow.finished` carries the chain, so no loops), and `only:` conditions (M14), so
"on unlock, only if on battery" needs no new concept. An event arriving while paused is dropped, not queued.

## 4. Input prompts
Two layers, both on the no-activate pop-up (`FlowMenu`/`RunPopup`, D-055), so a prompt never steals focus:
1. **Ask at start.** A flow input (D-064) gets `ask: true`: before the run, a prompt offers the Library items of that kind
   ("Which headphones?"). The answer fills `$name` exactly as today, so nothing downstream changes.
2. **`prompt.ask` step** (mid-flow): `confirm` (Yes/No become the success/failure edges like a check step, so "No" can stop or
   branch) and `choose` (2-12 labels via the flow menu; a controller works). `text`/`number` later: they need a run-scoped
   variable (`$answer-name`, late binding) and keyboard focus.
Every prompt has a **timeout and a default** (`onTimeout: skip|stop|use-default`), because scheduled and event runs happen when
nobody is there; for those starts the default is `stop` with a "No answer" note, so a flow slot is never blocked forever.
**Built (T20.5):** `FlowInput.Ask/Timeout/OnTimeout` (YAML `ask`, `timeout` 5s-10m, `onTimeout: stop|use-default`) asked by
`InputPrompts` in `FlowLauncher` before the run (inside the slot, so Stop/restart closes the prompt; only inputs a step still
uses and with 2+ Library items; a `flow.run` that passes the input skips it; nested waits and dry runs take defaults).
`prompt.ask` (`PromptProvider`, `mode: confirm|choose`, `question`, `choices`, `flag`, `timeout`, `onTimeout: stop|use-default|skip`,
`default`) runs on the same `IMenuPresenter` seam: No fails the step, a choice sets flags `name:answer`. Unset `onTimeout`:
`stop` for steps, default device for ask inputs a person started, `stop` for a "Schedule ·"/"Event ·" trigger
(`Prompts.IsNonInteractive`); stop = quiet run, "No answer" on the first step in history. The close timer is the menu's
(time with no input). Text/number conflict with "never activates": proposal = show non-activating, take keys only after the person clicks it (§7.3).
Windows toast buttons: rejected (packaging identity, footprint).

## 5. Editor
Flow inspector gets a "When this runs" card (Hotkey, Schedule and Event rows, a plain sentence: "Weekdays at 22:30",
"When the PC is unlocked"), a next-run line ("Next: today 22:30") and "Test this trigger" (fire now). Palette: Ask (Yes/No,
Choose). The Flows list shows a clock/bolt chip. Strings never say trigger/cron/node (UX).
**Built (T20.6):** `WhenThisRuns` (sentences, edits, event list from `TriggerText.Events` + engine start), `InspectorViewModel.Triggers`, Flows chips, ask-input rows; Ask step uses the catalog fields. See UX §5.4.

## 6. Tasks
PLAN M20: Core model + `ScheduleTable` -> schedule runtime -> event framework + session/power -> device/audio/display/network
sources; prompts in parallel; editor card; gate (footprint rows, docs).

## 7. Open questions for the user
1. Schedule mechanism: in-engine due-time timer (A, recommended) or Task Scheduler (B, can wake the PC)?
2. Which event sources in v1 (table above); is "program started/stopped" needed enough to accept a cost?
3. Text/number prompts: in v1 with click-to-type, or only confirm/choose first?
4. Missed-run grace (10 min) and whether schedules may wake the PC from sleep.
