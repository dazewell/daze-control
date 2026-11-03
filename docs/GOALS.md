# Goals

What the user wants and where the project stands. Keep it current: an agent should be able to read only
this file (plus the Resume block in [PLAN.md](PLAN.md)) and know what to build next. History of *why*
lives in [DECISIONS.md](DECISIONS.md).

## User intent

- Replace `scd` (C:\Projects\Copilot\system-control-daemon, .NET 9 WinForms + WPF tray daemon) with a new
  engine in a different stack: named flows of steps (Bluetooth, audio, displays, Android (Fire TV), Sunshine,
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
- [x] M5 Run pop-up · [x] M6 Editor foundation · [x] M7 Canvas · [x] M8 Inspector + Library · [x] M9 Runs · [x] M10 Ship
- [x] First-use fixes (user, 2026-11-02; 0.19.0): Flows list stays inside the window when maximized (X-003); Fire TV is
  now Android, old `firetv` names are problems that name the new ones (D-045); Library layouts rename and show their
  screens, Programs get Open file location, "Used by" opens the flows (D-046). Programs stays a derived list (D-041).
- [x] Second-round feedback (user, 2026-11-02; 0.20.0): flow edits are a draft with Save / Cancel, Library and
  Settings keep autosaving (D-047); tray left click opens the editor, right click the menu (D-048); "If it fails,
  run…" and a Connections list in the inspector; a click on a flow's card opens it; Library Remove always shows (off
  with a reason when in use); canvas pills follow the re-layout after same-column links and deletes (X-004).

## Ready for you (M10, 2026-11-02)

Daze Control 0.17.0 is feature-complete for v1: engine, editor, `dctl`, per-user install with in-app updates.
Nothing has been released yet. What's left is yours:

1. **First release (needs your OK):** `release.yml` publishes only when a tag `v<Version>` is pushed (D-043), so a
   merge never ships. To release 0.17.0: `git tag v0.17.0 origin/main` then `git push origin v0.17.0`. Or install
   from the checkout without a release: `pwsh scripts/install.ps1`. Want every merge to release, as the OSD does?
   Say so (D-043 "Revisit if").
2. **24 h daily-driver soak:** install, copy `samples/user-config.yaml` to `%APPDATA%\DazeControl\config.yaml`, turn
   on Start with Windows (editor › Settings), and use it for a day. Check `%LOCALAPPDATA%\DazeControl\logs`
   afterwards. The idle footprint gate already passed (5.4 MB private, 0 ms CPU over 10 min).
3. **Retire scd** once Alt+Z works for a day (T4.4 check below): turn off scd's scheduled task and stop scd.
4. **UX checks that change system-wide settings** (not done by agents on your PC): Settings › Display › Scale at
   100 % and 150 %, Accessibility › Text size 225 % (editor pages, canvas pills and the run pop-up grow, nothing
   clips), and Narrator on the canvas Outline. The rest of the checklist passed at 175 % (PLAN T10.3).
5. **Open follow-ups:** the pop-up isn't read by Narrator (T10.5); the run overlay attaches to a run already going only at its next step.

## Waiting on the user

- **T4.4 gate, live check (needs your Technics headphones):** copy `samples/user-config.yaml` to
  `%APPDATA%\DazeControl\config.yaml`, start the engine (`artifacts\engine\DazeControl.exe`), turn the
  headphones on but leave them disconnected, press **Alt+Z**. Expected: they connect and become the default output.
  Unit tests and `dctl run connect-technics --dry-run` pass; the live run wasn't possible without the headphones.
  If the connect works but the output switch fails, the endpoint name may not contain "Technics EAH-AZ80"
  (offload endpoints are named "Headphones"/"Technics"): change `library.audio.technics.match`.
  When it works, scd can be stopped.
- **Android (Fire TV), Sunshine, services (optional, untested live):** `android.*` steps are tested only with fakes and a
  dry run. To try them, add your TV under `library.android` (address, inputs as up/down moves, D-032). A
  `credential:` is a Credential Manager entry named `DazeControl:<name>` (D-031). Most services can only be
  started or stopped by an admin, so `service.*` fails with a message for those (D-033).

## Next step

See the Resume block at the top of [PLAN.md](PLAN.md).
