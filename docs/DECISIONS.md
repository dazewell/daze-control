# Decisions

Append-only log of choices that shape the code. Newest at the bottom. Format and rules: the
`knowledge-log` skill. A decision is changed by adding a new entry that supersedes it, never by editing
history (the status line is the only thing you update on an old entry). "OSD D-0xx" / "OSD X-0xx" point
into `C:\Projects\daze-media-osd\docs`. D-001..D-021 were seeded from the approved plan (2026-10-31).

## D-001 · 2026-10-31 · Stack: .NET 10; engine Native AOT headless, editor WinUI 3
Status: accepted
- Decision: C# / .NET 10. The resident engine (`DazeControl.exe`) is Native AOT with no UI framework;
  the editor (`DazeControl.Editor.exe`) is WinUI 3 and runs only when opened.
- Why: the engine must cost ~nothing idle (user); AOT + plain Win32 is the smallest .NET resident; WinUI
  gives the editor the Windows 11 look the OSD already has; interop code from the OSD carries over.
- Why not one WinUI process: the OSD measured ~86 MB private resident. Why not Rust: two toolchains and
  no reuse of the OSD's C# interop.
- Revisit if: the T0.5 / T3.7 footprint gate fails with no fix in reach.

## D-002 · 2026-10-31 · Three executables, no launcher or watchdog
Status: accepted
- Decision: engine (resident), editor (on demand), CLI `dctl.exe`. They talk over the pipe (D-011).
- Why: keeps WinUI out of the resident process; the CLI stays tiny and scriptable.
- Why not a launcher/watchdog like the OSD (OSD D-006): nothing system-wide needs restoring if the engine dies.
- Revisit if: the engine changes system state that must be undone on a crash.

## D-003 · 2026-10-31 · Flows are a free DAG with group cards, edge `when` and per-node failure policy
Status: accepted
- Decision: nodes = steps or cards (members start together, no nesting); edges `after` with `when`
  success/failure/always; node policy stop/skip/ignore + retries + timeout. Skips propagate; only
  `always` edges fire from a skipped node. Semantics in FEATURES F1.
- Why: the user wants mind-map freedom, parallel steps and fallbacks ("buds if headphones fail").
- Why not scd's ordered list: no parallelism or fallbacks. Why not series-parallel only: can't express
  joins of unrelated branches or failure edges.
- Revisit if: users never draw anything but chains and cards (then simplify the editor, not the model).

## D-004 · 2026-10-31 · YAML config owned by the editor, hot reload, one-time manual conversion
Status: accepted
- Decision: `%APPDATA%\DazeControl\config.yaml` (`version: 1`). The editor writes it (atomic + backups);
  the engine hot-reloads it and keeps the last good version. The user's scd.toml is converted once by an
  agent (T1.5), no importer code. Comments are lost on editor save; hand edits still load.
- Why: user choice (2026-10-31): readable and hand-editable; one source of truth for engine and editor.
- Why not TOML (scd): awkward for nested graphs. Why not JSON: worse for hand edits. Why not an
  importer: one user, one file.
- Revisit if: comment preservation becomes important to the user.

## D-005 · 2026-10-31 · YAML via YamlDotNet static context (AOT)
Status: accepted (verified in T0.5: YamlDotNet 18.1.0 + Vecc static generator, zero AOT/trim warnings)
- Decision: YamlDotNet with its `StaticContext` source generator for DTOs in `Core/Yaml`.
- Why: mature, widely used; the static context avoids reflection so it works under Native AOT / trimming.
- Why not VYaml: fallback if YamlDotNet's static path emits AOT/trim warnings or misses features.
- Revisit if: T0.5 shows AOT/trim warnings, or the static generator breaks on our DTOs.

## D-006 · 2026-10-31 · Providers are compiled in; no runtime plugins
Status: accepted
- Decision: `IStepProvider` implementations in `DazeControl.Providers`, descriptors (`ParamSpec`) in
  `Core/Catalog`, registered by hand in the engine composition root.
