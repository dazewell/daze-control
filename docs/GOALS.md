# Goals

What the user wants and where the project stands. Keep it current: an agent should be able to read only
this file (plus the Resume block in [PLAN.md](PLAN.md)) and know what to build next. History of *why*
lives in [DECISIONS.md](DECISIONS.md).

## User intent

- Replace `scd` (C:\Projects\Copilot\system-control-daemon, .NET 9 WinForms + WPF tray daemon) with a new
  engine in a different stack: named flows of steps (Bluetooth, audio, displays, Fire TV, Sunshine,
  programs…) fired by global hotkeys, the tray menu or a CLI (user, 2026-10-31).
- Reliability and Windows 11 UX of daze-media-osd: WinUI 3, CsWin32, fail-soft, event-driven, repo-is-memory
  docs. Copy its code patterns; no shared build-time code (user, 2026-10-31; D-001).
- A **free-DAG visual flow editor**: drag-drop pills, parallel group cards, sequence/parallel chaining, steps
  allowed to fail (user, 2026-10-31; D-003).
- The engine costs ~nothing when idle (user; budget in ARCHITECTURE → Footprint, D-009).
- Config in **YAML**; the user's scd.toml is converted once by hand/agent, no importer (user, 2026-10-31; D-004).
- Separate app in `C:\Projects\scd-rewamp`, own repo (user, 2026-10-31).
- ADRs in a file; clean, testable, extensible code (user, 2026-10-31).
- The plan must survive usage caps: an agent always knows where the last one stopped (user, 2026-10-31; D-018).
- Working name **Daze Control** (namespace `DazeControl`, exe `DazeControl.exe`, CLI `dctl.exe`); the user may rename it.

## Must keep from scd

- The user's live flow: **"Connect Technics" = bluetooth:technics → audio:technics on Alt+Z**, plus Bluetooth
  ids for technics and sony (`%APPDATA%\scd\scd.toml`). Gate T4.4: this works on Alt+Z → the user can stop scd.
- Fix scd's failures: one misconfigured module killed startup; unobserved COM exceptions; admin install.

## Non-goals

- Cross-platform. Runtime plugins (D-006). Free node positioning (D-020). Preserving YAML comments on editor save (D-004).

## Status

Milestones and tasks live in [PLAN.md](PLAN.md). Summary:
- [x] M0 Bootstrap · [x] M1 Core model + YAML · [x] M2 Runtime · [x] M3 Engine shell · [x] M4 Providers
- [ ] M5 Run pop-up · [ ] M6 Editor foundation · [ ] M7 Canvas · [ ] M8 Inspector + Library · [ ] M9 Runs · [ ] M10 Ship

## Waiting on the user

- **T4.4 gate, live check (needs your Technics headphones):** copy `samples/user-config.yaml` to
  `%APPDATA%\DazeControl\config.yaml`, start the engine (`artifacts\engine\DazeControl.exe`), turn the
  headphones on but leave them disconnected, press **Alt+Z**. Expected: they connect and become the default output.
  Unit tests and `dctl run connect-technics --dry-run` pass; the live run wasn't possible without the headphones.
  If the connect works but the output switch fails, the endpoint name may not contain "Technics EAH-AZ80"
  (offload endpoints are named "Headphones"/"Technics"): change `library.audio.technics.match`.
  When it works, scd can be stopped.
- **Fire TV, Sunshine, services (optional, untested live):** `firetv.*` steps are tested only with fakes and a
  dry run. To try them, add your TV under `library.firetv` (address, inputs as up/down moves, D-032). A
  `credential:` is a Credential Manager entry named `DazeControl:<name>` (D-031). Most services can only be
  started or stopped by an admin, so `service.*` fails with a message for those (D-033).

## Next step

See the Resume block at the top of [PLAN.md](PLAN.md).
