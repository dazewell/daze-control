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
- After v0.20.0 (user, 2026-11-02): a desktop shortcut per flow (D-049); pick values instead of typing them —
  "Use only these screens" from the PC's screens, and pickers or examples for every free-text value (PowerShell,
  Android, code…) (D-050, D-051); the Bluetooth list must match Windows' own (D-052). Design first, same standards.
- A **flow menu** (user, 2026-11-03): a step pops up a menu "in our OSD format" on top of all windows; each button runs
  another flow, shown with that flow's icon and name; it works with a controller. (A controller button combo that starts a flow was built, then removed 2026-11-04: too
  complex across pads and Steam Input, D-086; map the button to a key in Steam and use it as a shortcut.) Designed first as its own sub-project
  ([flow-menu/DESIGN.md](flow-menu/DESIGN.md), D-053..D-057). Pads: an Xbox controller and the Steam Controller (through
  Steam). The menu never takes focus; polling a pad only while one is on is acceptable (all 2026-11-03).
- Priority after v0.23.0 (user, 2026-11-03), before all other planned work (PLAN M13, designed 2026-11-03,
  D-061..D-070). The editor: right-drag panning, a better layout after wrapped chains (what follows goes on a new
  line under it), flow icons in the Flows list, nav "Flows" returns to the list, a "Run" button on each flow card,
  Tidy up reworked into a Layout menu with more strategies ("make it wider"), a collapsible nav pane. Flows: a step
  that runs another flow (no loops), with device inputs so one chain serves several devices (in P6 itself). Fallback
  steps that weren't needed count as neither progress nor a warning, and a failure its fallback fixed ends "Done"
  (all user answers 2026-11-03).
- **Step values** (user, 2026-11-12): steps give information that later steps use as placeholders, e.g. a Show message
  "Connected to {device}" naming a particular step, composed with clickable pills; maybe a fuller way of working with
  values. Built (M23, D-129..D-131; PLAN → M23); not tried live yet: UX §5.9 M23 is the user's checklist. Open items: [step-values/DESIGN.md](step-values/DESIGN.md).
- **Roadmap from a gap analysis** (user, 2026-11-12): compare with what users of automation tools expect (AutoHotkey, Power
  Automate Desktop, PowerToys, Home Assistant, Stream Deck…), rank the missing features, then build as far as limits allow.
  Done: [roadmap/GAPS.md](roadmap/GAPS.md), PLAN M24 to M28, D-135 (proposed). M28's rows each need the user's yes first.
- Name **Ctrl Alt Daze** (namespace `CtrlAltDaze`, exe `CtrlAltDaze.exe`, CLI `cad.exe`); renamed from Daze Control / `dctl` in M29 (D-152).
- **Rename to Ctrl Alt Daze** (user, 2026-11-13; D-152): everything, down to folders, installer id and the GitHub repo; CLI `cad`.
  Planned in PLAN M29, not started: the user starts the work separately.

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
  with a reason when in use); canvas pills follow the re-layout after same-column links and deletes (X-004); edges can be hovered, clicked,
  marquee-selected, deleted and right-clicked (UX §5.8 #2), with "+"/"×" above the midpoint.
- [ ] M11 Desktop shortcuts, pickers and examples, Bluetooth list (designed 2026-11-02, D-049..D-052; PLAN → M11).
  Desktop shortcuts done (T11.4/T11.5, 0.21.0): Flow settings › Desktop shortcut, the Flows card's menu, `cad shortcut`.
- [x] Canvas: long simple chains wrap onto rows of 5 like text; forks, joins and parallel branches keep their
  columns (user, 2026-11-03; D-060; 0.23.0).
- [ ] M13 Editor and flow updates P1–P9 (designed 2026-11-03, D-061..D-070; PLAN → M13), before everything else.
- [ ] M12 Flow menu, driven by a controller (designed 2026-11-03, D-053..D-057; PLAN → M12). Starts with two spikes:
  controller input at 0 idle CPU (needs your Xbox pad plugged in) and the menu over games without taking focus.
- [ ] M14 Step defaults and conditions (user, 2026-11-04; designed D-080..D-082; PLAN → M14), before the rest of the plan:
  default Try again / Wait / Give up after / If it fails per step type; "Only if…" on steps (Connect skips when already
  connected, "always" forces it) and Check steps (is it connected? …). Small scope: no general if/else.
- [ ] M24 to M27 from the gap analysis (D-135; PLAN → Roadmap): M24 quick wins (Stop flow, Open, Check value, screen and away
  events, media keys, which device on events, Wake over network, Check address, inputs on `cad run`), M25 "Only run if" and
  values in Runs, M26 app / flow / file events and links, M27 recipes and sharing. M28 = questions for the user (power, mute
  toggle, clipboard, keys, windows, ask for text, MQTT, signing).

## Ready for you (2026-11-03)

v0.22.0 is released (GitHub Releases; installed copies update themselves). What's left is yours:

1. **24 h daily-driver soak:** install, copy `samples/user-config.yaml` to `%APPDATA%\CtrlAltDaze\config.yaml`, turn
   on Start with Windows (editor › Settings), and use it for a day. Check `%LOCALAPPDATA%\CtrlAltDaze\logs`
   afterwards. The idle footprint gate already passed (5.4 MB private, 0 ms CPU over 10 min).
2. **Retire scd** once Alt+Z works for a day (T4.4 check below): turn off scd's scheduled task and stop scd.
3. **UX checks that change system-wide settings** (not done by agents on your PC): Settings › Display › Scale at
   100 % and 150 %, Accessibility › Text size 225 % (editor pages, canvas pills and the run pop-up grow, nothing
   clips), and Narrator on the canvas Outline. The rest of the checklist passed at 175 % (PLAN T10.3).
4. **Releases** stay yours: `release.yml` publishes only on a `v<Version>` tag (D-043).
5. **Open follow-ups:** desktop shortcuts on an installed copy (survive an update, uninstall removes them) — you'll
   test them (2026-11-03). The editor can't delete flows yet, so "deleting a flow removes its shortcut on Save" waits
   for that feature. Dropped (2026-11-04, D-091): the pop-up for Narrator (issue #26). Settled by you (2026-11-03): a tray left click brings
   an open editor forward; the live view joining a running flow at its next step is fine for now.

## Waiting on the user

- **T4.4 gate, live check (needs your Technics headphones):** copy `samples/user-config.yaml` to
  `%APPDATA%\CtrlAltDaze\config.yaml`, start the engine (`artifacts\engine\CtrlAltDaze.exe`), turn the
  headphones on but leave them disconnected, press **Alt+Z**. Expected: they connect and become the default output.
  Unit tests and `cad run connect-technics --dry-run` pass; the live run wasn't possible without the headphones.
  If the connect works but the output switch fails, the endpoint name may not contain "Technics EAH-AZ80"
  (offload endpoints are named "Headphones"/"Technics"): change `library.audio.technics.match`.
  When it works, scd can be stopped.
- **Android (Fire TV), Sunshine, services (optional, untested live):** `android.*` steps are tested only with fakes and a
  dry run. To try them, add your TV under `library.android` (address, inputs as up/down moves, D-032). A
  `credential:` is a Credential Manager entry named `CtrlAltDaze:<name>` (D-031). Most services can only be
  started or stopped by an admin, so `service.*` fails with a message for those (D-033).

## Next step

See the Resume block at the top of [PLAN.md](PLAN.md).