- Why: Native AOT can't load assemblies at runtime; compile-time registration is testable (one
  implementation per descriptor).
- Why not plugin DLLs / scripting hosts: AOT-incompatible, more attack surface. `process`, `script` and
  `http` steps are the escape hatches.
- Revisit if: the engine stops being AOT.

## D-007 · 2026-10-31 · Interop via CsWin32 + `[LibraryImport]` only
Status: accepted
- Decision: Win32/COM through CsWin32 (`NativeMethods.txt`); undocumented interfaces (IPolicyConfig, KS
  properties) via `[LibraryImport]` / source-generated COM.
- Why: AOT/trim-safe, no runtime marshalling, same as the OSD.
- Why not AudioSwitcher / NAudio: 2.5 s startup and unobserved COM exceptions in the user's scd crash log;
  reflection-heavy, not AOT-safe.
- Revisit if: a needed API can't be expressed with source-generated interop.

## D-008 · 2026-10-31 · No admin rights anywhere
Status: accepted
- Decision: Bluetooth connect/disconnect via the KS one-shot reconnect (OSD D-023, X-005); no elevated
  scheduled task; `service.*` steps fail with a clear message when rights are missing.
- Why: scd needed an elevated task (BluetoothSetServiceState); per-user install is simpler and safer.
- Why not keep the elevated task: admin install, UAC, larger attack surface.
- Revisit if: a must-have step can't be done without elevation (then a separate, opt-in helper).

## D-009 · 2026-10-31 · Event-driven waits, zero idle timers, lazy COM workers
Status: accepted
- Decision: no timers or polling while idle; waits use notifications (IMMNotificationClient, device
  state, WM_DISPLAYCHANGE) with a timeout; each COM provider starts its MTA worker on first use and stops
  it after 60 s idle.
- Why: the idle budget (0 ms CPU over 10 min) and scd's 250 ms audio poll.
- Why not polling with backoff: still wakes the CPU; slower to react.
- Revisit if: an API has no notification at all (then poll only during a run, never idle).

## D-010 · 2026-10-31 · Engine-wide resource locks + per-flow `whenRunning`
Status: accepted
- Decision: providers declare resources (`audio-default`, `display-topology`, `bluetooth:<addr>`,
  `adb:<host>`); the runner acquires them in sorted order. Each flow has `whenRunning` ignore/queue/restart,
  applied to every trigger.
- Why: different flows run concurrently (as in scd) but must not fight over one device; sorted
  acquisition avoids deadlocks.
- Why not one global run queue: blocks unrelated flows. Why not scd's static Bluetooth semaphore: ad hoc.
- Revisit if: lock contention shows up in run history.

## D-011 · 2026-10-31 · Named-pipe IPC, current user only, versioned JSON lines
Status: accepted
- Decision: `\\.\pipe\DazeControl-<user SID>`, `PipeOptions.CurrentUserOnly`, newline-delimited
  source-generated JSON, `protocol: 1`, request/response by id plus an event stream.
- Why: local, per-user, no ports or firewall prompts; JSON source-gen is AOT-safe.
- Why not localhost HTTP/gRPC: ports, firewall, heavier. Why not COM/WinRT servers: registration, complexity.
- Revisit if: a non-.NET client needs a schema (then publish one).

