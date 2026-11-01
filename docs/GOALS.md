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
- [x] M0 Bootstrap · [x] M1 Core model + YAML · [x] M2 Runtime · [ ] M3 Engine shell · [ ] M4 Providers
- [ ] M5 Run pop-up · [ ] M6 Editor foundation · [ ] M7 Canvas · [ ] M8 Inspector + Library · [ ] M9 Runs · [ ] M10 Ship

## Next step

See the Resume block at the top of [PLAN.md](PLAN.md).
