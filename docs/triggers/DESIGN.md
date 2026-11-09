# Triggers and prompts (M20): what is left

Status: BUILT (T20.1-T20.7, D-107). As-built facts live in ARCHITECTURE (Schedules, Event triggers, Prompts), FEATURES F2
and UX §5.4/§5.9; the why is D-107. This file keeps only what is **not built or not verified live** (D-053).

## Not built (later, nobody asked yet)
- Events: `window.opened/closed/focused` (out-of-context `SetWinEventHook`, no injection), `file.changed` (ReadDirectoryChangesW),
  `flow.finished/failed` (`FlowLauncher.RunCompleted`; the chain guard already carries `Chain`). Program start/stop: not planned
  (WMI X-011, ETW needs admin).
- Prompts: `text`/`number` need a run-scoped variable (`$answer-name`, late binding) and keyboard focus, which conflicts with
  "never activates" (D-055). Proposal: show non-activating, take keys only after the person clicks it.
- Waking the PC for a schedule: only via Windows Task Scheduler (rejected for v1, D-107); revisit if wanted.
- Persisted last-run: a due time while the engine was closed is not replayed (by design); a "run at next start" option would need it.
- Footprint split per source (each costs little; measured together in ARCHITECTURE → Footprint).

## Not verified live (needs the user's PC; the PR lists how)
Real USB/Bluetooth plug and unplug, a Wi-Fi switch (with and without `ssid:`), default-sound-device and display changes, lock/unlock
and sleep/resume, AC/battery; a schedule firing at its time and a missed run after sleep; a prompt on screen (no focus steal,
timeout, controller); the editor card with Narrator.