## D-012 · 2026-10-31 · Run pop-up drawn natively in the engine
Status: accepted (pending spike T5.1)
- Decision: Win32 window + DWM system backdrop + Direct2D/DirectWrite, no-activate, in the AOT engine.
- Why: keeps WinUI out of the resident process; looks like the OSD.
- Why not WinUI in the engine: memory (D-001). Why not toasts: they go to the notification centre and
  ignore duration (OSD D-014, scd's balloon).
- Revisit if: T5.1 can't get acrylic in AOT → fallback: solid rounded layered window.

## D-013 · 2026-10-31 · Editor canvas is a native WinUI control
Status: accepted
- Decision: XAML nodes on a `Canvas`, edges as `Path`s (one Composition layer), layout computed in Core.
- Why: native look and accessibility, one UI stack.
- Why not WebView2 + React Flow: non-native, +100 MB, second UI stack. No mature WinUI node-editor library.
- Revisit if: XAML element counts make large flows slow (see UX §5.8 #1).

## D-014 · 2026-10-31 · Secrets in Windows Credential Manager, referenced by name
Status: accepted
- Decision: YAML holds only a credential name; the value lives in Credential Manager (`CredRead`).
- Why: secrets never in YAML or logs (hard rule).
- Why not DPAPI-encrypted values in YAML: file churn, still sensitive to copy around.
- Revisit if: credentials must roam between PCs.

## D-015 · 2026-10-31 · Velopack per-user install + updates, HKCU Run autostart
Status: accepted
- Decision: same as the OSD (OSD D-015, D-029..D-031): Velopack Setup.exe into `%LOCALAPPDATA%`, GitHub
  Releases feed, HKCU `Run` value for start with Windows.
- Why: no admin, proven in the OSD.
- Why not MSIX: packaging/identity hassle; why not scd's elevated scheduled task: admin (D-008).
- Revisit if: Velopack can't package the AOT engine + WinUI editor side by side.

## D-016 · 2026-10-31 · Own rolling log + `history.jsonl`
Status: accepted
- Decision: OSD-style rolling text log (7 days, OSD D-028) and a `history.jsonl` of the last 100 runs.
- Why: no logging framework in the AOT engine; history feeds the Runs page and `dctl history`.
- Why not Microsoft.Extensions.Logging / Serilog: size, reflection, not needed.
- Revisit if: structured log querying becomes a need.

## D-017 · 2026-10-31 · Tests: xUnit v3 on Microsoft.Testing.Platform, hand-written fakes, TimeProvider
Status: accepted
- Decision: xUnit v3 + MTP (`global.json` test runner); fakes by hand; time via `TimeProvider`
  (`ManualTime` from the OSD); view models in a WinUI-free project so they're unit-testable.
- Why: same as the OSD; deterministic runtime tests without sleeps.
- Why not Moq/NSubstitute: reflection, little value for small interfaces.
- Revisit if: fakes grow larger than the code they stand in for.

## D-018 · 2026-10-31 · Agent workflow: repo is memory + PLAN.md Resume block + checkpoint commits
Status: accepted
- Decision: docs as in the OSD (GOALS/ARCHITECTURE/DECISIONS/DEADENDS) plus `docs/PLAN.md` with a Resume
  block rewritten in every checkpoint commit; commit + push after every green sub-step; `resume` skill and
  a SessionStart hook that prints the Resume block.
- Why: usage caps cut sessions often (user); a cut session must lose at most one sub-step.
- Why not GOALS "Next step" only: too coarse to resume mid-task.
- Revisit if: the Resume block keeps going stale (then automate it).

## D-019 · 2026-10-31 · Displays: presets and outputs-only first; captured layouts re-resolve LUIDs
Status: accepted (T4.6 spike passed 2026-11-01: a layout captured on the dev PC re-resolved by device path and passed SDC_VALIDATE)
- Decision: `display.topology` (Win+P presets) and `display.outputs` (port scd's CCD code) first;
  `display.layout` captures a CCD layout and re-resolves adapter LUIDs by monitor device path at apply time.
- Why: LUIDs change across reboots; presets and port lists cover the user's current flows.
- Why not scd's external MonitorSwitcher.exe: missing exe killed scd's startup.
- Revisit if: T4.6 shows device paths are unstable too.

## D-020 · 2026-10-31 · Canvas layout is always automatic; no coordinates in YAML
Status: accepted
- Decision: layered left→right layout computed in Core; dragging regroups/reorders, never sets x/y; node
  order within a column = order in `nodes` (designer, 2026-10-31).
- Why: no overlapping messes; coordinates would churn the file.
- Why not free positioning: messy graphs, noisy diffs.
- Revisit if: the user wants to pin nodes freely — then add an optional `pos` per node.

## D-021 · 2026-10-31 · Hot reload keeps the last valid version per flow
Status: accepted
- Decision: on reload, each flow that fails validation keeps its last valid version running (and the editor
  says so); only a never-valid flow is disabled.
- Why: editor autosave writes half-built flows; they must not disable a working predecessor.
- Why not whole-file last-good: one broken flow would freeze edits to all the others.
- Revisit if: users find it confusing that an edited flow still runs its old version.

## D-022 · 2026-10-31 · Engine shell: one message window, CsWin32 without marshalling
Status: accepted
- Decision: the engine's tray icon and hotkeys share one hidden top-level window on the main STA thread
  (`Engine/Shell/MessageWindow`); CsWin32 runs with `allowMarshaling: false` (NativeMethods.json), so the
  WndProc is a static `[UnmanagedCallersOnly]` function pointer and structs are passed by pointer.
- Why: ARCHITECTURE's single message thread; no marshalling stubs or delegate thunks under AOT. T0.5
  measured 3.8 MB private / 15.2 MB working set / 0 ms idle CPU with 3 threads.
- Why not the OSD's thread-per-component windows (OSD TrayIcon, GlobalHotkey): an extra thread and stack each
  for no gain in a headless engine. Why not marshalling CsWin32: delegate WndProc and marshalled structs are
  less certain under AOT.
- Revisit if: a component must block its thread (it then gets its own window/thread, not the shared one).

## D-023 · 2026-10-31 · YAML DTOs hold scalars as strings; the mapper turns bad values into problems
Status: accepted
- Decision: `Core/Yaml` DTOs type every scalar as `string`; `ConfigMapper` parses enums (case-insensitive,
  written camelCase), bools, ints and durations and reports bad ones as `Problem`s with a YAML path and the
  flow id. Only unreadable YAML or a newer `version` reject the whole file (`ConfigLoad.Config == null`).
  Step params stay text (`ParamValue`) and are checked against `ParamSpec` by validation.
- Why: D-021 needs one bad flow to disable only itself; typed DTOs make YamlDotNet throw for the whole file
  on `enabled: yes`.
- Why not typed DTOs + catching the exception: no way to keep the rest of the file. Why not parsing the
  YAML representation model by hand: loses the static context (D-005) and doubles the code.
- Revisit if: YamlDotNet gains per-property error recovery.

## D-024 · 2026-10-31 · An outgoing `when: failure` edge handles the failure
Status: accepted
- Decision: a failed node with at least one outgoing `when: failure` edge doesn't apply its `stop`/`skip`
  policy: the fallback runs and the flow carries on. Its `success` edges still don't fire, so dependents that
  needed success are skipped. A node with no failure edge keeps its policy. A handled failure makes the flow
  PartiallySucceeded ("Done, with a problem").
- Why: user, 2026-10-31 ("Link handles it"): drawing the fallback link is the intent; "buds if headphones
  fail" must work without extra settings.
- Why not "stop still stops": every fallback would also need "Keep going" set by hand, and forgetting it
  makes the fallback dead. Why not making it a validation error: still a manual step for an obvious intent.
- Revisit if: users want a failure branch that runs and then still stops the flow.

## D-025 · 2026-11-01 · Policies live in the pure Scheduler; a may-fail failure fires failure edges too
Status: accepted
- Decision: `Runtime/Scheduling/Scheduler` is a synchronous state machine (Start / Complete(step, result) →
  steps to start) that applies edge `when`, joins, skip propagation, cards and the stop/skip/ignore policies
  (with D-024). Edges fired per outcome: Succeeded → success, always; FailedIgnored → success, failure,
  always; Failed → failure, always; Skipped → always. The async runner (T2.2) only runs steps, retries and
  times out.
- Why: policies decide which edges fire, so they can't sit apart from the graph; a pure core makes every F1
  rule a fast deterministic test. A "may fail" step that fails did fail, so a repair branch on it should run.
- Why not failure edges ignoring FailedIgnored: a "fix it" branch would silently never run on a may-fail step.
- Revisit if: users expect "may fail" to mean "pretend it succeeded" everywhere.

## D-026 · 2026-11-01 · One EngineController in Runtime answers triggers and pipe ops; Runtime references Ipc
Status: accepted
- Decision: hotkeys, the tray and every pipe request go through `Runtime/Control/EngineController`, which finds
  the flow (id, else name), starts it via FlowLauncher and builds the `Ipc` answers. Runtime references Ipc;
  the pipe transport (PipeServer/PipeClient) stays in Ipc.
- Why: one place decides what "run this flow" means for every trigger, tested in Runtime.Tests without a pipe or
  Win32. The CLI and editor reference only Ipc, so they don't carry the runtime.
- Why not the handler in the Engine exe: it has no test project (AOT WinExe). Why not Ipc → Runtime: dctl would
  carry the runner.
- Revisit if: the editor needs runtime types itself (then split protocol records from the transport).

## D-027 · 2026-11-01 · The tray icon is drawn in code
Status: accepted
- Decision: `Engine/Shell/TrayGlyph` paints the idle/busy/paused icons as 32-bit pixels at the small-icon size
  (supersampled) and makes them with `CreateIconIndirect`; no .ico files or Win32 resources.
- Why: the AOT engine ships as one exe; a .res needs rc.exe in the build; the variants come for free. An accent
  square with a play mark reads on dark and light taskbars.
- Why not `IDI_APPLICATION` + a drawn overlay: GDI alpha when drawing an icon into a DIB is unreliable.
- Revisit if: the product gets a designed icon (M10): ship it as a resource then, keep the variants drawn.

## D-028 · 2026-11-01 · One engine per user across sessions: Global\ mutex keyed by the user SID
Status: accepted
- Decision: `SingleInstance` uses `Global\DazeControl.Engine-<user SID>`, taken before `Log.Initialize`.
- Why: the pipe name is per user and named pipes are machine-wide, so two sessions of one user would run two
  engines on one pipe and two writers of history.jsonl and engine.log (PR #4 review). Plain users may create
  `Global\` mutexes (only file mappings need SeCreateGlobalPrivilege).
- Why not: per-session pipe names — clients would need the session id, and history/log would still be shared;
  `Local\` mutex — the bug. Cost accepted: a second interactive session of the same user gets no tray or hotkeys
  (hotkeys are per desktop), and its dctl drives the first session's engine.
- Revisit if: per-session engines are ever wanted (then per-session pipe, history and log paths too).

## D-029 · 2026-11-01 · Flags live in engine memory; `notify` shows a tray notification until M5
Status: accepted
- Decision: `FlagStore` is an in-memory, case-insensitive map shared by all flows; unset = off; an engine
  restart clears it. `notify` hands its text to `INotifier`; the engine shows it as a tray balloon (NIF_INFO)
  until the run pop-up (M5) takes over.
- Why: flags model "mode while I'm at the PC" (gaming, headphones); a reboot resetting them matches that, and
  it needs no file or write path. A balloon is the cheapest visible output with no new window code.
- Why not: persisting flags in config/state file — a write path and stale state after a crash; waiting for M5
  for `notify` — the step would be untestable end to end until then.
- Revisit if: the user wants a flag to survive a restart (then a small `flags.json` next to history).

## D-030 · 2026-11-01 · Bluetooth devices are found by address in their audio endpoints' device paths
Status: accepted
- Decision: `KsBluetoothAudio` lists all non-disabled endpoints; those whose device path properties
  (`{b3f8fa53-0004-438e-9003-51a46e139bfc}` pid 2 or 39) contain the address hex ("B8208EED48FC") belong to the
  device, widened to every endpoint with the same `PKEY_Device_ContainerId` (not the local-machine one). Connect
  sends `KSPROPERTY_ONESHOT_RECONNECT` to its inactive endpoints, disconnect `…_DISCONNECT` to its active ones,
  then waits on IMMNotificationClient until an endpoint turns active (or none is). Already in that state = success.
- Why: the library stores the address (scd's ids), and names change (offload endpoints are "Headphones",
  "Technics"). Pid 39 holds the BTHENUM path on Intel offload endpoints, pid 2 on classic ones (dev PC, 26300).
- Why not match by friendly name: ambiguous and renamed by Windows. Why not Windows.Devices.Bluetooth / CfgMgr to
  map address → container: more interop for the same answer; no profile connect API there anyway (OSD D-023).
- Revisit if: a driver puts the address in neither property (then map the address to a container via CfgMgr).

## D-031 · 2026-11-01 · Credentials are generic Credential Manager entries named `DazeControl:<name>`
Status: accepted
- Decision: `http.request`'s `credential: sunshine` reads the generic credential `DazeControl:sunshine`
  (`CredRead`); with a user name it becomes Basic auth, without one the secret is a Bearer token. Users save it
  with `cmdkey /generic:DazeControl:sunshine /user:admin /pass` (the editor writes it later). Errors name the
  host and status code only (never the secret or the server's reason text). TLS is checked as usual; no "trust any
  certificate" switch. A sign-in goes only over https:// or to loopback. No cookies, no redirects (one request).
- Why: the prefix keeps a flow from reaching the PC's other saved passwords; cmdkey needs no admin and no UI.
- Why not raw target names: any config could send any saved credential to any URL. Why not a skip-TLS
  option now: nothing in the user's config needs it yet (scd's sunshine section was unused).
- Revisit if: a self-signed local API (Sunshine on :47990) must be called — then pin its certificate, not skip checks.

## D-032 · 2026-11-01 · Fire TV: adb.exe per step, one chained shell call, `library.firetv`
Status: accepted
- Decision: `library.firetv.<key>` = `address` (port 5555 if none), optional `adb` path (else PATH), `inputs`
  (name → up/down moves). Each step runs `adb connect` (3 tries, 1 s apart; success only when the answer says
  "connected to", since adb exits 0 on failure), then one `adb -s <serial> shell "a && b && c"` (scd's
  sequence for inputs: WAKEUP, TV_INPUT, moves, DPAD_CENTER). Keys must be `[A-Za-z0-9_]` (they go into a shell
  line); `home` → `KEYCODE_HOME`. Lock `adb:<address>`. `dctl devices adb` = `adb devices`.
- Why: a port of what scd did and the user relies on; nothing resident between steps (D-009).
- Why not an adb protocol client in-process: RSA auth + framing to maintain for three key presses.
- Revisit if: adb's start-up (~1 s cold daemon) makes the TV flow feel slow.

## D-033 · 2026-11-01 · Services through the SCM without admin; no elevation path
Status: accepted
- Decision: `service.start`/`stop` read the state first (query rights only); a pending start/stop is waited out;
  already in that state = success; only then open with start/stop rights, request it and poll every 250 ms
  until there (the step timeout ends it). Lock `service:<NAME>` upper-cased (SCM names ignore case).
  Access denied fails at once: "Windows only lets an administrator start …".
- Why: D-008 (no admin). Most services refuse non-admin start/stop; a few (and ones whose ACL the user changed,
  e.g. with `sc sdset`) allow it, and those work.
- Why not an elevated helper or scheduled task like scd: an admin path is exactly what the rewrite dropped.
  Why not NotifyServiceStatusChange: needs an alertable thread; a run lasts seconds, idle stays at zero.
- Revisit if: the user needs a service only an admin can control (Sunshine) — then an opt-in scheduled task.

