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
- Decision: no periodic timers or polling while idle (D-107 allows one due-time timer for schedules); waits use notifications (IMMNotificationClient, device
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
Status: accepted (T5.1 spike passed 2026-11-01 on 26300: real acrylic, no fallback needed; how → D-034)
- Decision: Win32 window + DWM system backdrop + Direct2D/DirectWrite, no-activate, in the AOT engine.
- Why: keeps WinUI out of the resident process; looks like the OSD.
- Why not WinUI in the engine: memory (D-001). Why not toasts: they go to the notification centre and
  ignore duration (OSD D-014, scd's balloon).
- Revisit if: T5.1 can't get acrylic in AOT → fallback: solid rounded layered window.

## D-013 · 2026-10-31 · Editor canvas is a native WinUI control
Status: accepted
- Decision: XAML nodes on a `Canvas`, edges as `Path`s (one layer, batched by style: D-037), layout computed in Core.
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
Status: accepted; "always automatic" superseded by D-115 (steps can be placed by hand; still no coordinates in YAML)
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
Status: accepted; its last sentence (a handled failure → PartiallySucceeded) superseded by D-065
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
Status: accepted; mark changed by D-044
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

## D-034 · 2026-11-01 · Pop-up: transient-window backdrop on a layered WS_POPUP, D2D HWND target
Status: accepted
- Decision: `Engine/Shell/RunPopup`: `WS_POPUP` + `WS_EX_NOACTIVATE|TOOLWINDOW|TOPMOST|LAYERED` (+`TRANSPARENT`
  unless the run failed); `DwmExtendFrameIntoClientArea(-1)`, `DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW`,
  `DWMWCP_ROUND`, `DWMWA_USE_IMMERSIVE_DARK_MODE` re-applied per show; **software** `ID2D1HwndRenderTarget` (BGRA
  premultiplied, cleared to transparent, grayscale text AA); layered alpha is the 200/150 ms fade. CsWin32 COM
  structs, AOT-clean. On hide: layouts, target, factories released and d2d1/DWrite (loaded by `NativeLibrary`, not
  P/Invoke) freed; the window stays.
- Why: verified over a striped window in light and dark: blurred acrylic, DWM shadow and border, click-through
  on success, a click on a failure fires without changing the foreground window.
- Footprint (T5.2, after 3 runs, 30 s settle): hardware target +13.5 MB private, WS 44.9 MB, 16 ms idle CPU (GPU
  driver stays loaded); software target +3.8; plus unloading d2d1/DWrite +3.1 (WS 24.4, 0 ms) vs +0.7 for the
  same flow with `popup: false`. `HeapCompact` after hide: no change.
- Why not the D-012 fallback (solid layered window): not needed. Why not `UpdateLayeredWindow` + own blur: no
  system acrylic. Why not DirectComposition: more code for the same result.
- Revisit if: a Windows build stops drawing the backdrop on layered windows (then drop `WS_EX_LAYERED`, lose
  click-through and fade), or the footprint after a pop-up breaks the budget.

## D-035 · 2026-11-01 · Long outcomes go to row 3, not the 44-DIP status column
Status: accepted
- Decision: the status column holds only "2 of 3" and "Done"; "Done, with a problem", "Didn't finish" and
  "Stopped" become row 3's first line in the tone colour (semibold), above the reason (UX §5.6 updated).
- Why: in the column they squeezed the flow name to "Audio to headpho…" (screenshot, T5.2).
- Revisit if: the editor's run view (M9) settles on another pattern; keep the two alike.

## D-036 · 2026-11-01 · Accept the pop-up's one-time ~+2.5 MB; keep Direct2D
Status: accepted (user, 2026-11-01: "Accept, merge")
- Decision: the post-run budget line allows the pop-up's first showing (+4.5 MB after a run once it has shown,
  instead of +2); the pop-up keeps software Direct2D/DirectWrite (D-034).
- Why: the cost is paid once and stays flat (3 and 10 runs: +3.1 / +3.5 MB vs +0.7 without the pop-up), absolute
  numbers stay well inside 15 MB / 35 MB / 0 ms (8.8 / 24.9 / 0).
- Why not a GDI rewrite: more code (own alpha handling, no antialiased ring) to save ~2.5 MB.
- Revisit if: the post-pop-up footprint grows with runs, or another feature needs that headroom.
- Amended 2026-11-04 (user, T12.7): the post-run line is **+6.5 MB** once the flow menu has shown (measured +6.3 after 10 menus,
  flat). Why: the menu's software-drawn surface is ~700×700 px at 175 % against the pop-up's ~600×180. Absolute budgets still
  hold (12.6 / 27.5 MB). Revisit if it grows with menus.

## D-037 · 2026-11-01 · Canvas edges are a few XAML paths batched by style; pills are built in code
Status: accepted
- Decision: all edges go into one `Canvas` layer of eight `Path`s (one per style: normal, into may-fail, if it
  fails, always, selected, plus three arrowhead fills), each holding every edge of that style as Bézier figures.
  Pills and cards are `Grid`s built in code (pill 10 elements, card 8), theme brushes re-read on a theme change.
- Why: constant element count however many edges (the point of UX §5.8 #1), dashes and theme brushes for free;
  code-built views keep the element budget visible and need no template machinery.
- Why not a Composition `ShapeVisual` per §5.8: Bézier `CompositionPath` needs an `IGeometrySource2D`, i.e. Win2D
  or hand-written D2D geometry interop — a dependency or a lot of interop for no visible gain.
- Revisit if: a 100-edge flow redraws slowly (then move the paths to a `ShapeVisual` with Win2D geometry).

## D-038 · 2026-11-02 · Canvas card edits refuse rather than change when a step runs
Status: accepted
- Decision: group, ungroup and take-out refuse (with a tip) when the result would run differently: different
  conditions on one source, an "if it fails" edge on a grouped step or on a card losing a step, or steps with
  different failure policies. A step leaving a card takes the card's policy unless it may fail itself; a new card
  takes its steps' shared policy. Mirrors `Scheduler.CompleteMemberOf` (in a card only a step's own may-fail counts).
- Why: a drag must not silently change what a flow does; PR #11 review found three such cases.
- Why not rewrite edges to keep the meaning: "any member failed" has no per-step edge form, and silent policy
  rewrites would surprise more than a refusal.
- Revisit if: the refusals block common gestures (dropping a plain step on a may-fail card is refused until the
  M8 inspector can change policies).

## D-039 · 2026-11-02 · Canvas delete bridges, paste runs after the selection, undo is per-flow snapshots
Status: accepted
- Decision: Del removes steps (a card with its members) and makes their dependents wait for what they waited
  for, under the deleted edge's condition; an "only if it fails" edge goes with its step. Ctrl+V pastes after the
  selection (the next column); nothing selected or Ctrl+D keeps edges from steps that still exist, so copies land
  beside the originals. The clipboard is in-process (`CanvasClipboard`, one per window), so library refs stay keys.
  Undo is a stack of whole-`Flow` snapshots in `FlowCanvasViewModel` (100 deep), one per `Edit` call; a change from
  outside the canvas (reload, another page) clears it.
- Why: layout is automatic (D-020), so "a new column to the right" (UX §5.2) can only mean "runs after
  something"; the selection is the natural anchor. Snapshots are tiny records, so per-command inverses buy nothing.
- Why not the Windows clipboard (YAML text): cross-config paste needs library merging and id clashes handled; no
  one asked for it yet. Why not bridge failure edges: "if B fails" can't become a condition on A.
- Revisit if: users paste between config files, or undo across page switches is wanted.

## D-043 · 2026-11-02 · Ship as one Velopack package; dctl checks for updates, the engine applies; release on tag only
Status: accepted
Refines: D-015
- Decision: `scripts/pack.ps1` publishes the editor (self-contained) then the AOT engine and dctl into one folder and
  packs it (packId `DazeControlApp`, mainExe `DazeControl.exe`, Start-menu shortcut). The engine runs Velopack's hooks
  first: before-update closes a running engine with WM_CLOSE and waits for its mutex; before-uninstall removes our Run
  value if it points into the install. At start (after the single-instance check) it hands a downloaded update to the
  updater with its own args and exits; skipped when `VELOPACK_RESTART` is set. Checks run in `dctl update` (find +
  download, never apply), which the installed engine starts hidden 1 min after start and every 24 h. The editor's
  Settings › Updates card checks on demand and offers Restart now. `release.yml` runs only on a pushed `v<Version>` tag.
  CI runs `pack.ps1` on every PR. `DAZECONTROL_UPDATE_SOURCE` points at a local feed folder for testing.
- Why: one install, one Apps entry, three exes side by side (the engine starts the editor and dctl from its folder).
  Checking in a child process keeps the HTTP/TLS stack out of the resident engine (footprint budget, D-009); applying
  at engine start is the OSD's apply-on-next-launch (OSD D-030) for an app that has no launcher. Tag-only: the user
  authorized merges, not releases (2026-11-02); a release is outward-facing.
- Why not the OSD's release on merge (OSD D-031): merging any feat PR would publish. Why not checks in the engine:
  sockets + SChannel stay loaded after the first check. Why not checks only in the editor: it runs rarely.
- Accepted: uninstall force-kills the engine before any hook (velopack log, 2026-11-02), so its tray icon lingers until
  hovered. Restart now from the editor starts the engine on the default config. Unsigned (SmartScreen warns).
- Revisit if: the user wants release on merge (add `branches: [main]` and the OSD's `check` job), or a migration
  needs `OnAfterUpdateFastCallback`.

## D-042 · 2026-11-02 · Dry run walks the editor's on-screen flow; runs record when each step started
Status: accepted
- Decision: the editor's Dry run sends `run` with `dryRun` and `config` = library + the flow as shown (like D-040).
  The engine walks it with the dry runner if its only errors are step values (`…nodes.<id>.with…`); any other error
  (a loop, a bad timeout) refuses with "Fix this first: …". Dry steps do nothing, except that a step whose device
  isn't in the library (or is unset) fails, so the canvas shows what a missing ref would fail and skip.
  `NodeResult` gains `Start` (from the run's start), `Order` (start order, numbered on the scheduler's thread so
  parallel steps get a stable number) and `Retries` (each later try's start) for the start-order numbers and the
  Runs timeline. Old history lines read with zeros.
- Why: the dry run is a preview of what's on screen, and a missing device is the most common thing to preview;
  numbering in `Launch` makes the order deterministic where timestamps of parallel steps tie.
- Why not the saved flow by id: with a missing ref the flow isn't valid, so the engine would walk its last valid
  version (D-021), not what's shown. Why not a dry run inside the editor: Editor.ViewModels doesn't reference
  Runtime (the scheduler), and the engine's walk is the one that counts.

## D-041 · 2026-11-02 · Library Test only looks; Programs come from the flows
Status: accepted
- Decision: the Library page's Test asks the engine for the live list (pipe `devices`) and says whether the item is
  there and connected; a layout's test checks every monitor device path is plugged in (the `displays` list now
  carries it as `Id`); a program's test looks for the file (a bare name on PATH, as Windows would). It never runs a
  step. Programs is not a Library kind: the page lists the `process.run` paths the flows use. Remove is offered only
  for items no flow uses.
- Why: a test from the Library has no step to borrow settings from, and connecting, switching audio or launching a
  program from a settings page would change the user's PC as a side effect of looking (the "Test this step" button
  in the inspector is where a real run belongs, D-040). Listing programs needs no new YAML.
- Why not a real connect/disconnect pair: it would leave a device in a different state when the second half fails.
  Why not a `library.programs` kind: steps would need a second way to name a program for no gain yet.
- Revisit if: users want to name programs once (arguments, working folder) — then add a kind and a LibraryRef param.

## D-040 · 2026-11-02 · "Test this step" sends the editor's config text; the engine runs that one step alone
Status: accepted
- Decision: the inspector sends `test-step` with the flow id, the step id and `config` = YAML of the library plus
  that flow as on screen (unsaved values included). The engine maps it, validates just that step, and runs it as a
  one-node flow `test:<flow>` (no edges, stop on failure) on a second `FlowRunner` sharing the providers and
  `ResourceLocks` but raising no events: no pop-up, no history. The editor sends it on a pipe connection of its own;
  Cancel closes that connection, which cancels the request (`cancel test:<flow>` works too, for `dctl`).
- Why: "with on-screen values" (UX §5.4) without waiting for autosave + the engine's reload; the shared locks make a
  test wait for a running flow that uses the same device instead of fighting it.
- Why not by flow/node id against the engine's config: races the 300 ms save and the reload, and tests the last
  *valid* version of a half-built flow (D-021) rather than what's shown. Why not the run connection's queue: a
  20 s Bluetooth test would hold every other request behind it.
- Revisit if: a config grows past the pipe's 64 KB line limit (big display layouts) — then send only the step and
  the library items it references.

## D-044 · 2026-11-02 · App icon: a fork-and-join flow on the OSD's violet→blue tile
Status: accepted
- Decision: `scripts/make-icon.ps1` draws `assets/app.ico` + `app.png`: one step fanning out to two that join
  again (horizontal Béziers, as on the canvas), white on the OSD's gradient tile, the last step amber. Every exe
  gets it via `ApplicationIcon` (SDK-built resource, no rc.exe), the editor window and About card from
  `Assets\`, the installer via `vpk --icon`. The tray keeps D-027's drawn variants, now with the same mark.
- Why: the user wanted an icon before the first release; the OSD's tile makes the two apps read as a family,
  and the mark is what the app does (a flow with parallel steps).
- Why not one step fanning out to two: it is the stock "share" glyph. Why not the old play mark: says "media".
- Revisit if: the mark is unreadable at 16 px on a real taskbar (then drop the edges at ≤ 20 px).

## D-045 · 2026-11-02 · Fire TV becomes Android, with no old-name compatibility
Status: accepted
- Decision: the steps are `android.input`/`android.keys`/`android.sleep`, the library section `android:`, the code
  `Android*` in `Providers/Android`, the Library tab "Android". `firetv:` and `firetv.*` are not read: the section
  is a file-wide problem naming `android:` and is dropped, a `firetv.x` step is a flow problem naming `android.x`.
- Why: the steps only send basic adb commands (connect, `input keyevent`), so they work on any Android device; the
  old name hid that (user, 2026-11-02). No one has a `firetv` entry yet (the converted scd config had none).
- Why not accept both names: a second spelling forever, in the mapper, validator and every doc, for no user. Why not
  rewrite old names on load: a silent edit of the user's file.
- Revisit if: someone does lose entries to it — then add a one-time rewrite with a backup.

## D-046 · 2026-11-02 · Library rename follows the flows; layout details come from the saved capture
Status: accepted
- Decision: layouts and Android devices get Rename: `LibraryEdits.Rename` changes the key and every step setting
  that named it, in one edit (one undo, one save). Audio and Bluetooth don't: their cards show the device's name,
  not the key. A layout's Details read its CCD bytes (`LayoutScreens.Read`): cable type, resolution, position,
  refresh, primary = at (0, 0); a plugged-in screen takes its name and port from the live list, another shows the
  maker/model code from its device path. Programs stay derived (D-041) and add Open file location (File Explorer
  with the resolved file selected) and the same "Used by" list, which opens a flow in the editor.
- Why: the user captured layouts named "layout", "layout-2" and couldn't tell or rename them (first-use report);
  renaming without the refs would break the flows that restore them.
- Why not ask the engine for details: the capture already holds every mode, and a screen that is unplugged now must
  still be described. Why not store names at capture time: one more YAML field for what the device path already says.
- Revisit if: the CCD struct layout changes (the Providers test fails), or users want audio/Bluetooth keys shown.

## D-047 · 2026-11-02 · Flow edits are a draft until Save; Library and Settings keep autosaving
Status: accepted (supersedes UX §5.1/§5.2 "autosave debounced 300 ms" for flow edits)
- Decision: `ConfigSession` keeps the saved configuration beside `Current`. Canvas and inspector edits to the open
  flow go through `UpdateDraft`: shown everywhere (canvas, Problems, Dry run, Test this step), never written.
  Save (Ctrl+S) writes the draft at once; Cancel / "Don't save" puts the saved flow back; leaving the flow or
  closing the window with a draft asks Save / Don't save / Cancel. Undo stays inside the draft (Save and Cancel
  clear it; undoing back to the saved flow leaves nothing to save). `Update` (Library, Settings, the Flows list's
  On switch, New flow) applies to both versions and still autosaves after 300 ms, so a Library rename reaches the
  draft and the file alike. A reload from disk keeps the draft on top of the new file.
- Why: the user wants to experiment without fear of breaking a working flow (2026-11-02). Library items and
  settings are single values with no half-built state, and their pages have no place for a Save bar.
- Why not autosave plus "Revert to saved": the engine reloads every 300 ms save, so a half-built flow would run
  (or keep its last valid version, D-021) mid-experiment. Why not a draft file next to config.yaml: one more file
  to reconcile, and nothing outside the editor needs a draft.
- Effect on D-021: the editor no longer writes half-built flows by itself, but a user can still save one, so the
  engine keeps the last valid version per flow. D-040's "on-screen values" now means the draft.
- Revisit if: users lose drafts to a crash (then persist the draft), or want Save on the Library too.

## D-048 · 2026-11-02 · Tray: left click opens the editor, right click the menu
Status: accepted
- Decision: NIN_SELECT/NIN_KEYSELECT (left click, Enter/Space) start the editor or bring it forward
  (`AllowSetForegroundWindow(ASFW_ANY)` first, so its running instance may take the foreground); WM_CONTEXTMENU
  (right click, Shift+F10/Apps) shows the menu. Without an installed editor every gesture shows the menu.
- Why: the user expects the shell's convention, where the left click opens the app (2026-11-02).
- Why not keep the menu on both: the editor is two clicks away for the most common thing.
- Revisit if: the engine gains a quick panel worth a left click.

## D-049 · 2026-11-02 · Desktop shortcut per flow: a .lnk to the engine with `--run <flow id>`, made by the engine
Status: accepted (M11)
- Decision: the shortcut targets the installed Velopack stub (`%LOCALAPPDATA%\DazeControlApp\Daze Control.exe`, which
  survives updates and forwards args; the running exe outside an install) with `--run <flow id>`, plus the quoted
absolute config path when the engine runs a non-default config (as `Autostart.Enable` does), named after the flow,
  icon = the flow's glyph on the app tile, rendered by the engine (D-012's DirectWrite path) into
  `%LOCALAPPDATA%\DazeControl\shortcuts\<safe id>.ico`; file names (.ico and .lnk) are made filename-safe (reserved
  characters and `/` replaced, reserved device names avoided, plus a short hash of the id when anything changed),
  while the .lnk's arguments keep the real id. The engine writes it (IShellLinkW + IPersistFile, Desktop via
  `SHGetKnownFolderPath`, so a OneDrive desktop works). No YAML state: pipe op `shortcuts` scans the Desktop for .lnk
  files with our target and `--run`. A second engine started by the shortcut forwards `run` (trigger "Desktop
  shortcut") over the pipe and exits; a first one starts as usual and runs the flow after the first config load.
  The id, not the name, is the link: renaming a flow keeps the shortcut working (the editor offers "Update"). A
  desktop run resolves by id only, cold start and forwarded alike (the pipe `run` gets an `exactId` flag), so a
  deleted flow's shortcut never runs another flow whose name equals that id; `dctl run` keeps its name fallback. Pause
  shortcuts doesn't apply (a double-click is deliberate, like the tray). The uninstall hook removes our shortcuts and
  icons.
- Why: one double-click per flow, also when the engine isn't running (user, 2026-11-02).
- Why not `dctl.exe run` as the target: a console window flashes. Why not `shortcut: true` in YAML kept in sync by the
  engine: it would rewrite the desktop on every reload and bring back a shortcut the user deleted. Why not the editor
  writing it: `dctl shortcut` would need a second copy, and the glyph renderer lives in the engine.
- Built (T11.4/T11.5, 0.21.0): the scan also matches the config (a link without one = the default config), so each
  engine lists only its own; a name taken by a file that isn't this flow's shortcut gets " (2)" and up, never an
  overwrite; the icon is the flow's glyph, else its first step's (`FlowGlyph.CodeOf`, as the flow settings show it);
  a desktop run that can't start (flow gone, off, needs fixing) shows a "Didn't start" pop-up, since nobody reads the
  pipe's answer. .ico entries are 32-bit BMP (no PNG encoder in the AOT engine; ~350 KB per flow). The engine is one
  per user, not per config: a forwarded run carries the shortcut's config path and an engine running another config
  refuses it (pop-up) rather than run its own flow of that id. Icons are named by id + a hash of config and exact id.
- Revisit if: Start menu or taskbar pins are wanted (same .lnk in the Programs folder; a pin needs an AUMID per flow).

## D-050 · 2026-11-02 · Suggestions and examples are catalog metadata (`ParamSpec.Suggest`, `ParamSpec.Examples`)
Status: accepted (M11)
- Decision: a `ParamSpec` may name where its values come from — `Suggest = Devices(kind)` (engine `devices`: displays,
  services, credentials, adb), `Config(kind)` (the open config: flag names, programs, the inputs of the Android
  device in param `device`), `Static(values)` (Android key names, one Core table the provider also maps) — and carry
  `Examples` (title + value). The inspector renders from them: Text → editable ComboBox (free text still allowed);
  TextList → a checklist when unordered (screens), a token list with an add box when `Ordered` (keys);
  MultilineText/TextMap → an "Examples" menu that fills the box (Ctrl+Z undoes). Validation is unchanged: suggestions
  never restrict a value, except where a Choice already does.
- Why: the user wants to pick values, not type them, and to see examples for code, PowerShell and Android steps
  (2026-11-02). Keeps "the inspector renders from `ParamSpec`" (ARCHITECTURE → Adding a provider).
- Why not new Library kinds (screens, services, keys): the Library holds named, reusable, testable things; a screen
  set or key list is a step's value, and a Library detour adds two clicks. Why not per-provider inspector templates:
  every new provider would need editor code.
- Revisit if: a value depends on more than one other param, or users want whole-step presets (e.g. "Sunshine: close
  the app" filling address, method and credential at once).

## D-051 · 2026-11-02 · "Use only these screens" is picked from the PC's screens and saves monitor names
Status: accepted (M11)
- Decision: the `outputs` param is a checklist of the screens plugged in now (`devices displays`: monitor name, port,
  on/off); checking writes the monitor name, or the port when the name is empty or isn't unique under the
  provider's matching rule. The provider matches a saved name exactly first and only then falls back to its old
  "contains" match, so checking DP-2 "DELL U2723QE" can't turn on DP-1 "DELL U2723QE HDR". Saved entries not
  plugged in stay checked as "Not connected right now". "Use the screens that are on now" checks exactly the active
  ones. The provider keeps matching by port or monitor name, so hand-written and scd-style port lists still load.
  `DeviceInfo` gains optional `Value` (what a picker writes) and `State`; additive, `protocol` stays 1.
- Why: typing monitor names exactly as Windows spells them is error-prone (user, 2026-11-02). A name survives moving
  a cable to another port; a port is the only tell between twin monitors.
- Why not monitor device paths: unique but unreadable in YAML. Why not always ports: a re-cabled screen silently
  stops matching.
- Revisit if: twin monitors also swap ports (then write the device path, shown by name).

## D-052 · 2026-11-02 · The Bluetooth list is Windows' paired devices; audio endpoints only add state
Status: accepted (M11, fixes the user's report)
- Decision: `devices bluetooth` lists present device nodes `BTHENUM\DEV_<addr>` (classic) and `BTHLE\DEV_<addr>` (LE)
  through CfgMgr32 (`CM_Get_Device_ID_List`, `DEVPKEY_Device_FriendlyName`), merged by address. A device with audio
  endpoints is "can connect here" and takes Connected from them; others are listed but not pickable for
  connect/disconnect ("Daze Control can connect only headphones and speakers."). Connected for non-audio devices
  comes from the devnode's undocumented `{83DA6326-97A6-4088-9453-A1923F573B29} 15` (bool): it matched Settings' AEP
  `IsConnected` on all 8 nodes of the dev PC (T11.1, 2026-11-02); missing → no state shown.
- Why: the old list came from audio endpoints, including NOTPRESENT ones, so it showed unpaired devices and missed
  every non-audio pairing — it matched neither Settings nor reality (X-005).
- Why not WinRT `DeviceInformation` (the AEP list Settings itself uses): pulls the CsWinRT projection into the AOT
  engine (size, startup, D-001); use it only if devnodes miss a device Settings shows. Why not
  `BluetoothFindFirstDevice`: classic only, no LE devices.
- Revisit if: connecting non-audio devices is wanted (needs `BluetoothSetServiceState`, i.e. admin, D-008).

## D-053 · 2026-11-03 · The flow menu is designed as a sub-project: `docs/flow-menu/DESIGN.md` + shared ADRs and PLAN M12
Status: accepted (M12 design); its revisit came true with flow calls (D-061): `docs/<name>/DESIGN.md` is now the rule for large designs (`knowledge-log`)
- Decision: the feature's spec (features, architecture, UX, spikes) lives in one file, `docs/flow-menu/DESIGN.md`;
  its decisions stay in this file (D-053..), its tasks in PLAN → M12, its strings in UX §5.7 (#28–#44). Each M12 task
  moves what it built into ARCHITECTURE/UX and cuts it from DESIGN.md, which ends as the list of what's still open.
- Why: the user wants this large feature designed as its own sub-project before any code (2026-11-03); a folder keeps
  ~200 lines of not-yet-true design out of ARCHITECTURE ("current state only").
- Why not a full doc set in the folder (own DECISIONS/PLAN): splits the D-xxx sequence and the Resume block, so
  `grep DECISIONS` and the `resume` skill would miss it. Why not an "M12" section in each shared doc (as M11 did):
  ARCHITECTURE would describe code that doesn't exist, and the design is twice M11's size.
- Revisit if: a second sub-project appears (then make `docs/<name>/DESIGN.md` the rule in `knowledge-log`).

## D-054 · 2026-11-03 · A menu pick launches the flow (its `whenRunning`); `wait` nests; nothing picked stops quietly
Status: accepted (M12 design); `wait: true` amended by D-061 (it nests instead of launching and awaiting)
- Decision: `menu.show` lists 1–12 flow ids; a pick starts that flow through `EngineController` like the tray (trigger
  "Menu · <menu flow>", its own `whenRunning`, history, pop-up) and the step succeeds with the summary "Picked …".
  `wait: true` makes the step wait for the picked flow and take its outcome. Closing without a pick (B, Esc, click
  outside, close timer, the flow stopped) cancels the menu node and the flow, and shows no pop-up
  (`StepEndsFlowException`, `RunFinished.Quiet`). Submenus are items whose flow has a menu; no in-window submenus.
  Validation: own flow → error; unknown or duplicate item → warning, dropped; a `wait` loop → error. One menu on
  screen: a presenter gate held only while the menu shows, never a step resource (a `wait` parent would hold it while
  its child's menu waits: deadlock, PR #28 review). "Back" (an item = the parent's flow) follows the parent's
  `whenRunning`, so it reopens the parent only once that run has ended.
- Why: launch reuses every existing rule (whenRunning, locks, history) and needs no recursion guard: the first menu's
  run ends before the next menu shows, so menus may point at each other ("Back" = the parent's flow). A user closing
  the menu isn't a failure, so no "Didn't finish".
- Why not always nest: the child's queue/restart rules and timeouts compound, cycles become runtime recursion, and
  the parent run stays open while the user plays. Why not the pick as the step's outcome for edges: edges know
  success/failure/always only; the items already are the branches. Why not "nothing picked = failure": a red pop-up
  for pressing B.
- Revisit if: users want a flow to continue differently per pick (then an edge `when: picked <id>`).

## D-055 · 2026-11-03 · The menu window never activates: borrowed keys via RegisterHotKey, a pass-through mouse hook
Status: accepted, borderless only (user, 2026-11-04: "Borderless only", after spike S2 failed gate (a) for
full-screen games, X-010). The menu shows over borderless and windowed games and over the desktop. Before
drawing anything or taking any key, it checks `SHQueryUserNotificationState`. `QUNS_RUNNING_D3D_FULL_SCREEN`
covers exclusive mode and FSO alike, and then the menu doesn't open: we never knock a game out of full screen.
- Refusal is logged, not silent: the step fails with #32 in run history, with one engine log line per refusal, and
  no run pop-up (a pop-up would kick the game out too). Why: a press that seems to do nothing needs an answer
  somewhere the user can find it ("switch the game to borderless"). Why not silent: the user couldn't tell a
  refusal from a missed chord.
- S2 result (DESIGN §5): over a borderless game the menu works with keys and mouse, the foreground never changes,
  and the game renders at 113–121 fps throughout. All 20 keys register and are freed on every close path, a killed
  process included. The hook's own time averages ~6 µs (max 245 µs). Over a DXGI full-screen game (flip + FSO, or
  exclusive), any window we show kicks the game out of full screen, and QUNS reports 3 for both. So the #32
  check covers every DXGI full-screen game, not only true exclusive mode. The menu can only show over borderless
  or windowed games.
- Decision: `Engine/Shell/FlowMenu` on the run pop-up's surface (D-034, cut out as `AcrylicSurface`), no-activate and
  topmost, centred on the foreground window's monitor. While open only: Up/Down/Left/Right/Enter/Space/Esc/Tab/Home/
  End/1–9 are `RegisterHotKey`s on the message window, a `WH_MOUSE_LL` hook (never swallows) spots a click outside;
  all are released on every close path. Any DXGI full screen (`QUNS_RUNNING_D3D_FULL_SCREEN`: exclusive mode or
  FSO) → the step fails with #32 before showing (see Status).
- Why: keeps the hard rule (never take focus); a game or video in front keeps running; hotkeys are what the engine
  already uses (`GlobalHotkey`), work over elevated windows and are freed by the OS if the engine dies.
- Why not activating the menu: breaks the hard rule (user declined), minimizes exclusive full-screen games, and the
  foreground lock refuses it after a controller trigger anyway. Why not `WH_KEYBOARD_LL`: a slow callback gets the hook
  dropped silently, and UIPI hides elevated windows' keys. Why not `CreateWindowInBand` (Game Bar's band): needs
  uiAccess, signing and Program Files (admin, D-008). Why not WinUI: memory (D-001).
- Revisit if: S2 shows hotkeys don't reach us over a game (then ask the user about focus), or Windows offers a
  documented overlay band. (S2: hotkeys did reach us; the window itself is what full-screen games can't take.)

## D-056 · 2026-11-03 · Controller input: GameInput callbacks if they idle at 0, else XInput polling only while a pad is on
Status: accepted 2026-11-04 (spike S1): **XInput** (`xinput1_4`), polled only while a pad is on. GameInput is out:
reading callbacks are E_NOTIMPL on PC (X-011), so it polls too, plus GameInputSvc. Probe numbers (AOT, exe 1.5 MB):
no XInput pad, 600 s: 0 ms (one run 16 ms, a single tick), 22.7 M cycles, 7 threads, 5.4 MB private; arrival and
removal rescan the slots and go back to sleep with 0 polls. The Steam Controller (wireless puck) is never an XInput or
GameInput pad on the desktop, Steam on or off; it reaches us only as keys (D-056's "keys, no code" holds; DESIGN §5 S1
lists them). Flydigi Vader 4 Pro on its wireless adapter (an Xbox 360 XInput pad, VID_045E PID_028E): read in
the background with Steam on and off; pad on and untouched, 600 s: 234 ms, 866 M cycles, 5493 polls (about 50 µs per
poll; the pad slept after about 4 min, so it was polled about 5.5 min). Kept on for the whole window, 20 Hz would be
about 410 ms, over the 300 ms budget: T12.6 measures the real `GamepadSource` and, if it is over, polls at 10 Hz
while only chords listen. Sleep/resume was not run.
T12.6: `GamepadSource` polls at 10 Hz while only chords listen (a chord fires on release, 100 ms is unnoticeable), 60 Hz in a menu.
(user, 2026-11-03: polling while a pad is on is acceptable.)
- Decision: `Engine/Input/GamepadSource` reads Xbox/XInput pads. First choice: the inbox `GameInput.dll` (present on
  the dev PC, 0.2309.26100, no redist) with reading + device callbacks and background input. Fallback: `xinput1_4`
  polled at 20 Hz while only chords listen and 60 Hz while a menu is open, on connected slots only, and only while a
  pad is connected (device-interface notifications, suspend/resume). Nothing loads unless a runnable flow has a
  `gamepad:` chord, a menu shows, or `record-gamepad` runs (a temporary consumer, released when it answers, times out
  or its client goes); `devices gamepads` enumerates once on demand. Steam Controller: through Steam's desktop layout (keys), no code.
- Why: the user's pads are an Xbox controller and the Steam Controller (2026-11-03); D-009 wants 0 CPU idle, and the
  dev PC is a laptop on battery, so the zero-cost path goes first and polling is bounded to "a pad is on".
- Why not Windows.Gaming.Input: CsWinRT in the AOT engine (D-052's reasons), it polls too, and desktop apps get no
  readings without focus. Why not Raw Input/HID: per-pad report parsing, and pads that report at 250 Hz+ nonstop
  wake the engine more than polling. Why not `XInputGetKeystroke`: still a poll. Why not a filter driver (HidHide) to
  hide presses from games: admin and a third-party driver (D-008).
- Revisit if: S1 fails both gates (then ask the user), or PlayStation pads become a goal (GameInput or HID).

## D-057 · 2026-11-03 · Controller trigger: `gamepad:` chords of 2–4 buttons, fired on release, recorded by the engine
Status: superseded by D-086 (chords removed 2026-11-04; built in v0.40.0)
- Decision: flow key `gamepad: [View+Menu]` (Xbox names A B X Y LB RB LT RT View Menu LS RS Up Down Left Right,
  canonical order); no Guide, no single buttons. A chord fires when its buttons were held together, with no other
  button, and the first one is let go. Hotkey rules apply (enabled valid flows, later clash gets a problem, Pause
  shortcuts pauses them). The editor records through the engine (`record-gamepad`), which pauses chords meanwhile.
- Why: on release, a chord that is part of a longer one never fires early, and the menu opens with the buttons up
  (so the chord's A can't pick). Two buttons minimum keeps games from starting flows by accident. Recording in the
  engine means what you record is what the engine sees, with one backend (D-056).
- Why not firing on press: `View+Menu` would fire inside `View+Menu+A`, and held buttons would leak into the menu.
  Why not a "hold one button" trigger: games hold buttons all the time. Why not the editor reading the pad (WGI):
  a second backend that can see pads the engine can't.
- Steam (S1, user 2026-11-04): with Steam running, its desktop layout turns `View+Menu` on an Xbox pad into Alt+Tab,
  so the chord fires and the task switcher opens too. The user keeps `View+Menu` as the default (template, examples);
  the README tells Steam users to unbind it in Steam's desktop layout (T12.10, DESIGN §4.6).
- Revisit if: users want long-press triggers (then `Hold View` with a ≥ 1 s threshold).

## D-058 · 2026-11-03 · "Run together" on linked steps drops the links between them instead of refusing
Status: accepted (supersedes the "edges between members refused" part of UX §5.2 for Ctrl+G and drops)
- Decision: Ctrl+G removes edges between the steps it groups; a body drop also removes the dragged step's links
  on any path to or from the target (a → b → c, c onto a: [a c] → b). Outer edges move to the card as before; one
  undo step. A removed `failure`/`always` link is said in a tip beside the card (§5.7 #45). Still refused: loops
  through other steps (Ctrl+G), nested cards, and an edge a user draws between members (§5.7 #4, `CanConnect`).
- Why: the user (2026-11-03) drags steps together to fix a wrong sequence; refusing because of that sequence
  made the gesture useless. The drop names which step moves, so its path links are the ones to cut.
- Why not refusing with a "remove the link first" tip: the old behaviour, reported as the bug. Why not cutting
  every link of the dragged step: its unrelated waits (x → b) would vanish silently. Why not refusing fallback
  links: the user still means "together"; the tip makes the behaviour change visible and Undo restores it.
- Revisit if: users expect the dragged step's dependents to be re-linked to its old predecessors (as Delete does).

## D-059 · 2026-11-03 · A new fallback switches its source from "Stop the flow" to "Skip what depends on it"
Status: accepted
- Decision: when an edge from a step or card becomes `when: failure` (inspector "If it fails, run…", Connections,
  edge menu) and the source's policy is `stop`, the editor sets `skip` in the same undo step and says so (§5.7 #46).
  `skip`/`ignore` chosen by the user stay; removing the last fallback doesn't switch back.
- Why: user, 2026-11-03: a fallback link should change the step's behaviour by itself. Under D-024 a fallback
  already handles the failure, so `stop` + fallback runs exactly like `skip` + fallback (SchedulerTests
  `WithAFallbackStopAndSkipRunTheSame`): the fallback runs, dependents that needed success are skipped, the rest go
  on. The inspector said "Stop the flow · Nothing after this runs", which was false; now it shows what happens.
- Why not `ignore` ("Keep going", the user's word): it counts the failure as done, so the success dependents run
  too, which breaks "try X, else Y" (the fallback and the normal path would both run). Why not leaving the policy and
  only fixing the inspector's wording: the user asked for the setting to follow, and the YAML then says it too.
- Revisit if: users want "run the fallback, then stop the flow" (D-024's revisit), which needs `stop` to mean that.

## D-060 · 2026-11-03 · Long simple chains wrap onto new rows; forks, joins and their branches never wrap
Status: accepted (built in C3, 0.23.0; supersedes D-020's "one column per layer" for wrapped chains only); placement after a block superseded by D-066
- Decision: after layering, ordering and `FlowTidy`, a last placement pass in `Core/Layout/FlowLayout` finds
  **spine runs**: maximal sequences of consecutive layers that each hold exactly one box (a step or a card), where
  every box in the run, both ends included, has at most one incoming and one outgoing edge, each box's edge goes to
  the next box (any `when`), and no other edge crosses the run's layers (a fork's long arm beside a direct edge to
  its join is not a run, and a join never starts one). A run longer than
  `LayoutOptions.WrapAfter` boxes (default 5) is cut into rows of `WrapAfter`, placed like text: every row reads left
  to right from the run's first column, rows stacked downwards with a 48-DIP gap. Whatever follows the run starts
  in the column after the block's right edge, top-aligned with its last row. Nothing that forks, joins or runs side by
  side with another branch is ever wrapped, so complex parts keep their separate columns (D-020 as before).
- Wrapped edge: from the row's last output port, right 16, down to the middle of the gap, left to a return lane 16
  DIP left of the next row's start column, down to the first input port's height, then right 16 into the port (so
  the corner and arrowhead have a straight approach); orthogonal with 8-DIP corners, styled by
  `when` like any edge. `EdgeCurve` gains this shape for hit-tests (UX §5.8 #2).
- Unchanged: YAML (no coordinates), layers and `nodes` order, Outline order, keyboard order (Right/Left = next or
  previous step in run order, so Right from a row's end goes to the next row's start), drag zones (computed from
  boxes), Tidy up (works on layers before the wrap pass). Fit/zoom use the wrapped bounds.
- Why: a 10-step chain needs ten columns and scrolls sideways, while it has no structure to show (user, 2026-11-03).
  Count-based so the layout doesn't jump when the window is resized, and stays testable.
- Why not wrap at the window width: re-layout on every resize, and the same flow looks different on two screens.
  Why not snake (alternate rows right-to-left): arrows pointing left read as going back. Why not wrap whole branches:
  a fork's columns are what show it runs in parallel.
- Revisit if: users want width-based wrapping or a per-flow "don't wrap" switch.

## D-061 · 2026-11-03 · "Run another flow" is one model with two modes: Start it (launch) or Wait for it (nested)
Status: accepted, built (T13.8; amends D-054's `wait`)
- Decision: a new step `flow.run` (Flow › Run a flow, [flow-calls/DESIGN.md](flow-calls/DESIGN.md)) and `menu.show`
  share one seam, `IFlowStarter` (Runtime), with two modes. **Start** (`wait: false`, the menu's default): a launch
  through `FlowLauncher` exactly as D-054 says (own `whenRunning`, record, pop-up). **Wait** (`wait: true`, the step's
  default): a nested run inside the caller's run (D-062); the step takes its outcome. Menus with `wait: true` now nest
  too, instead of launching and awaiting.
- Why: the user wants typical things composed into one step (2026-11-03); a building block called by several flows
  is a subroutine, so it belongs to the caller's run (one pop-up, one record, no `whenRunning` refusing the second
  caller). Launch stays right where a person picks or where the caller doesn't care. One seam keeps menus and calls
  from drifting apart.
- Why not launch-and-await for Wait (D-054 as written): `whenRunning: ignore` would refuse a building block that
  another flow is using, two pop-ups would show for one press, and history would split one action in two. Why not
  nest only (no Start): fire-and-forget is the menu's main case, and "start the TV flow, carry on" is useful in flows.
- Revisit if: users expect a waited flow's `whenRunning` to queue concurrent callers (then a per-flow "one at a time
  even when called" switch).

## D-062 · 2026-11-03 · Nested runs: no `whenRunning`, no lock held by the caller, one record and one pop-up
Status: accepted, built (T13.8)
- Decision: `FlowRunner.RunNestedAsync` runs the called flow's runnable version (D-021) on a new `Scheduler` with
  `ParentRunId`/`ParentNodeId`; no launcher slot. The `flow.run` step declares no resources; nested steps lock per
  attempt as usual (D-010). Outcome → step: Succeeded → Succeeded; PartiallySucceeded → Succeeded plus a problem for
  the caller's run; Failed → Failed (#56); quiet stop → the caller ends quietly; timeout (default 10 min) → Failed and
  the nested run is cancelled. History: one top record, `NodeResult.Called` nests results. Pop-up: the top run's
  only; the call counts as one step and row 3 shows the nested step (#59). A turned-off flow is never run by a call.
- Why: locks held only around attempts and never while waiting mean nesting can't deadlock; one record and one
  pop-up match "one step" in the user's words. Partial → Succeeded keeps "try X else Y" from treating a may-fail
  hiccup inside X as X failing.
- Why not the call step holding the called flow's resources: unknown before it runs (conditional branches), and it
  would block the nested steps themselves. Why not separate history records linked by id: the Runs list would show
  building blocks as unrelated runs. Why not Partial → Failed: a skipped "may fail" extra inside a block would trigger
  the caller's fallback for a block that did its job.
- Revisit if: lock waits inside nested runs show up in history (then show "Waiting for Technics" in the pop-up).

## D-063 · 2026-11-03 · Flow-call loops are refused by validation and stopped at run time (call chain, depth 8)
Status: accepted, built (T13.8; user, 2026-11-03: "no infinite loops")
- Decision: `ConfigValidator` builds the call graph from every `flow.run` (both modes) and every `menu.show` item with
  `wait: true`; its own flow → error #49, any cycle → error #50 on each step in it. Menu items without `wait` don't
  count. At run time each run carries its call chain; a call to a flow already on it fails (#53), a chain over 8 fails
  (#54). Start-mode calls pass the chain to the launched run; a person's trigger starts a fresh one.
- Why: validation catches every loop in one config, but D-021 can run an old version of one flow beside a new version
  of another, a mix validation never sees; the chain check is exact, the depth cap bounds anything else. Start-mode
  loops count because nothing stops them (A starts B starts A, forever).
- Why not runtime only: the user would meet the loop as a failed run instead of a problem in the editor. Why not
  validation only: the D-021 mix above. Why not counting menu picks: a person picks every time, and "Back" items are
  loops by design (D-054); a chain through picks would stop Back working after 8 hops.
- Revisit if: someone needs recursion on purpose (retry-until loops); then a bounded "repeat" step, not calls.

## D-064 · 2026-11-03 · Flow inputs are Library devices with defaults; steps use `$name`; calls pass `inputs:`
Status: accepted (M13 design; user, 2026-11-03: inputs ship inside P6); amended by D-129 (values in text settings) and D-131 (text inputs)
- Decision: `Flow.Inputs` = name → (`LibraryKind`, label, default key); the default is always given (YAML `kind`,
  `name`, `default`; no "required" flag, every input is optional for a caller). A `LibraryRef` param of the same
  kind may hold `$name`; the runner resolves it (caller's value, else the default) before resources are filled.
  `flow.run` passes `inputs:` (TextMap: Library keys or the caller's own `$name`). Validation checks kinds, names and
  keys like any library ref; `LibraryEdits.Rename` rewrites defaults and passed values.
- Why: the user's chain ("make it default, else connect it") must serve Technics and Sony without copies. Typed
  Library inputs keep every value checkable before a run and reuse the device pickers; required defaults keep every
  trigger (hotkey, tray, menu, desktop shortcut) working with no caller.
- Why not free-text substitution (`{x}` in any value): every ParamKind's validation and pickers would have to accept
  placeholders, and a typo becomes a run-time failure. Why not optional defaults: a flow that only works when called
  would need a new "can't run on its own" state everywhere. Why not a copy of the flow per device: the user chose
  inputs (2026-11-03).
- Revisit if: users need text inputs (a key, a message) or values passed back (then outputs).

## D-065 · 2026-11-03 · A fallback that wasn't needed is "Not needed"; a failure its fallback fixed ends "Done"
Status: accepted (M13 design; user, 2026-11-03; supersedes D-024's "a handled failure makes the flow
PartiallySucceeded")
- Decision: a **fallback step** is a unit with an incoming `when: failure` edge, or an incoming `when: success` edge
  from a fallback step (members follow their card), computed in Core (`FlowShape.Fallbacks`). When the Scheduler
  would skip one, it ends **NotNeeded** (new `NodeOutcome`; fires `always` like Skipped). Counting (superseded by
  D-127): "n of m" leaves fallback steps out of m until one starts. Summary: Succeeded when every unit is Succeeded, NotNeeded, or Failed /
  FailedIgnored with an outgoing failure edge (handled); otherwise as before. Pop-up: "Done" + #48 when a fallback
  fixed it. Runs: "Worked", the handled bar caution-dashed, NotNeeded rows "Not needed" (#47). The tray's problem
  notification follows the outcome.
- Why: the user's Connect Technics flow said "Done, with a problem" every time the headphones simply worked, because
  the unneeded Sony branch was Skipped; a fallback is a "finally"-style plan, not a warning. A fixed failure is the
  plan working, and P6 needs "Make it the default" to count as worked when its fallback did the job (user's choice).
- Built (T13.7, 2026-11-03): a step skipped only because a handled failure didn't fire its `success` edge (the
  user's `out` after `bt`) is the branch the fallback replaced, so it doesn't make the run partial either; the
  done-when ("Technics fails and Sony works → Succeeded") needs it. Any other skip still does.
- Why not hiding fallback steps from results: the Runs page should still show what could have run. Why not a flag on
  Skipped instead of a new outcome: every consumer (pop-up, Runs, canvas, tray) would need the flow to tell them
  apart, and history outlives flow edits.
- Revisit if: users miss being told that a fallback was used (then a quiet caption, not a warning tone).

## D-066 · 2026-11-03 · After a wrapped chain, what follows starts on a new line under it
Status: accepted (M13 design; user, 2026-11-03; built in T13.5; supersedes D-060's "whatever follows the run starts
in the column after the block's right edge, top-aligned with its last row")
- Decision: `FlowLayout` lays columns out in **segments** split at wrapped spine runs. The segment after a block
  starts at the block's left x, its top one `WrapRowGap` under the block's bottom, its columns centred among
  themselves; the edge into it is a wrap edge (D-060's shape: right 16, down to the gap's middle, left to the return
  lane, down to the target port, right 16). Later segments repeat the rule. The rest of D-060 stays.
- Why: a long empty line ran from a short last row to the next step, and the space under the block stayed unused
  (user, 2026-11-03); reading like text keeps the width bounded by the block.
- Why not continuing on the last row (the other option shown to the user): compact only when the last row is short,
  as wide as before when it's full. Why not starting the new line at the far left: its wrap edge would run under
  everything before the block.
- Revisit if: users want the following part beside a short last row (then a "fits in the row" special case).

## D-067 · 2026-11-03 · Tidy up becomes a Layout menu: wrap, spacing, alignment, line style, untangle
Status: accepted (M13 design; user, 2026-11-03: "a layout menu… a couple more strategies… make it wider"); built T13.6;
its "why not circular or tree" and "no undo" superseded by D-115
(no reflow animation: none exists on the canvas yet; the wrapped rows' gap stays 48 for every Spacing)
- Decision: the CommandBar's Tidy up becomes **Layout** (flyout, §5.7 #63): Wrap long chains after 3 / 5 (default)
  / 8 steps / Never; Spacing Compact (40/8) / Normal (64/16) / Roomy (96/32); Line up Centred / Along the top; Lines
  Curved / Right angles (orthogonal, 8-DIP corners, D-060's router); Untangle lines (the old `FlowTidy`, off with a
  reason when it can't help, X-006); Reset layout. Choices are per flow, per PC, in `editor.json` (D-068), applied at
  once with the reflow animation; they are not flow edits (no draft, no undo). Untangle stays a flow edit (it
  reorders `nodes`). All map to `LayoutOptions`; layout stays automatic and deterministic (D-020).
- Why: these are the knobs layered-graph tools offer (Graphviz `nodesep`/`ranksep`/`splines`, ELK layered spacing,
  edge routing and node placement, yEd's hierarchical layout) that fit a left-to-right step graph; "wider" is Roomy
  or Never.
- Why not top-to-bottom direction now: ports, drop zones ("right third = Run after"), keyboard (Right = next) and the
  wrap all assume left to right, roughly a second canvas. Why not organic, circular or tree layouts: not
  deterministic, or wrong for joins; a layout must not jump between opens. Why not in YAML: it's how one PC views a
  flow, and every toggle would hot-reload the engine.
- Revisit if: the user asks for top-to-bottom (own task) or wants layouts to follow the config to another PC.

## D-068 · 2026-11-03 · Editor view state lives in `%LOCALAPPDATA%\DazeControl\editor.json`
Status: accepted (M13 design); built T13.3 (an editor started with `--config` keeps it next to that file)
- Decision: `Editor.ViewModels/Config/EditorSettingsStore`: STJ source-generated, read once at start (missing or bad
  → defaults, logged), written atomically 300 ms after a change. Holds the nav pane's open state (P9) and per-flow
  layout choices (D-067). The engine never reads it.
- Why: the editor is unpackaged (Velopack), so `ApplicationData.LocalSettings` has no package identity to use; view
  state isn't configuration, and writing config.yaml would make the engine reload.
- Why not the registry: harder to back up and inspect; only autostart lives there (D-015).
- Revisit if: view state must follow the user to another PC (then roaming `%APPDATA%`).

## D-069 · 2026-11-03 · Right-drag pans the canvas; a right click that doesn't move still opens the menu
Status: accepted (M13 design; user, 2026-11-03); built T13.4. D-074 adds pan room, so it moves a flow that fits too
- Decision: in `FlowCanvas`, a mouse right press arms a pan; once the pointer leaves the system drag rectangle
  (`GetSystemMetricsForDpi(SM_CXDRAG/SM_CYDRAG, dpi)` for the window's DPI, converted to DIP as px × 96 / dpi, since
  pointer positions are DIP) it captures, pans like middle-drag with the same cursor, and the
  release swallows `RightTapped`/`ContextRequested`. Released inside the rectangle → the context menu as today, for
  whatever is under the pointer. Works over pills and edges without selecting them. Pen and touch unchanged.
- Why: the user pans without scrolling sideways; diagram tools (Visio, draw.io, Miro) pan on right-drag and keep the
  click for the menu, and the system drag rectangle is how Windows tells a click from a drag.
- Why not a fixed pixel threshold: ignores the system setting and DPI. Why not a hold delay: slower, and unlike
  other tools.
- Revisit if: users report menus not opening after small hand tremors (then a larger threshold for right only).

## D-070 · 2026-11-03 · The Flows card runs with a "Run" button; the card itself opens the flow
Status: accepted (M13 design; user, 2026-11-03)
- Decision: the card's Edit chevron becomes a plain **Run** button (engine `run`, trigger "Editor", the saved
  version); while that flow runs it reads **Stop**. Off with a reason (#62): engine not running, flow turned off,
  never valid. The card becomes one keyboard stop that opens the flow on Enter/Space and exposes Invoke ("Open
  Connect Technics"); the On switch and Run stay their own stops. Its context menu adds Open and Run. The card's icon
  is `FlowGlyph.CodeOf` (own glyph, else its first step's), as in flow settings and desktop shortcuts (P3).
- Why: a click on the card already opens it (second-round feedback); the chevron read as "open", and the user wants
  to run from the list. The Edit button existed only for the keyboard, which the card now covers.
- Why not an accent Run button: one accent per page (New flow). Why not icon only (▶): the user asked for the word.
- Revisit if: users start flows by accident from the list (then confirm for flows without a pop-up).

## D-071 · 2026-11-03 · Shortcut keys: [ ] \ and the other punctuation keys, and number-pad digits (alone too)
Status: accepted (design M13b, user 2026-11-03); built T13b.1
- Decision: `Core/Input/Hotkey` takes, besides A–Z, 0–9 and F1–F24, the punctuation keys by virtual-key code with
  US names: `[` `]` `\` `;` `'` `,` `.` `/` `` ` `` `-` `=` (VK_OEM_4, 6, 5, 1, 7, COMMA, PERIOD, 2, 3, MINUS, PLUS),
  and the number pad's digits `Num0`–`Num9` (VK_NUMPAD0–9; "Numpad1" parses too, `Num1` is canonical). Win, Ctrl
  or Alt stays required, except for `Num0`–`Num9`, which may stand alone (user: "Alone too"). Shift with a Num
  key is refused. Num keys need Num Lock on (user); with it off the pad sends End, Down… and nothing fires. YAML
  stores the US name whatever the layout; the YAML writer may quote `Win+[` (round trip tested).
- Why: the user wants Win+[ ] \ and Num1… (2026-11-03). `RegisterHotKey` takes all of them with no hook (spike,
  dev PC: Win+\, Ctrl+Alt+[, Num0/1/5 alone, Ctrl/Alt/Win+Num1 all registered). It is VK based, so the stored key
  stays the same when the user switches layouts. Shift is refused because with Num Lock on Windows turns
  Shift+Num1 into End, so the shortcut could never fire. A bare Num key swallows that digit in every app while
  Num Lock is on (the user accepted this; Num Lock off or Pause shortcuts gives the digits back).
- Why not a low-level keyboard hook (to work with Num Lock off, or tell the pad's End from the real End): it would
  stay resident and see every key press. That goes against idle = zero, it looks like a keylogger to antivirus,
  and Windows drops slow hooks. Why not the layout's character as the name (`ß` on German): the name would change
  with the layout while the key doesn't. Why not bare punctuation or letters too: they'd take normal typing.
- Revisit if: someone on a non-US layout finds the names confusing (then show `MapVirtualKey`'s character in the
  editor and keep the US name in YAML), or the user wants number-pad keys with Num Lock off.

## D-072 · 2026-11-03 · Recording pauses the engine's shortcuts; failed shortcuts are retried on user actions
Status: accepted (design M13b, item 5 of the user's 2026-11-03 request); built T13b.3 (failures stay known while recording or paused)
- Decision: the recorder tells the engine when it starts and stops recording (pipe op `record-hotkey`
  `{on: true|false}`, like M12's `record-gamepad`). While on, `HotkeyTable` unregisters every keyboard shortcut
  (its own flag, apart from the tray's Pause, not shown in the tray). The pause ends on `off`, when the editor's
  pipe connection drops, or when the editor exits. It ends with a `Sync`, which registers every wanted chord
  again, including the ones that failed. Failed chords are retried on user actions too: opening the tray menu,
  Resume, and every reload (as now). When a retry changes the failures, the engine sends a `shortcutsChanged`
  event and the editor reads `status` again, so the "Another app uses…" note goes away at the next of these
  actions after the other app has let the key go (not at the moment it quits). No timer, no polling.
- Why (evidence, dev PC, 2026-11-03): a failed chord was only retried on the next reload. Recording the same chord
  again changes nothing (`InspectorViewModel.SetShortcut` skips an equal list), so nothing is saved, nothing reloads,
  and the note stays until the engine restarts, even after the other app quits. The engine's own registered chords
  never reach the recorder either: `RegisterHotKey` takes the keystroke, so the flow runs instead (an ARCHITECTURE
  leftover). `RecordingChanged` had no subscriber.
- Why not retry on a timer: idle = zero. Why not have the editor test the key with its own `RegisterHotKey`: that
  races with the engine, which is the owner, and still can't see past the other app.
- Revisit if: a chord is held by an app that grabs it again whenever we let go (then keep ours during recording).

## D-073 · 2026-11-03 · "Taken" vs "not supported": the recorder says which, and names Windows when it's Windows
Status: accepted (design M13b); `HotkeyRejection` + `WindowsShortcuts` built T13b.1, H5/H6 built T13b.3, the recorder text (H1–H4, H7) built T13b.2
- Decision: the recorder's `Rejected` event carries a reason. The inspector shows it under the box (UX §5.7 H1–H4):
  the key can't be a shortcut, a modifier is missing, Num Lock is off (a number-pad navigation key: `VirtualKey`
  End/Down/… with `KeyStatus.IsExtendedKey` false), or Shift with a Num key. A chord the engine can't register
  (error 1409) gets "Windows uses…" (H5) when it's on a fixed list of Windows 11's own Win shortcuts in
  `Core/Input/WindowsShortcuts`, else "Another app uses…" with the tray hint (H6). Modifiers that were held and let
  go without any key arriving get H7 ("Nothing came through"), since another app's chord never reaches us.
- Why: in the user's case, Win+[ did nothing in the recorder. `Hotkey.TryCreate` refused it and nobody listened to
  `Rejected`, which looked like "taken". Once supported, it is taken: `C:\Projects\Autohotkey\SwitchDisplay.ahk`,
  still running since 2026-10-15, binds `#[` and `#]` (both 1409 in the spike; Win+\ was free).
- Why not name the app that holds the key: Windows has no API for it (X-007). Why not the error code alone: Windows
  and apps both give 1409 (X-007).
- Revisit if: the Windows list goes wrong after a Windows update (the generic "Another app…" is the safe fallback).

## D-074 · 2026-11-03 · The mouse wheel zooms the canvas at the pointer; a flow can always be moved
(Zoom range 50–200 % superseded by D-126: 10–200 %.)
Status: accepted (user, 2026-11-03, v0.29.0 feedback); built F1. Changes UX §5.2 Navigate.
- Decision: in `FlowCanvas`, a mouse wheel notch zooms (×1.1 per 120 delta, 50–200 %) around the point under the
  pointer, which stays put; Ctrl+wheel does the same; Shift+wheel still scrolls sideways. The scroll extent gets
  room to pan: the `ScrollViewer`'s content becomes a transparent wrapper Grid around `Host` whose padding is the
  viewport size (in content DIP at the current zoom) less a 48-DIP strip and the layout's own 48-DIP margin, so right-drag, middle-drag and Space+drag
  move a flow that fits the window, at least 48 DIP of it stays on screen, and `Host`'s coordinates don't change.
  The wrapper covers the whole viewport, so its `PointerWheelChanged` handler (mouse only, marked handled) runs
  before the `ScrollViewer` scrolls, and does Shift+wheel sideways itself (built: the WinUI 3 `ScrollViewer` scrolls it
  vertically); a precision touchpad's two-finger scroll and pinch stay the `ScrollViewer`'s
  own (pan, zoom). Fit (Ctrl+0) and opening a flow centre it. Right-drag (D-069) is unchanged.
- Why: right-drag works as built but moves a flow only along an axis with room to scroll (0.29.0 build: sideways
  only), which the user read as broken; they asked for wheel zoom, and for a flow that fits to be movable too
  (2026-11-03).
- Why not wheel scroll as before: the user's flows are short and wide, so vertical scrolling rarely helps, and
  diagram tools (Miro, Figma) zoom on the wheel. Why not an unbounded canvas (render transform instead of a
  `ScrollViewer`): loses the `ScrollViewer`'s pinch, touchpad and keyboard scrolling and `BringIntoView` for focus.
  (Superseded by D-119 e, T21.7: the camera does those itself; the wrapper padding is now `Camera.Clamp`.)
  Revisit hit (live, 2026-11-12): touchpad vertical scroll arrives as mouse wheel (device type Mouse; sideways as a
  horizontal wheel), so `WheelInput` tells it by message source and delta size. A smooth-scrolling mouse whose deltas
  aren't whole notches pans instead of zooming; Ctrl+wheel still zooms.
- Revisit if: mouse users miss vertical scrolling on tall flows (then wheel scrolls when the flow is taller than the
  window), or the touchpad's two-finger scroll turns out to arrive as wheel input (then tell it by device type).

## D-075 · 2026-11-03 · A flow's id follows its name, as a safe slug, when a rename is saved
Status: accepted (user, 2026-11-03: "transparently convert the name to safe format"; built in F4)
- Decision: `Core/Model/FlowIds.Slug(name)`: diacritics removed (NFD, non-spacing marks dropped), letters and digits of
  any script lowercased (invariant), every other run of characters one `-`, trimmed of `-`, at most 40 characters (cut
  at a `-`), "flow" when nothing is left. `FlowIds.Unique(slug, taken)` adds `-2`, `-3`… (New flow uses it too). On
  Save of a draft whose name differs from the saved one, the id becomes `Unique(Slug(name))` among the other flows (no
  change when it is already that). In the same write, `Core/Model/FlowRefs.Rename(config, old, new)` rewrites every
  `flow.run` `flow:` that named the old id (M12's `menu.show` items join it when built). After the engine has
  loaded the save, a desktop shortcut of the old id is made again for the new id and the old one removed (pipe
  `create-shortcut` then `remove-shortcut`, both `exactId`). The open flow stays open under the new id. Undo inside
  the draft reverts the name before Save; after Save, renaming back gives the old id again when it's free.
- Not rewritten: history and logs keep the id each run had (a record of what ran; the Runs list shows the recorded
  name); `dctl run <old id>` stops resolving (`dctl run "<name>"` still works). Hotkeys, controller buttons and tray
  placement live inside the flow, so they follow on reload. Per-flow view state in `editor.json` (T13.6 layouts) must
  follow too: T13.6 keys it by id and calls the same rename.
- Why: the user matches log lines to flows by id, and nothing in the UI shows it; an id made from the name is
  guessable. Only on a name change, so a hand-written id that differs from its name isn't rewritten on every save.
- Why not rewrite history.jsonl: it's the engine's append-only record and the logs can't be rewritten anyway, so the
  two would disagree. Why not a separate stable id plus a display slug: two ids for one flow, and the YAML key is
  already the id. Why not ASCII only: a Cyrillic name would become "flow". Why not on every keystroke: the draft
  (D-047) isn't saved, and other flows' references would change for nothing.
- Revisit if: users script `dctl run <id>` and lose it on rename (then keep an `aliases:` list), or the Flows card's
  last run (by id) going blank after a rename bothers them.

## D-076 · 2026-11-03 · Delete a flow from the Flows list, with Undo; off while another flow runs it
Status: accepted (user, 2026-11-03: Undo bar, not a dialog; off until unused; built in F4)
- Decision: the Flows card's context menu (and Delete on a focused card) has **Delete**. It removes the flow through
  `ConfigSession.Update` (saved after 300 ms; the engine reloads and frees its shortcut keys) and shows an InfoBar on the
  Flows page, #67 "Connect Technics was deleted." with **Undo**, which puts the flow back at its place with its id. The
  bar, and with it the chance to undo, ends on another Delete, New flow, opening a flow, leaving the Flows page or
  closing the editor; only then is the flow's desktop shortcut removed (`remove-shortcut`, `exactId`), so Undo needs
  no shortcut work. Delete is off while another flow's `flow.run` names it (`FlowRefs.Callers`), with #68 under it
  in the menu, and while the editor can't edit. Runs already in progress finish; history keeps its runs.
- Why: the user found no way to delete a flow (v0.29.0). An Undo bar costs one click less than a dialog, and the
  flow list autosaves, so the bar is the only safety net besides config backups. Off while used matches the
  Library's Remove (D-041) and keeps another flow from turning itself off (#51 is an error, D-021).
- Why not a confirm dialog: the user picked Undo. Why not delete callers' steps too: silently changes flows the user
  isn't looking at.
- Revisit if: users want Delete inside the open flow (then the same bar on the Flows list after leaving it).

## D-077 · 2026-11-03 · Display steps check the screens after each change and fail when Windows did something else
Status: accepted (user, 2026-11-03: "restore layout and use only screens… don't restore… Only PC screen / Extend";
live test OK'd; built in F5, fix/m13-display-restore). Refines D-019.
- Decision: after each `SetDisplayConfig`, `display.outputs` and `display.layout` re-query the active paths and
  compare them with the request (`DisplayCheck`): the same screens on, no two sharing a source unless asked, and for a
  layout each screen's resolution and position. A mismatch fails the step with #70 (the runner's failure policy
  decides what follows). `UseOnly` gives each screen its own source and asks Windows for the set's saved arrangement
  (`SDC_TOPOLOGY_SUPPLIED`) before letting it pick one (`USE_SUPPLIED | ALLOW_CHANGES`, X-009). `Apply` validates
  first, remaps source ids per adapter, and, when Windows refuses the layout from where the screens are now (error 87),
  switches to the layout's set first and then applies it. `display.topology` (Win+P presets) is unchanged: it was right
  in every case.
  Screens are matched by the monitor's device path and use its lowest target id: this PC's driver gives the DELL a
  second id (0x1009038) while duplicating, and using it broke Dual from Duplicate (live run, F5).
- Why: the live test (PLAN F5) showed "Use only these screens" making up an arrangement, leaving the laptop on and
  duplicated while reporting success, and "Restore screen layout" refused from that state. A step that says
  Succeeded when the screens are wrong is worse than one that fails (the fallback can fix it, D-065).
- Why not drop `SDC_ALLOW_CHANGES`: without it a set the database doesn't know is refused outright, and some drivers
  need small mode changes; the check catches what it changes instead. Why not retry until it matches: each attempt
  flickers every screen.
- Revisit if: the check fails on a change that looks right on screen (for example a refresh rate a driver rounds), then
  compare less.

## D-078 · 2026-11-04 · Android inputs are edited in the Library; a rename follows the steps that pick it
Status: accepted (user, 2026-11-03, L1). Extends D-046.
- Decision: an Android card has an Inputs toggle that opens a panel under it: each input with its D-pad moves as an
  ordered list of Up/Down chips (× removes one; "Add Up"/"Add Down" append), Rename (flyout, refused when empty or
  taken on that device) and Remove (off while used, the tooltip says why), plus Add input (offers "input", "input-2"…;
  a new input has no moves). `AndroidInputEdits` (Core) does the edits; a step "picks" an input when it is
  `android.input` with that input name and its device is the key, or a flow input (`$tv`) whose default is the key;
  rename rewrites those steps' `input`, and that is also what "Used by" counts. The inspector's "Add inputs in the
  Library." is a link (the device by key or through `$tv`'s default) that shows Library › Android with that device's
  inputs open, leaving the flow with the D-047 prompt.
- Why: inputs could only be added by hand in YAML, and the hint pointed to a Library page that couldn't add them.
  "Moves as an ordered Up/Down list" (the user's words) read as the moves of each input: the file stores exactly that.
- Why not reorder the inputs themselves: the order means nothing to the engine (lookup by name). Why not reorder
  moves: they are only up and down, so remove and add again is as quick. Why not follow `$tv` steps whose call passes
  another device: the callee may run with any device; only the default is known here.
- Revisit if: users want a "Try it" that switches the TV's input from the Library (it changes the TV, so it would
  need the same care as Test this step).

## D-079 · 2026-11-04 · Cheaper agent workflow: the tag is the version, merge commits, auto-merge, resume state in the PR
Status: accepted (user, 2026-11-04). Supersedes the per-PR version bump (repo-workflow §4) and the squash merges of
§6; amends D-018 (the Resume block).
- Decision: PRs don't touch `<Version>`; the release cutter picks the next number from the PR titles since the last tag
  (any `feat` → minor, else patch) and release.yml builds the tag's number (`pack.ps1 -Version`). PRs merge with
  merge commits, armed with `gh pr merge --auto --merge` once review is settled; a ruleset makes `build` required on
  main, so GitHub merges when CI is green and the session doesn't wait. A task's live Resume state is a "## Resume"
  section in its PR description (`gh pr edit --body-file`), not the PLAN.md Resume block, which only the coordinator
  rewrites. Copilot reviews code PRs once; a second only after Critical findings; small fix PRs whose tests cover the
  change skip it. Tiny related fixes share a session. Agents batch lookups.
- Why: measured on 26 sessions (2026-11-01..04, ~2,100 model calls): ~30 % of usage was after the PR opened (CI waits,
  wake-ups, merging main back in for the version line and the Resume block), ~29 % was the fixed ~72k-token prompt
  re-read on every call, and 698 calls were single shell reads. Each call re-reads the whole context.
- Why not keep `<Version>` per PR: every parallel PR conflicted on that one line. Why not squash: it rewrote history
  so stacked and merged-in branches conflicted again (the old §6 "Stacked PRs" recipe).
- Revisit if: auto-merge lands something a later review would have stopped, or releases need a version before tagging.

## D-080 · 2026-11-04 · Default timing and failure policy per step type live in config.yaml `settings.stepDefaults`
Status: accepted (design; user asked 2026-11-04). Design: docs/step-settings/DESIGN.md Part A.
- Decision: `settings.stepDefaults.<step id>: { timeout, retry: { count, delay }, onFailure }`, each field optional and
  resolved alone: the step's own value > the type default > the built-in (descriptor timeout, 0 retries, `stop`). The
  model keeps "not set" as null (`Node.OnFailure` and the retry fields become nullable; an explicit `stop` is now
  written), and `StepDefaults.Apply` fills a flow before the run so the Scheduler and runner are untouched. The
  inspector shows each field as Default or Set with a Reset link and a "Use these for all <type> steps…" button;
  Settings gets a "Step defaults" card with "Used by n steps".
- Why: the engine only reads config.yaml; a default that changes a run must be in the file the run is built from,
  survive the editor being closed, and be diffable. Inheriting (null) means a changed default reaches every step that
  never overrode it, which is the point. No schema bump: additive, and no key means the old behaviour.
- Why not editor.json: the engine can't read it (D-068). Why not copying the default into each step on creation: later
  changes wouldn't reach existing steps and every step would grow keys. Why not retry as one unit: "Wait between tries"
  alone would turn "Try again" into Set. Why not per area or per flow: the user asked for step types; an area mixes very
  different operations (Audio: set default vs volume). Why not resolve in the mapper: save would lose which fields were set.
- Revisit if: users want per-flow defaults (then a middle level between step and type).

## D-081 · 2026-11-04 · "Only if" is a per-step condition and checks are step types; both read one shared read-only probe
Status: accepted (design; user asked 2026-11-04). Design: docs/step-settings/DESIGN.md Part B.
- Decision: providers may implement read-only fact probes (`bluetooth.connected` …). A step's `only:` (a scalar id from
  its descriptor's `Conditions`; absent or `always` = run) skips the step when unmet; a check step type per area
  (`bluetooth.check`, `audio.check`, `process.check`, `service.check`, `file.check`, later `display.check`, `android.check`;
  `flag.check` stays) fails when the fact isn't what was chosen. A condition-skipped step ends **Succeeded with a note**
  (not `Skipped`, not `NotNeeded`): `success` dependents run, `failure` fallbacks don't fire, the flow is Done. A probe runs
  when the step runs, once, after the resource lock, inside the attempt's timeout; if it can't answer, the step runs.
  Dry run assumes conditions are met; "Test this step" evaluates for real.
- Why: "skip straight to the next step when already connected" must keep dependents running, which `Skipped` (skips
  propagate, D-003) can't do, and `NotNeeded` is fallback bookkeeping (D-065). A failing check reuses failure edges,
  retries and policies, so branching and "wait until connected" need no new runtime. One shared probe keeps the two
  from disagreeing; the cost is one read per use.
- Why not only a check step: "Connect unless connected" would need a check, a failure edge and a fallback hop on every
  step, and the failure would read as red. Why not only `only:`: it can't stop or branch. Why not a new outcome that fires
  `success`: every consumer (pop-up, Runs, canvas, tray) would learn it, for what a note already says (D-065 made the same
  call). Why not skip when the probe can't tell: a wrong skip silently leaves the PC in the wrong state; a needless run
  repeats an idempotent step. Why not poll or subscribe to state: idle = zero.
- Revisit if: users want a step to depend on another area's check (extend `only` to `{ check, with }`).

## D-082 · 2026-11-04 · Branching stays "check + failure edge"; no if/else, no expressions
Status: accepted (design). Design: docs/step-settings/DESIGN.md B9.
- Decision: M14 adds no conditional edges. A choice is a check step with a `success` dependent and a `failure` fallback
  (D-024/D-065); the pop-up gets "X isn't connected, so Y ran instead" wording for checks.
- Why: the model already branches this way and the editor draws and validates it; a second mechanism would need its
  own validation, canvas states and wording for the same flows.
- Why not `if:`/`else:` blocks or an expression language: new graph semantics (joins after branches, skips) for no scenario
  the user has. Why not conditions on edges: every edge editor and the scheduler's edge logic would change.
- Revisit if: a real flow needs three or more ways out of one check, or a condition across two checks.

## D-083 · 2026-11-04 · Dropping a step on another step's left third runs it before that step
Status: accepted (design; user asked 2026-11-04). Design: docs/step-settings/DESIGN.md Part C.
- Decision: new `DropZone.RunBefore` (left third) and `FlowEdits.RunBefore`: the dragged step takes all of the target's
  incoming edges with their kinds, the target waits only for the dragged step; the dragged step's old incoming edges are
  replaced, its dependents stay as they are; loops refuse; one undo step; context-menu/Outline "Run before…" is the
  keyboard route.
- Why: it mirrors `RunAfter`, which the user already uses, so the two ends of a step behave alike. Moving the incoming
  edges (not just the success ones) keeps a fallback a fallback.
- Why not only the target's success edges: a `failure` link would stay on the target, which then runs both after the
  dragged step and as the fallback. Why not re-linking the dragged step's dependents to its old predecessors (as Delete
  does): `RunAfter` and the right-third drop don't, and silent extra edits surprised the user before (D-058).
- Revisit if: users expect the dragged step's dependents to follow it.
## D-084 · 2026-11-04 · Several selected steps edit their shared settings together, as one Undo step
Status: accepted (design; user asked 2026-11-04). Design: docs/step-settings/DESIGN.md Part D.
- Decision: in `InspectorMode.Many` the inspector shows the settings every selected item has: "If it fails" always; Timing,
  "Only if" and params for steps of one type; params across types only when name, kind and library kind match. A field whose
  values differ (or are partly inherited) shows **Mixed**; setting writes **Set on all**, Reset removes the field from all;
  text applies on commit; one `UpdateDraft` = one Undo; validation runs per step and the change lands on all or none.
- Why: the M14 rows already say Default vs Set per field, so "Mixed" is the only new state; one type keeps the form the
  user already knows, and the common subset across types is small and safe.
- Why not the union of settings across types: a value would silently apply to a step that doesn't have the field.
  Why not "first selected wins" instead of Mixed: hides the difference and overwrites it on any touch. Why not per-key
  apply for text: dozens of Undo entries for one word.
- Built (T15.3): params in Many mode skip lists, maps, flow pickers (`flow.run` inputs are per step), display layouts (captured per step) and params with suggestions (listed per step), and are shared only when name, kind, library kind, required, default, range and choices all match (Copilot review); while Mixed, an empty write is ignored (an editable box echoing its empty text on focus must not wipe differing values), so pick or type a value first, then clear.
- Revisit if: users want to copy a step's settings onto others (a paste-style command).

## D-085 · 2026-11-04 · Shortcut keys: the number pad's * + - . / too; Num Enter never
Status: accepted (user, 2026-11-04: no numpad on the dev PC, asked for the operators); built T14.13. Extends D-071.
- Decision: `Hotkey` also takes VK_MULTIPLY 0x6A `Num*`, VK_ADD 0x6B `Num+`, VK_SUBTRACT 0x6D `Num-`, VK_DECIMAL 0x6E
  `Num.`, VK_DIVIDE 0x6F `Num/`, with the digits' rules (may stand alone, Shift refused, `IsNumberPad` true). Parsing
  accepts `Numpad…` and words (`NumDivide`, `NumPlus`, `NumStar`, `NumPeriod`…), any case, spaces ignored; a trailing
  `+` after Num/Numpad is the plus key (`Win+Num++`). The engine's `RegisterHotKey` takes the VK, so it needed no change.
- Num Lock: the operators arrive as themselves either way; `Num.` is Delete with Num Lock off, so the recorder now
  reports H3 for the pad's non-extended Delete instead of clearing the box.
- Why not Num Enter: it is VK_RETURN with the extended flag, which `RegisterHotKey` can't see, so it would register
  plain Enter. Left out (a hook is ruled out, D-071).

## D-086 · 2026-11-04 · No controller chords: a pad only drives the open menu, and the pad source runs only while one is open
Status: accepted (user, 2026-11-04); supersedes D-057 and the chord parts of D-056. Built on feat/remove-gamepad-chords.
- Decision: removed the `gamepad:` flow key / `Flow.Gamepad`, `GamepadChord` (only the `GamepadButtons` flags stay),
  `GamepadChordRecognizer`, `GamepadTable`, the editor's "Controller buttons" recorder and trigger text, `record-gamepad`,
  chord validation and clash checks, the sleep/resume wiring, and the 10 Hz chord poll. Kept: the menu driven by a pad
  (D-pad/stick move, A run, B close; `MenuModel`'s pad path and released-since-open), XInput at 60 Hz **only while a menu
  is open** (the first listener starts the reader, closing the menu stops it), `DeviceArrival` while it runs, and
  `dctl devices gamepads` (a one-shot enumeration, useful for diagnosis). The "Controller menu" template has no trigger.
- Compatibility: a `gamepad:` key in a file from v0.40.0 loads: one warning on the flow ("chords were removed"), the key
  is ignored and not written back (D-021, D-023).
- Why: the user found button chords impractical: Steam Input remaps buttons per pad, the recorder can't see a pad
  Steam owns, and mapping across pads is too complex. A pad button mapped to a key in Steam works as a shortcut (D-071,
  the F13–F24 keys) with none of that code.
- Why not keep chords as an option: untested on real pads (T12.6's live legs never ran), a second backend consumer, and
  the idle cost (10 Hz poll, arrival and power hooks) for a feature nobody will use. Also removes T12.10's Steam recipe
  for chords; the README explains the shortcut route instead.

## D-087 · 2026-11-04 · Flow groups in the list are the tray folders: one concept, no new config key
Status: accepted (user, 2026-11-04)
- Decision: a group *is* `tray.folder`. The list shows groups as expandable headers; creating, renaming, ungrouping and
  moving write `tray.folder` on the members. No `groups:` key, no schema bump, no migration. A group exists while it has a
  member (no persisted empty groups). Flow settings' "Tray folder" is renamed "Group" ("Also the folder in the tray menu").
  Flows hidden from the tray (`show: false`, Off) can still be grouped; the list dims them.
- Why: the user asked whether to merge; the data already has it (folder = a submenu, placed at its first member), the tray
  needs no change, and one name for one thing means the list never disagrees with the tray menu.
- Why not list-only groups (`group:` field): two things to keep in step, a flow could sit in "Audio" in the list and "TV" in
  the tray. Why not a top-level `groups:` list: lets empty groups exist that the tray cannot show, and a second source of truth.
- Revisit if: users want list groups that differ from tray folders, or nested groups (the tray menu would need nesting too).

## D-088 · 2026-11-04 · List order is the tray order; a reorder writes the displayed order; sort and filter are view-only
Status: accepted (user, 2026-11-04)
- Decision: the list shows what `TrayMenu.Build` shows (groups at their first member's place, members in file order). Drag,
  Alt+↑/↓/← and "Move to" call pure `FlowListModel` operations and write the whole displayed order to `flows:` (autosaved,
  Undo stack of 20 cleared as D-076). Sort (Name, Last run), search and filters only change the view and turn dragging off;
  "Make this my order" writes a sort on purpose.
- Why: `flows:` order already is the tray order, so a reorder needs no new field; writing the displayed order makes a
  hand-scattered group contiguous without changing the tray, and every picker follows. A view that silently rewrote the file
  would change the tray menu by looking at it.
- Why not `ListView.CanReorderItems`: no cross-section reorder with groups, replaces `FlowCard` (D-070). Why not a separate
  `order:` list: a second source of truth that the engine would have to read.
- Revisit if: the drag can't be made reliable on `ItemsRepeater` (then a prototype result goes to DEADENDS first).

## D-089 · 2026-11-04 · Row facts come from the config and the history op; relative times refresh on events, not on a timer
Status: accepted (user, 2026-11-04)
- Decision: a row shows shortcut (first, "+n"), step count, last run (result + relative time), and icons for not-in-tray and
  desktop shortcut; "Used by n flows" and all shortcuts are tooltips. Last run uses the existing `history` op with `Limit`
  100 (newest per flow); a flow with no record says "No recent run". Times are recomputed when the page shows, the window
  activates and on run events; nothing ticks.
- Why: every fact is already in `Configuration`, `RunRecord` or the desktop store: no engine change, no new IPC. A timer
  would break "idle = zero" for no gain; "Not run yet" is false once a run falls out of the 100-run history (the engine's `RunHistory.Capacity`; the file holds up to 200 lines before it is cut).
- Why not a `lastRuns` op or storing last-run data in config.yaml: new surface for a cosmetic fact; config would change
  on every run.
- Revisit if: a user needs run counts or a longer history (then a persisted per-flow summary, engine-side).

## D-090 · 2026-11-04 · List selection is additive: a plain click still opens; Ctrl/Shift+click and a Select toggle select
Status: accepted (user, 2026-11-04)
- Decision: Ctrl+click toggles, Shift+click selects a range, Ctrl+A all visible, Ctrl+Space toggles the focused card, Esc
  clears; a Select command-bar toggle shows checkboxes. A selection drags together and takes Group…, Move to, Turn on/off,
  Delete (D-076 for many) and Duplicate; Run stays single. Duplicate clears shortcuts (D-072 clash) and lands after the original.
- Why: D-070 made a click open the flow and Enter/Space is the keyboard open; selection must not change either, so it hides
  behind modifiers and an explicit mode (touch, discoverability).
- Why not always-visible checkboxes: noise for the usual one-flow case. Why not bulk Run: several flows at once fight for
  the same devices (resource locks) and have no clear stop.
- Revisit if: users expect click-to-select (then make the card's body select and move "open" to a button).

## D-091 · 2026-11-04 · No Narrator/screen-reader work; no live audio run
Status: accepted (user, 2026-11-04)
- Decision: no new Narrator / screen-reader work: T10.5 (pop-up, issue #26) and T12.9 (menu UIA announcements) are dropped,
  as are Narrator checks in T12.10 / UX §5.9 M12 and flow-menu DESIGN §3.3/§4. Already-built names (list, Outline, recorder)
  stay as they are. The DESIGN §1 B live audio run is not wanted: it changes the user's default audio device.
- Why: the user does not use a screen reader and wants usage spent elsewhere; the live run touches their real audio setup.
- Supersedes: the "Parked: pop-up for Narrator" note (user, 2026-11-03) and the Narrator part of the flow-menu design (D-056 area).
- Revisit if: a user needs screen-reader support.

## D-092 · 2026-11-06 · App names over adb: the literal label, else a known-name table, else the package id in words
Status: accepted (design docs/android-start-app/DESIGN.md)
- Decision: the picker's names come from `cmd package query-activities` (launcher + leanback launcher): the literal
  `nonLocalizedLabel` when set, else a built-in table of well-known package ids, else the package id's last meaningful
  segment capitalised. The package id is always shown too and is what the flow stores.
- Why: adb cannot resolve `labelRes` resource ids (checked on an Android 14 box: no `dumpsys` output has labels);
  most apps only have a resource id. A guess shown next to the exact package id is enough to pick from.
- Why not pull the APKs and read `resources.arsc`: tens of MB per device per refresh, a binary-resource parser to
  maintain, slow on Wi-Fi adb. Why not `pm list packages` alone: includes apps with nothing to launch.
- Revisit if: the guessed names bother the user (then the parser is the only real fix, for the few APKs picked).

## D-093 · 2026-11-06 · The app list is cached on disk per device, refreshed only when the user focuses the box
Status: accepted
- Decision: the engine answers `android-apps` from `%LOCALAPPDATA%\DazeControl\android-apps\<serial>.txt` and only
  runs adb when the request says `refresh`; a successful listing rewrites the file. The editor refreshes once when the
  app box gets focus. No timer, no resident worker.
- Why: Idle = zero (D-009); adb connect and list take seconds and the TV may be off; the last list is still useful.
- Why not refresh on every step selection: adb traffic to the user's device just from clicking around.

## D-094 · 2026-11-06 · `android.start`: an `am start` by package, with a searchable picker, free text allowed
Status: accepted
- Decision: step `android.start` (`device`, `app` = package id): `am start -a MAIN -c LAUNCHER -p pkg`, then the
  LEANBACK_LAUNCHER category; failure is decided from the answer text (am exits 0). The inspector shows an AutoSuggestBox
  (filter by name or package, Enter on unmatched text keeps it as the package). Suggestions never restrict the value (D-050).
- Why: no activity name needed, works for phones and TV boxes; same adb path, retries and lock as the other Android steps.
- Why not `monkey -p`: prints "No activities found" with exit 0 for TV-only apps and injects events.
- Revisit if: an app has several launcher activities and the user wants a specific one.

## D-095 · 2026-11-06 · Editor Back/Forward: one history per window, fed by the page and the open flow
Status: accepted (user asked for touchpad and mouse back/forward, 2026-11-06)
- Decision: `NavigationHistory` (50 places, no repeats, a new place drops Forward) in `ShellViewModel`. A place is a page,
  or Flows + the open flow's id; it is recorded from `Page` and `Editor` changes, so every route (nav pane, breadcrumb, Used by,
  Runs, "Open this flow") is covered without touching the pages. Opening flow B from A is one step (no list between).
  Triggers: mouse XButton1/2 (pointer event, handled-too), `WM_APPCOMMAND` back/forward (touchpad gestures, subclass), Alt+Left/Right
  (Alt+Left not while a Flows card has focus: it moves it out of its group, D-088). One click can arrive as both pointer and
  APPCOMMAND: a second one in the same direction within 300 ms is ignored. Back/Forward ask the D-047 prompt first (Cancel stays); entries whose flow
  was deleted are skipped; a rename follows (D-075). No on-screen button: none existed, the tray-less editor has the breadcrumb.
- Why not restore selection and scroll: no cheap hook in the canvas; a flow opens fresh as it does today.
- Revisit if: users want a visible Back button or scroll/selection restore.

## D-096 · 2026-11-06 · `android.connect`: the existing library device and connect, as its own step
Status: accepted (user, 2026-11-06: stop running `adb connect` from a PowerShell script)
- Decision: step `android.connect` (`device` = library Android device, which already holds host, port default 5555 and adb path)
  runs `AndroidRemote.ConnectAsync`: `adb connect`, up to 3 tries, success judged from the answer (`connected to` /
  `already connected to`, never `failed`; adb exits 0 either way). `failed to authenticate` adds "accept the debugging prompt".
  Same `adb:{device}` lock, 20 s timeout, process-tree kill and dry run as the other Android steps.
- Why: no new host/port fields, no second place to remember devices; the other steps already connect, this one is for flows that
  only need the link up. No pairing (`adb pair` needs a one-time code; codes never go in YAML or logs, D-014).
- Why not a disconnect step: not asked; add it the same way if wanted. No "Only if" check: T14.9 is parked.

## D-097 · 2026-11-06 · Editor motion: shared tokens, built-in transitions, nothing continuous
The configuration pages (inspector, Settings, Many mode) no longer snap. `Motion.Animate` (attached property on a panel, in `Motion.cs`) gives a panel `RepositionThemeTransition` on its children (neighbours slide) plus an implicit fade-in when a child turns visible; `Motion.AnimateItems` gives an ItemsControl add/delete + reposition transitions; selecting another step fades the pane (`Motion.Swap`, opacity only). Tokens in `MotionTokens` (fade 120 ms, swap starts at 0.4 opacity; slides use WinUI's own reposition timing). Rejected: per-step patches (every step type would need them); hide animations (delay Visibility, break focus/click); scale or offset on the pane (moves inputs under the pointer, blurs text). The Windows "Animation effects" setting (`UISettings.AnimationsEnabled`) is read when a panel loads or a swap starts (no listener); off or unreadable = no animation at all. All animations are one-shot composition animations: nothing runs while idle.

## D-098 · 2026-11-06 · The editor window remembers its place: `editor.json` › `window`, saved on close
Status: accepted (user, 2026-11-06: big flows meant resizing every launch)
- Decision: on close (`AppWindow.Closing`, before any "unsaved" prompt) the editor stores the restored rectangle
  (`GetWindowPlacement`, workspace → screen pixels), the maximized flag and the window's DPI under `window` in D-068's
  `editor.json`; the store's existing write on exit persists it. No timer, nothing on resize. At start
  `WindowPlacementLogic.Resolve` (pure, tested) picks the rectangle: the one monitor it overlaps most by ≥ 120×48 px,
  size rescaled if that monitor's DPI differs from the saved one (size stays the same in DIP), clamped into its work
  area. Off every monitor, smaller than 640×420 DIP, or unreadable → the 1100×720 DIP default centred on the current
  monitor (the maximized flag still applies). The window is moved/sized before the first `Activate`, then maximized.
- Why: editor closed = not running (idle = zero), so the geometry must outlive the process. Reusing `editor.json`
  keeps one per-PC view-state file; config.yaml holds flows the user may share, and writing it makes the engine reload.
- Why not `SetWindowPlacement` on start: it can't clamp to a changed monitor layout or rescale for DPI.
- Other windows: the editor is one window; dialogs are `ContentDialog`s sized by content, nothing to remember.

## D-100 · 2026-11-06 · Controller menu: the number pad works as a d-pad (Steam Controller reaches us as keys)
Status: reverted 2026-11-06 (user): the Num keys were not the cause (D-101) and the user fixed it in Steam.
- Finding (user's PC, Steam running): no XInput pad in slots 0–3 (`dctl devices gamepads`: none), no HID gamepad or virtual
  Xbox pad; the Steam Controller exists only as a Bluetooth LE device (`BTHLE`, "Steam Ctrl (BT)"). As D-056 found, it is
  never an XInput/GameInput pad on the desktop: Steam turns it into keys/mouse. The pad reader is not at fault.
- Cause: the user's Steam layout sends number-pad keys (Num Lock is on), and the menu registered only arrows, Enter, Esc, Tab,
  Home/End and the top-row 1–9.
- Decision: the open menu also registers Num8/2/4/6 (move), Num7/Num9 (previous/next), Num5 (run), Num0 (close).
  Num Enter is Enter already. Registered only while a menu is open, like the other keys. Needs Num Lock on.
- Not done: raw HID / Bluetooth reading of the Steam Controller (Steam owns it; needs its protocol, idle cost, fragile).

## D-101 · 2026-11-06 · Steam Controller and Big Picture: documented limit, no second input path
Status: accepted (user report: the menu works from the Steam Controller until Steam Big Picture is open). Corrects D-100's cause.
- Finding (user's PC, Big Picture open and foreground, 2026-11-06): still no XInput pad in slots 0–3, `dctl devices gamepads` none,
  no HID/virtual pad; only the Bluetooth LE controller and Steam's "Enhanced Features" driver. So the Steam Controller is
  invisible to us with Big Picture open too: it works as keys only through Steam's desktop layout, and Big Picture swaps that
  layout for its own UI navigation, so no keys reach the menu. Nothing to fix in the pad reader. D-100's Num keys stay (harmless
  extra), but they were not this problem.
- Decision: document it (README known limits). No raw BLE/HID reading of the Steam Controller (Steam owns the device, its
  protocol is undocumented, and it would add an idle cost). Not verified: Steam's own UI for changing Big Picture's layout.


## D-104 · 2026-11-07 · Automatic backups: setting in `backups\auto-backup.txt`, gate from file names
Status: accepted (follows D-103 choices 4-5)
- Decision: the on/off switch and count (default on, 10, 3-50) live in `%LOCALAPPDATA%\DazeControl\backups\auto-backup.txt` (`enabled=`, `keep=`), read by the editor and `dctl`. Triggers: `dctl update` when a newer version was found (before the download; no gate), and after every successful editor save, on a background task, only if the newest backup in the folder by file name is under 24 h old (`Core/Backup/AutoBackup`). Never throws; a failed backup is logged and the update or save goes on.
- Why: `dctl` and the editor must agree, and neither can read the other's store (editor.json is editor-only). Not config.yaml: it would change the engine's schema, show up in restores and trigger a hot reload for a per-PC policy.
- Why not a timer or watcher: idle = zero (D-009). The editor's own update download does not back up (the engine applies it at start; `dctl update` is the scripted path).

## D-103 · 2026-11-06 · Backup and restore: one zip, byte-for-byte config.yaml, restore through the normal loader
Status: accepted (user, 2026-11-06, all open choices as recommended; docs/backup-restore/DESIGN.md)
- Decision: `.dcbackup` = zip (`System.IO.Compression`) with `manifest.yaml` (format, app and config versions, SHA-256 per entry, reason), `config.yaml`
  byte for byte, optional `editor.json` and `history.jsonl`. Never secrets (D-014: only credential names; restore lists names missing in Credential Manager).
  Logic in `Core/Backup/BackupService`; zip work runs in the editor and `dctl backup|restore`, never resident in the engine (no timer, no new engine path, D-009).
  Automatic copies only at existing events (before update, after an editor save, at most daily; newest 10). Restore: preview, Replace or Add-missing, safety copy
  first, one atomic config.yaml replace, the engine's hot reload judges every flow (D-021), Undo from the safety copy. An unreadable backup config disables Restore.
- Why: the repo already writes only config.yaml for the engine; copying it verbatim keeps hand comments and needs no schema; the normal loader is the one fail-soft path.
- Why not a folder of files: harder to move. Why not encrypt or DPAPI values: no secrets are in the file (D-014). Why not a scheduled/periodic backup: idle = zero.
  Why not an engine-side zip: footprint budget. Why not rewrite machine-specific values on restore: wrong guesses; list them instead.
- Revisit if: credentials must roam (D-014's trigger), per-flow export/import (M16 "Out") is wanted, or config grows past a few hundred KB.

## D-102 · 2026-11-06 · Step vocabulary: verb + bare object, themed palette groups, ids stay
Status: accepted (user, 2026-11-06: all 12 open choices of docs/steps-review/REVIEW.md as recommended). Choice 9 = no id renames; 7 = add Close program, Stop app (Android), Turn screens off, the "Wait until" recipe and more PowerShell examples; 8 = every "maybe later" item stays later, HDR and non-audio Bluetooth spikes are no for now.
- Decision: palette titles are an imperative verb plus a bare object (no article, group name not repeated: Android → "Connect", "Start app");
  checks are "Check <noun>" and last in their group; palette groups are themed (Audio, Bluetooth, Displays, Android, Programs, Services, Files,
  Web, Flow) through a display mapping, not the id prefix; choices get captions in the editor. Step ids and param names are **not** renamed.
- Why: titles, groups and captions are not saved, so the whole pass costs no migration; ids are what old flows and backups contain.
- Why not rename ids to a perfect `area.verb`: needs an alias table kept forever (old backups) for no user-visible gain.
- Revisit if: a new step cannot follow `area.verb`, or the user hand-edits YAML a lot.

## D-104 · 2026-11-07 · No layout jumps: reserved slots, overlays, animated slides
Status: accepted (user, 2026-11-07: "I don't want UI to jump without a strong reason, in any sense, in any place")
- Decision: the flow editor's "Not saved" text is a dot in a fixed 12 px slot (opacity fade, `OpacityTransition` with the D-097 fade token, tooltip + accessible name "Unsaved changes"; Save's tooltip and enabled state say it too). Status that toggles gets a reserved slot (Restart now, Make this my order: disabled and transparent/disabled instead of collapsed) or overlays free space (the run result bar over the canvas). The Flows page panel uses `Motion.Animate`, so bars above the list slide it instead of snapping. Exceptions are listed in UX.md "No layout jumps".
- Save's enabled/accent state, the dot and the tooltips come from one value: the session draft (`HasChanges`) or an inspector box whose text differs from its text on entry. Before, "a text box has focus" alone kept Save on (blue on a mere click into Program/Arguments; stuck blue after Save when the focus event raced the save). Focus is never a reason.
- Why not text in a reserved-width label: still reads as noise at the top of the editor; a dot plus the enabled accent Save says the same.
- Why not reserve space for every field error: every inspector would grow a permanent gap; the panel already animates them (D-097).
- Left alone: the top-of-page error/conflict bars (rare, must be seen), Run to Stop swap, the Flows selection bar (user action, animated).
- Revisit if: the user still notices a specific jump: add it to the reserved-slot list.

## D-105 · 2026-11-07 · Android media and sound steps: seven steps, percent of the device's own range, no toggles
Status: accepted (user via coordinator, 2026-11-07: "broader than key events"; the set is the session's judgment)
- Decision: Android group gets `android.media` (Control media: play/pause/stop/next/previous/fast forward/rewind, sent as media keys),
  `android.setVolume` (stream media/ring/alarm/notification/system, 0 to 100 %), `android.changeVolume` (up/down 1 to 30 times),
  `android.mute` (Mute or unmute, media stream), `android.setBrightness` (0 to 100 %, turns auto off), `android.open` (Open address, VIEW intent),
  `android.wake`; `android.keys` gets 19 more key names. Group order: Connect, Start/Stop app, Open address, Choose input, Send keys, media and
  sound, brightness, Wake, Sleep.
- Volume: read `[min..max]` with `cmd media_session volume --get`, fall back to `media volume`, then set round(percent of range). Nothing works =
  a plain "can't set its volume over adb (Android 8+)" failure, never a guess. Mute uses `cmd audio adj-mute|adj-unmute 3` (newer Android; `media_session --adj` only knows raise/lower, so it is the second try),
  because the mute key is a toggle and flows should be repeatable (D-081 style).
- Why not: a percent-only guess of the maximum (devices differ: 7, 15, 25, 100); a "Toggle mute" step (not repeatable); a Media group in the
  key box (it is a free-text suggestion box; names are prefixed `media_`).
- Not done: reading the volume back as a check; per-app volume; install/type text (D-102 "No").
- Not run on a real device (adb output formats are from AOSP source); the first live run is the check.

## D-106 · 2026-11-08 · Flow shortcut icons: the engine puts them back at each start
Status: accepted
- Cause: Velopack's updater "updates" every .lnk on the Desktop/Start menu whose target is under the app root, and resets
  its icon to `current\DazeControl.exe` (velopack_DazeControlApp.log, 2026-11-07 15:11, 0.49 → 0.50). Our D-049 links
  target the stub, so they match.
- Decision: once per engine start, off the message thread, `DesktopShortcuts.RepairIcons` rewrites (same path and
  name) any of our shortcuts whose icon isn't its flow's .ico or whose .ico is gone. Shortcuts of flows no longer in the
  config are left alone. Copies in the Start menu (with Startup) and taskbar pins (`User Pinned`), searched with
  subfolders, are repaired too (user: "I definitely want Start menu to keep the icon"); listing and the editor stay
  Desktop-only.
- Why not: a target outside the app root (no stable exe there; the stub is what survives updates, D-049); a Velopack
  option (none turns this off per shortcut); only after `VELOPACK_RESTART` (a manual install or an editor-started
  engine after an update would miss it; the scan is a handful of .lnk reads).

## D-107 · 2026-11-08 · Triggers: schedules in the engine, lazy push event sources, prompts that never take focus
Status: accepted (user answers to docs/triggers/DESIGN.md §7, 2026-11-08)
- Decision: flows get a `triggers:` list (schedule, event; hotkey unchanged). Schedules run in the engine on one due-time
  timer (amends D-009: no periodic timers, one due-time timer allowed). Missed runs: chosen per schedule when it is created
  (`ifMissed: run|skip`, with a grace for run; no global default). v1 events: session lock/unlock/logon, power resume and
  AC/battery, device/audio/display changes, network/Wi-Fi; each a lazy push source. v1 prompts: ask-at-start inputs and
  `prompt.ask` Yes/No and choose, always with timeout and default; text/number later.
- Why: 0 CPU between runs and no OS objects to clean; prompts keep "never activates" (D-055).
- Why not Task Scheduler: leaves OS tasks and rebuilds pause/history/`whenRunning` outside the engine; revisit if waking the PC is wanted.
- Why not program start/stop events: needs WMI (X-011) or ETW (admin).
- T20.3: a trigger has no step, so M14 fact probes (they read a step's device or program) can't answer its `only`. `only` is
  a small context-free list (`TriggerText.Conditions`: `onBattery`, `onAC`; `GetSystemPowerStatus` when it fires); unknown
  ids warn (trigger ignored), a condition that can't be read lets the trigger fire (as D-081). Debounce is a timestamp
  compare from the latest event (2 s), cooldown per trigger (10 s), both without timers; an event whose `chain` holds the
  flow is refused by `FlowCalls.Guard`. Sources `Load`/`Unload` on the message window, registered only while subscribed.
- T20.12 (2026-11-08): network events are per connection (user's choice): a connection = an adapter Up with a gateway
  (Wi-Fi by SSID, Ethernet-type by adapter id); each one coming or going raises its own event with a kind and detail,
  filtered by `via: wifi|cable` / `ssid:`; cooldown per trigger + detail. Why not one "PC online" state: a cable under live
  Wi-Fi changed nothing, so it never fired. Cellular/PPP/VPN aren't connections (a VPN shouldn't read as "a network came").

## D-108 · 2026-11-09 · Update channel: Stable or Release candidates, a per-user file, no automatic downgrade
Status: accepted (user, 2026-11-09: opt in to release candidates from the app)
- Decision: `UpdateChannel` Stable (default) or ReleaseCandidates, kept in `%LOCALAPPDATA%\DazeControl\update-channel.txt`
  (`channel=stable|rc`). `VelopackUpdateClient` passes `prerelease: true` on the RC channel. Set from About › Updates ("Updates to
  follow", a drop-down that saves at once) or `dctl update --channel stable|rc`; the engine's daily check, `dctl update` and
  the editor all read the file. Switching to Stable never downgrades: an RC newer than the latest stable stays until a newer
  stable ships (Velopack orders `0.52.0-rc.1 < 0.52.0`; `AllowVersionDowngrade` stays off). UI words: "Release candidates"
  and "Stable", never "pre-release" or "semver".
- Why a small text file: same reasons as the auto-backup switch (D-104): per PC, so a restore, a hot reload or a config copied
  from another PC never moves this PC onto RCs; readable by `dctl` without the editor or the engine running.
- Why default Stable: an RC may have rough edges; the user opts in. Unreadable file = Stable.
- Why no downgrade: a downgrade replaces a newer install with an older one and can leave config the old version can't read
  (a newer schema); the user can reinstall an older Setup.exe by hand.
- Why not: a key in config.yaml (a restore would flip the channel); the registry (not visible next to the other per-user files);
  a per-release channel name in Velopack (`vpk --channel`: RCs and stable would need two feeds and a stable could not supersede
  its RC); a Settings-page row (the Updates card already lives in About, two places would drift); a radio group (a drop-down
  matches the other Settings rows and reads well at 225 % text).
- A download already waiting still installs after a switch (it was chosen under the old channel; Restart/next start applies it).

## D-109 · 2026-11-09 · Updates re-check right before they apply
Status: accepted (user report: the update installed the version found at check time, not the newest)
- Decision: `IUpdateClient.RefreshBeforeApplyAsync` looks at the feed again (current channel, D-108) and downloads a release newer than
  the downloaded one; offline or failing it keeps the downloaded one. Called by the editor's "Restart now" and by the engine's apply at
  start (30 s cap, so a bad network can't hold the start). `dctl update` already checks fresh, and the daily check stays the only timer.
- Why not apply at once and let the next daily check catch up: the user restarts to get the newest, then has to repeat the cycle.


## D-110 · 2026-11-09 · Editor: Check only looks, Install fetches the newest and says when it changed
Status: accepted (user, 2026-11-09). Refines D-043/D-109 for the editor card.
- Decision: About › Updates has "Check for updates" (finds, downloads nothing, always available) and "Install" (re-finds the
  newest, downloads with a % in the status, applies, restarts). If Install finds a newer release than the one shown it stops
  and says "Version 2 is out, newer than version 1. Install now installs version 2, not 1."; the next click installs it.
  Offline, an already downloaded update still installs. The engine's daily check still downloads quietly and the engine start
  still applies (with D-109's re-check), so updates need no editor.
- Why not install the newer one at once: the editor closes right after, so the notice would never be read.

## D-111 · 2026-11-09 · A long edge detours round the steps it would strike through
Status: accepted (fix for a user report: check → restore → show message plus check → show message drew through restore)
- Decision: `FlowLayout` gives an edge that skips columns and would cross a pill (sampled along its drawn line, Curved or
  Right angles) a lane just above or below every box between its ends (nearer side, 14 DIP clear). It reuses the wrapped
  edge's orthogonal shape (`EdgeRoute.WrapLaneY`: out 16, along the lane, down 16 before the port), so hit-test, chip and
  "+/×" controls need no new code; the extent grows to hold the lane. Clear lines stay Bézier curves.
- Why: D-020 means steps can't be moved by hand and no Layout command could ever resolve it, so the router must.
  Why not dummy-node layers: every long edge would widen the flow by a column of empty slots.
- Revisit if: several detours stack on one lane and overlap (lane per edge), or lines should dodge other edges.

## D-112 · 2026-11-09 · A join after a check and its fallback runs once if either branch fires
Status: accepted (user's bug: `show message` after `check` (success) and `restore layout` (the failure fallback) never ran)
- Decision: in the Scheduler an incoming edge that can't fire because its source is an **untaken branch** (a failure handled
  by a failure edge, a skip excused by it, or a NotNeeded fallback) no longer skips the step. Once every edge has settled the
  step runs if at least one fired, and is skipped only when none did. Dead edges from real failures and plain skips still skip
  at once (D-003 AND), so `bt` fails → its plain dependents skip as before.
- Why: with AND, the two edges of such a join are mutually exclusive, so it could never run.
- Why not OR for every edge: a failed or skipped upstream step must still skip what depends on it ("Skip what depends on it").
- Open (not changed, needs the user): a "May fail" step with a failure edge fires BOTH its success and failure edges (D-025,
  pinned by tests). A check set to "Keep going" therefore shows both messages when it fails, which matches the wake-from-sleep
  report (devices not ready right after resume). Proposal: with a drawn failure edge, "May fail" fires only failure.

## D-113 · 2026-11-09 · Inspector value controls ignore the mouse wheel
- `WheelGuard.IsEnabled` (attached, no reflection) is set by implicit styles for ComboBox, NumberBox, Slider and ToggleSwitch in InspectorPane.xaml: the wheel is marked handled and scrolls the nearest scrollable ScrollViewer; an open combo box keeps it for its list. New config controls inherit it with no markup.
- Group header right-click opens its menu at the pointer (Menu key / Shift+F10 still anchor to the "…" button). The nav hamburger is now the first rail item ("Menu", `SelectsOnInvoked=False`, toggles `IsNavPaneOpen`) with the built-in toggle hidden: stretching the template's `TogglePaneButton` in code left it floating in the content area (2026-11-11).
- NumberBox has no keyed default style (`DefaultNumberBoxStyle` crashed the editor on opening a flow); its style has no BasedOn, and `XamlStyleKeyTests` checks every BasedOn key against WinUI's generic.xaml.
- Only checked by building: run `scripts/dev-run.ps1` to see the wheel, the menu position and the hamburger row.

## D-114 · 2026-11-09 · Failure options renamed: "Skip what follows", "Run the next steps anyway"
Status: accepted (wording only; supersedes the names in D-024/D-025/D-059; YAML `stop`/`skip`/`ignore` and runtime unchanged)
- Decision: the per-step "If it fails" options read "Stop the flow" · "Skip what follows" (was "Skip what depends on it") ·
  "Run the next steps anyway" (was "Keep going"); the canvas chip "May fail" is now "Failure ignored". Each description says what
  happens to "When it works" and "Only if it fails" links: Skip drops only the first, Run anyway runs both (D-025).
- Why: a user picked "Keep going" meaning "skip the dependents", and a failed check ran both messages on wake from sleep.
- Revisit if: users still confuse the second and third option.

## D-115 · 2026-11-09 · Canvas: Arrange as left to right, top to bottom, tree or circle; drag a step anywhere
Status: accepted (user, 2026-11-09: "moving the actions where I want… tree, circular… familiar to people using similar software")
- Decision: Layout › **Arrange** picks the shape (`LayoutShape`): Left to right (the column layout, default), Top to
  bottom (its columns as rows), Tree (each step under the step it waits for that runs last; parents centred over their
  children), Circle (run order, clockwise from the top). `Core/Layout/FlowPlacement` derives every shape from
  `FlowLayout.Arrange`, so sizes, cards and run order stay one code path; all shapes are deterministic.
- A drag dropped on empty canvas places the dragged step or card, with the rest of the selection, on an 8-DIP grid
  (the dragged one snaps, the rest keep their distance). The first move freezes every step where it is (n8n,
  Node-RED): nothing else shifts. A step with no place yet (added, pasted) lands one gap past the step it runs after,
  pushed down clear of the rest. Drops on a step still mean Run after / before / together; a member still reorders in its card.
- Lines leave by the shape's own side while the target lies ahead (right → left; bottom → top in top to bottom and
  tree), else by the sides facing each other (`EdgeRoute.StartSide/EndSide`); arrowheads follow. Ports sit
  bottom/top in the vertical shapes.
- Stored per flow in editor.json (`FlowViewSettings.Shape`, `Placed`: top-left by id), never in YAML (D-068).
  Moves, Arrange and Reset layout are **undo steps** in the canvas history (they can throw away hand placement);
  Wrap, Spacing, Line up and Untangle are off while steps are placed by hand; "Arrange again" lets go.
- Why not one-step "put back in line" or pins on single steps among auto-laid ones (PR #168): auto-laid neighbours
  move under a pinned step and overlap it. Why not YAML `pos`: every drag would hot-reload the engine, and
  diffs churn. Why not force/organic layouts: not deterministic, so a flow would look different on each open.
- Revisit if: users want positions to follow the config to another PC, or right-angled lines that route round steps between hand-placed ones.

## D-116 · 2026-11-11 · Bound devices: a Bluetooth key + an audio key used as one device
Status: accepted (user, 2026-11-11: "binding audio and BT devices - so that they could then be used as one device in the BT/audio steps")
- Decision: a new Library kind `LibraryKind.Bound` (`library.bound.<key>: { bluetooth, audio, name? }`, both are Library
  keys). Step settings keep one string: a Bluetooth setting takes a Bluetooth key **or** a bound key, an audio setting an
  audio key or a bound key; `Library.ResolveBluetooth/ResolveAudio` pick the side, so providers, locks (`FlowRunner`
  resolves the address) and `Contains` stay one code path. A bound key may not equal a Bluetooth or audio key (error on
  the bound item; Rename and new keys refuse it), so a key always means one thing.
- Fail-soft: a missing side is a **warning** on the bound item; only steps that need that side fail validation ("The
  bound device "x" has no audio device…"), the other side's steps still run. The editor keeps a side from being removed
  while bound; renaming a side follows into the binding; renaming the binding follows into steps and inputs.
- Reference model stays generic (kind + key): a run-time pick of a Bluetooth or audio item can return a bound key and
  every step resolves it the same way.
- Why not a flag on the Bluetooth item (`audio: technics`): the audio item then has no single owner and the audio
  pickers would need a second lookup. Why not new step params (`bound:`): every Bluetooth/audio step and every picker
  would grow a second setting. Why not one combined device type replacing both: Library Test, Add from Windows and the
  live lists are per kind, and other flows use the sides alone.
- Revisit if: users want more than two sides (a dock = display + audio + USB). A bound flow input kind: D-125.

## D-117 · 2026-11-11 · Pick device step: its result fills later steps' device settings as `$result:<step id>`
Status: accepted (user, 2026-11-11: "selecting items from the library that can be then used in other steps"); amended by D-129 (step values), built; Pick-only `$result:` superseded by D-130
- Decision: a `prompt.pick` step ("Pick device", Questions group) offers the Library's items of one `kind`
  (audio/bluetooth/display/android) on the prompt window (D-107: never takes focus, timeout, `onTimeout: stop|use-default`).
  Any `LibraryRef` setting, and a value `flow.run` passes as `inputs`, may hold `$result:<id>`; the runner substitutes the
  picked key just before that step runs (lock included). Validation: the id is a Pick step of the same kind that the step
  waits for (any edge, through cards). Result missing at run time (pick failed and was ignored) = the step fails.
- Why a step and not more `ask: true` inputs: inputs are asked before the run starts; the user wants to ask mid-flow
  (after a check or a connect) and possibly twice. Why the same `$` prefix as inputs (D-064): one place in validation,
  pickers and the runner already treats `$…` in device settings; `:` can't occur in input names, so no clash.
- Why not a generic result/variable system (text outputs, any step): nobody asked; device settings are typed and checkable
  before a run, free text isn't (D-064's reason). Revisit with text prompts (D-107 "later").
- Bound devices (D-116): an audio or Bluetooth pick also lists bound devices; `kind: bound` picks only those, and its
  result fits audio and Bluetooth settings (`PickResults.Fits`). Another Library kind joins with a `kind` word in
  `PickResults.KindWords` and rows in `InputPrompts.ItemsOf`.

## D-118 · 2026-11-11 · Twelve PC-state checks on the D-081 probe model; `dctl check` runs one
Status: accepted (user asked 2026-11-11: "different device checks, like what wifi is connected … at least 10")
- Decision: new read-only check steps, each a `FactCheckProvider` over a one-shot probe: `network.wifi` (any Wi-Fi or a
  named network), `network.cable`, `network.internet` and `network.metered` (Windows' own NLM hint via
  `GetNetworkConnectivityHint`: nothing sent, no polling), `network.vpn` (an up adapter: PPP or a known VPN driver word, or
  a given name), `power.source`, `power.battery` (`GetSystemPowerStatus`), `pc.idle` (`GetLastInputInfo`), `pc.locked`
  (`WTSQuerySessionInformation` SessionInfoEx), `display.count` (`SM_CMONITORS`), `file.space` (`DriveInfo`),
  `time.check` (days + optional From/To window, past midnight belongs to the start day). Their outcome is pass/fail, so
  branching, "wait until" (retries) and "skip what depends on it" work unchanged (D-082). `ProviderCatalog.Checks` is now
  what makes a step a check (palette order, Test result wording), not the `.check` id suffix: one area has several.
  Palette groups Network and This PC (power, pc, time). `dctl check <check> [name=value…]` sends one check as a
  test-step with config.yaml's Library; exit 0 = as asked, 1 = not. Wi-Fi names come from the WLAN API, shared with
  the network trigger (`Providers/Network/WindowsNetworkState`); the Wi-Fi setting suggests connected + saved networks.
- Why these: the conditions automation tools most often gate on (Home Assistant/Tasker/Shortcuts/Power Automate style:
  network, power, presence/idle, lock, time, screens, disk) that are readable without admin, COM residency or polling.
- Why not Bluetooth/audio/process again: they exist (D-081). Why not USB device present, lid/dock, foreground app,
  volume level: need CfgMgr name matching or COM reads with unclear user value; add when a flow needs them. Why not a
  `$result:` value for checks (D-117 style): a check answers yes/no, which failure edges already carry. Why not
  pinging a host for "internet": a request per run and a host to choose; Windows already knows.
- Revisit if: Windows 11 hides the SSID (location off) for many users: the Wi-Fi check then fails with that advice;
  an "Only if" on any step from these context-free facts (extend `only` as D-081 foresaw).

## D-119 · 2026-11-11 · Canvas: keep XAML views, add a live scene layer and a camera; no custom-drawn surface
Status: accepted (user, 2026-11-11: "as smooth as butter… as Miro"; investigation only, built in M21). Supersedes D-074's
"Why not an unbounded canvas": T21.7's spike passed the agent-checkable parts (2026-11-12: crisp text at 50/200 %, clip,
zoom at centre; real touchpad/touch input is the user's live check, its fail = revert T21.7 per Revisit); D-013, D-037 stand.
- Verdict: the drawing stack is not the problem (a pill is 10 elements, flows are 5–80 steps, a re-layout of 80 steps
  costs ~2 ms in the VM). The **interaction architecture** is: the canvas knows only committed layout, so nothing derived
  follows a gesture, and a commit is a full rebuild. Root causes (code as of fb3e371):
  1. Drag moves only the views: `FlowCanvas.OnPointerMoved` sets a new `TranslateTransform` per view; edges, ports, chips,
     focus ring and "+/×" read the VM's committed rects, which change only in `MoveBy` on release (symptoms 2, 3).
  2. Drop jumps: `OnPointerReleased` calls `ResetDragged()` (transform off: the pill is back at its old slot) **before**
     `MoveBy`; then `Canvas.Left/Top` change under `NodeLayer`'s `RepositionThemeTransition`, which slides it from the
     old slot. The preview isn't snapped, the commit is (8-DIP grid), so even the end point differs (symptom 1).
  3. Library drop ignores the pointer: `OnPaletteDrop` uses the drop point only to hit-test; `AddStep(step, after)` has no
     position, so the step lands where `FlowPlacement` puts unplaced steps; no landing ghost while dragging (symptom 4).
  4. A commit re-raises everything: `Sync()` re-arranges, recreates every edge VM and the outline, and calls
     `Update(…, "")` then `Update(…, waits)`, so each node raises `Badges` (new list) and `AutomationName` twice; each
     event runs a full `PillView.Refresh` (≈25 property sets, brush lookups). Measured: per drop, 18 node events on the
     7-step test flow, 319 on an 80-step flow (only 2 were real moves). Hover and selection rebuild every edge figure
     and recreate every chip (`DrawEdges`).
  5. Semantic zoom changes layout: below 60 % the summary/badge collapse, pills shrink, `ReportSize` re-lays out: steps
     shift while zooming (the code already fights the resulting re-fit flicker in `OnViewChanged`).
  6. Camera is a `ScrollViewer` + padding wrapper: async `ChangeView` with a pending view and a low-priority retry,
     a synchronous `UpdateLayout` per wheel notch, bounded pan; works, but every pan/zoom is a layout-and-scroll round trip.
- Decision: keep XAML pills/cards (UIA names, theme and high-contrast brushes, text) and batched edge paths, and add:
  (a) a pure **scene** in `Editor.ViewModels/Canvas` — item rects by id (dictionary + uniform-grid index for hit tests),
  ports, edge routes, and a **transient** layer (drag offset for a set of ids, palette ghost, connect preview) from which
  moved rects, ports and the routes of touching edges are derived (on a flow not yet placed by hand, from the frozen
  arrangement the first move commits, so wrap and detour lanes go at drag start, not on release) with the same `FlowPlacement.Sides/Port` +
  `EdgeCurve.For` code the commit uses, so **preview == commit**; (b) per-frame updates during a gesture only:
  pointer moves are coalesced to one update per frame (`CompositionTarget.Rendering`, subscribed for the gesture,
  removed at its end: idle stays zero), views move by `UIElement.Translation`, edges touching moving items go to a
  separate "live" path set rebuilt each frame, the rest stay untouched; (c) commit without a jump: commit first, then
  zero the translation in the same tick, no theme transition on hand-moved items; geometry changes call `Place()`
  only; (d) layout-driven moves (Arrange, insert, undo) animate by FLIP: the scene tweens rects for 200 ms
  (UX §5.3 reflow token) and views and edges read the same tween, so lines stay attached; off with Animation effects;
  (e) a **camera** (offset, zoom) applied as one transform on the world layer, pure `Camera` maths (screen↔world,
  zoom at point, fit, reveal rect, a 48-DIP keep-on-screen clamp); wheel/drag pans are synchronous; touchpad (wheel
  events with `PointerDeviceType.Touchpad`) pans, Ctrl/pinch zooms; touch via manipulation events with inertia.
- UIA: unchanged. Pills/cards stay `NodePeer`s with their names; the Outline view stays the screen-reader equivalent;
  the camera transform doesn't change the peers' tree; bounding rects follow the transform.
- Why not Win2D `CanvasControl`/`CanvasVirtualControl` or Skia: text layout, theme/high-contrast brushes, focus visuals
  and every UIA peer (a fragment-root provider for each pill and edge) would be rebuilt by hand, plus a native
  dependency in the editor package, for element counts XAML handles easily; they fix none of root causes 1–6.
  Why not Composition `ShapeVisual` edges now: D-037 still holds; only the live subset is rebuilt per frame.
  Why not a WebView2 board (React Flow/tldraw): D-013. Why not keep the `ScrollViewer` with InteractionTracker
  tricks: the padding wrapper and async `ChangeView` remain, and pan stays bounded by extent.
- Revisit if: T21.8's frame probe shows > 8 ms per gesture frame on a 100-step flow (then edges to a `ShapeVisual`
  with D2D geometry, D-037's own revisit); the camera spike can't get touchpad pan/pinch (then keep the
  `ScrollViewer`, record an X-entry, and do only (a)–(d)).

## D-120 · 2026-11-11 · Canvas gestures: what you see while dragging is what you get
Status: accepted (user, 2026-11-11, same report as D-119); built in M21.
- Decision: a drag shows its result live: the dragged steps, their ports, lines, chips and arrowheads move together,
  snapped to the 8-DIP grid while moving, so the drop changes nothing visible. A step dragged from the library lands
  centred under the pointer (snapped) on empty canvas, as a hand placement (D-115: the first placement freezes the rest);
  dropped on a step it uses the same zones as a step drag (left third Run before, right third Run after, body Run
  together); a dashed landing outline shows where it will go. It never covers another step (user, 2026-11-12): over a
  step's free band or next to one it takes the nearest clear grid spot, keeping room to widen to the 320-DIP pill
  maximum; above or left of the flow it lands there too (the flow's origin moves, the camera pans back). Zoom never changes layout: below 60 % summaries and
  badges are hidden by opacity, so a pill keeps its size. Steps moved by layout (Arrange, insert, delete, undo) glide
  200 ms with their lines attached.
- Why: the user's report: steps that "cut and paste", dots and lines that update only on drop, library drops in the
  wrong place; Miro, Figma and n8n all work this way.
- Why not a library drop on empty canvas that keeps auto layout ("Add a step" where the layout puts it): the user
  explicitly drops at a place; keyboard and double-click adding still use the layout. Why not live snapping off
  (snap only on drop): the drop then visibly jumps up to 4 DIP.
- Revisit if: users want auto layout kept after library drops (then a drop on empty canvas adds without placing).

## D-121 · 2026-11-12 · One button size: shared styles, no per-button Width/Height/Padding
Status: accepted (user, 2026-11-12: "very inconsistent height and size of the buttons").
- Decision: every button is at least `ControlHeight` (32 DIP) tall via implicit Button/ToggleButton styles in
  App.xaml (none for DropDownButton: X-013; the stock one is 32 tall); icon-only buttons use `IconButton` / `SubtleIconButton` / `IconToggleButton` (32×32 square, icon 14),
  primary actions `PrimaryButton` (accent), weekday chips `ChipToggleButton`, canvas connection handles `CanvasHandleButton`
  (24 round, on purpose). Links (HyperlinkButton) pad `0,4`. `XamlButtonSizingTests` fails on any Button/ToggleButton/
  DropDownButton/HyperlinkButton/SplitButton tag that sets Width, Height, MinWidth, MinHeight or Padding.
- Why: WinUI buttons size from their content, so an icon-only (14 px glyph), a text and an icon + text button in one row
  were three heights, and each page patched it with its own Padding 6/8 or Width.
- Why not one `Height="32"` on everything: it clips at larger text scale; MinHeight grows with the text.
- Revisit if: a dense variant is wanted (add a style, don't override a button).

## D-122 · 2026-11-12 · PR review and merge go through `scripts/pr-*.ps1` and one `pr-review` skill
Status: accepted (user, 2026-11-12). Amends D-079 (merge policy wording; children never arm `--auto`).
- Decision: Copilot request/wait, thread listing, reply+resolve, ready check, merge and flaky rerun are scripts with
  short one-line-per-fact output and documented exit codes (`pr-lib.ps1` + `pr-request-review`, `pr-wait`, `pr-threads`,
  `pr-reply`, `pr-status`, `pr-merge`, `pr-rerun`). The `pr-review` skill holds the standing rules; other skills point to it.
  Merging always goes through `pr-merge.ps1` (ready check, then `gh pr merge --merge`), so the user can allow-list that one
  command instead of every session's merge hitting the auto-mode classifier.
- Why: every child session re-invented graphql/polling/transcripts at high usage cost; scripts do it deterministically.
- Why not one big script with verbs: a single named merge command is what makes allow-listing safe.
- Why not Pester: not used in the repo; a plain script test of the pure functions (`scripts/tests/pr-lib.tests.ps1`) runs in CI.
- Revisit if: the PR flow changes (new required checks, a different reviewer bot, auto-merge becomes safe).

## D-123 · 2026-11-12 · Library "Add by hand": audio, Bluetooth and Android devices typed in, stored like picked ones
Status: accepted (user, 2026-11-11: "let's add an ability to add any library item manually")
- Decision: Library › Audio, Bluetooth and Android get an "Add by hand" flyout beside "Add from Windows": Bluetooth = name +
  address (dashes, spaces or none accepted, saved as `B8:20:8E:ED:48:FC`), Android = name + address (host or IP, optional
  `:port`), audio = name text + Output/Input. `ManualItems.Add` checks the fields and refuses a duplicate (same address,
  same adb serial, same name text and direction) with one sentence; the key comes from the name (`NewKey`). The item is the
  same YAML record a picked device makes (no "manual" flag), so a device that appears later matches it by address / name
  text, isn't offered by "Add from Windows" again, and the card's state goes from "Not found on this PC" to live. A run
  with a device that isn't there fails that step with the provider's sentence (Bluetooth: "isn't paired with this PC as
  an audio device"), like any missing device (fail-soft, D-021). Edit = Rename/Remove as for other items.
- Why not every Library kind: a screen layout is raw CCD data only a capture can make (D-019); a bound device is already
  made by hand from two items (D-116); programs and Wi-Fi networks aren't Library kinds (D-041; an SSID is a trigger's
  value). Why not one page-level "Add item" with a kind picker: the Pivot already is the kind, and a second chooser
  duplicates it. Why not a `manual: true` flag: nothing behaves differently, and a flag would block the merge with the
  live device. No `dctl library` commands exist, so none were added.
- Revisit if: users want to type a screen layout (monitor names) or name programs once (then a Programs kind, D-041).

## D-124 · 2026-11-12 · No line through a step in any shape: Tree side joins, lane detours elsewhere
Status: accepted (user's report: check → connect (if it fails) and both → set default stacked set under connect, and the
line from check ran through connect; then "this must work in every layout mode", hand placement too if cheap)
- Rule: no line is drawn through a step other than its two ends (sampled on the drawn line, Curved or Right angles).
- Tree, by placement: a **side join** (a step that also waits for an ancestor of the step it hangs under, i.e. a line
  that skips a level) goes right of its first source's branch, centred up and down between its first and last source,
  so its lines fan in from the side; its own branch hangs under it. Joins are tried one at a time in run order and kept
  only when they cut the crossings, so plain trees and diamonds lay out as before.
- Every shape and hand placement, by routing (`FlowPlacement.Route`): a line that still crosses a step gets a lane
  detour (`EdgeRoute.Lane`): one step out of its port, along a lane 14 DIP past every step beside its run (nearer side;
  right/below on a tie), one step into the target. Top to bottom (rows are the columns) and Circle rely on it; the
  flow shifts right/down if a lane would leave the canvas. Left to right with nothing placed keeps D-111 unchanged.
  A drag re-routes still lines the moved step would hit, so the drop shows what the commit draws.
- Why not move steps in Top to bottom, Circle or hand placement: their places are the shape's (rows = columns, ring =
  run order) or the user's. Why not dummy-node layers: a column/row of empty slots per long line (D-111).
- Known limit: steps placed by hand closer than 16 DIP to a port (the detour's first step) can't be cleared by any lane;
  the line then stays as it was (fewest crossings). Auto shapes always leave more room than that.
- Revisit if: lines should dodge other lines.
- Amended 2026-11-12 (F-040): a line within 8 DIP of a step (`LineClearance`) also detours; lane candidates include the
  gap next to each step beside the run (14 DIP off), least stray wins; detours sharing a lane over the same stretch are
  spread 12 DIP apart (`Separate`), shortest run innermost, so lanes nest. Commit and drag preview both spread.
  Why not one lane per line always: lines that never meet would wander off for nothing.

## D-125 · 2026-11-12 · A flow input can be a bound device (`kind: bound`)
Status: accepted (user, 2026-11-12: "let's add bound devices as inputs for the Flows")
- Decision: `FlowInput.Kind` may be `LibraryKind.Bound` (YAML `kind: bound`, editor kind "Bound device"; default and
  passed values are bound keys). `$name` of a bound input fits Bluetooth and audio settings (`LibraryEdits.Refers`, the
  same rule as a bound key or a `kind: bound` pick); the runner substitutes the bound key and `Library.ResolveBluetooth/
  ResolveAudio` pick the side, so there is no second resolution path. A Bluetooth or audio input still takes a bound key.
- Validation: a missing default is the input's error, as for every kind (the config loads; that flow keeps its last
  good version, D-021); a step using the input whose default lacks that step's side fails like a step naming the bound
  key (D-116). Rename follows into defaults; "Used by" counts the input, so Remove stays off.
- Why not keep "use an audio or Bluetooth input with a bound value" (the old refusal): one input then can't feed both a
  Bluetooth and an audio step, which is what a bound device is for. Why no "disabled input" state for a missing
  default: no other kind has one; a disabled input would leave its steps with no device anyway.

## D-126 · 2026-11-12 · Canvas camera and drops: 10 % zoom, auto-pan that runs out, drops that keep the gaps
Status: accepted (user, 2026-11-12, findings F-001, F-006, F-015, F-030 of the M22 live test); built T22.1.
- Decision: zoom 10–200 % (`MinZoom` 0.1); Fit, opening a flow and Layout › Arrange / Reset layout fit the whole flow.
  Auto-pan tops out at 600 DIP/s (linear in band depth) and stops along an axis once one column's room (`EdgeRoom`
  384 DIP) past the still steps is on screen; a step drag released outside the canvas (over the inspector or palette)
  is no drop. A free move has no top/left clamp (it moves the origin and the camera pans back, as a library drop does);
  its lead snaps to the 8-DIP grid or to a still step's column within half a column gap, then takes the nearest offset
  that keeps the layout's column gap beside and row gap above/below every still step (`MoveSnap`, preview == commit).
- Why: Fit stuck at 50 % cut off a 95-step flow; 1200 DIP/s with no end scrolled the whole flow away in 2 s; the move
  clamp refused a spot a library drop accepted; drops overlapped by 8 DIP and sat off-column (UX §5.9).
- Why not snap only the drop (keep the free preview): the drop then jumps (D-120). Why not refuse a crowded drop: the
  library drop already takes the nearest free spot, so a move does the same. Why not time-based auto-pan acceleration:
  it differs with Animation effects off (T21.9).
- Revisit if: the column magnet feels sticky live (shrink it below half a column gap), or users want to pan past the
  content while dragging (raise `EdgeRoom`).

## D-127 · 2026-11-12 · "n of m" is every step, fixed at the start, one counter for the pop-up and the canvas
Status: accepted (F-012, M22 batch 13; supersedes D-065's counting sentence)
- Decision: `Core/Runs/RunProgress` is the only "n of m": m = the flow's `StepNode`s when the run starts (fallbacks
  included, cards not: their members count), n = steps with a `NodeFinished` (any outcome, each once). The pop-up
  (`RunPopupModel`) and the canvas (`RunOverlayViewModel`) both use it; a nested call's progress only fills its
  call step's share.
- Why: the user saw "18 of 20" on the canvas and "18 of 21" in the pop-up (the pop-up counted cards, the canvas
  didn't) and totals that grew mid-run as fallbacks started, so the bar jumped backwards. An unneeded fallback is
  skipped (NotNeeded) right after its source succeeds, so n still reaches m without counting anything twice.
- Why not keep "fallbacks join m when they start": any growing m moves the bar back. Why not leave fallbacks out for
  good: a fallback that runs would then do work the bar doesn't show. Why not send m from the engine on the pipe:
  both sides already hold the same flow (the canvas the saved one, D-047), and one Core class is one definition.

## D-128 · 2026-11-12 · Pick device's Default is a Library picker that stores the key; a hand-typed name still resolves
Status: accepted (F-029, M22 T22.6; user chose option A; supersedes nothing, D-117 stays)
- Decision: `prompt.pick`'s `default` shows a picker of the Library items of the step's `kind` (`ParamSpec.LibraryFrom: "kind"`, still
  `Text` so any string loads) and stores the item's key. `PickResults.DefaultKey(library, kind, text)` is the one resolver (editor, validator,
  runtime, dry run): an item key of that kind (bound devices count for audio/Bluetooth, D-116), else the one item whose name matches
  (case-insensitive); none or several → null. A name in an old file shows as its item and is rewritten only when the person changes the
  picker (no silent rewrite). A default that resolves to nothing is a **warning** (fail-soft, D-021: the pick works, `use-default` ends as "No answer").
  Changing `kind` clears a default that doesn't resolve in the new kind; one that still does stays.
  A Library rename rewrites a default that is the renamed key and "Used by" counts it (`LibraryEdits.Holders`); a hand-typed name is left alone.
- Why: the other device params are pickers; a free-text name drifted from the Library (renames, two items with one name).
- Rejected: a new `LibraryRef` kind with a dynamic `Library` (every `spec.Library!` site would need a null path); rewriting names to keys on load.

## D-129 · 2026-11-12 · Step values: typed values declared per step type, `{step.value}` in text settings that take them
Status: accepted, built (M23 T23.1-T23.7, PRs 223-230; user, 2026-11-12: all five PR #217 questions as recommended; open items in docs/step-values/DESIGN.md). Amends D-064, D-117
- Decision: `ProviderDescriptor.Values` (`ValueSpec`: name, Text/Number/Device kind, title, Sensitive, OnFailure) + `error`
  on every step + run values (`@flow`, `@trigger`, `@time`, `@date`) + `{$input}`. Text settings opt in
  (`ParamSpec.TakesValues`) and hold `{step.value|fallback}`; device settings keep `$result:<id>` (widened later to
  `$result:<id>.<value>`). One resolver `Core/Model/StepValues` (parse, must-wait-for, problems, fill) for validator,
  runner, dry run and editor. Missing value: fallback, else empty in text; a device setting fails (D-117). Scripts get
  values as `DAZE_<STEP>_<VALUE>` environment variables, never spliced into code. Editor word: "value".
- Why: the user asked (2026-11-12) for step information as placeholders with pills; typed, catalog-declared values keep
  every reference checkable before a run (D-064's reason), and opt-in fields leave scripts, JSON and `%VAR%` untouched.
- Why not one free-text result per step: a sentence, not a value; can't fill typed settings or be checked. Why not
  mutable flow variables: races between parallel branches, unknowable "is it set yet", a second rename system;
  single-assignment "Make text" and flow outputs cover the need. Why not Scriban/Fluid: reflection, AOT warnings.
  Why not `${x}` / `%x%` / `{{x}}`: PowerShell, Windows environment variables, noise; `{x.y}` with a strict grammar
  leaves other braces as text.
- Revisit if: a value must survive a restart (flags, D-029), or format specifiers are asked for.

## D-130 · 2026-11-12 · Device values beyond Pick: `$result:<id>.<value>`; `audio.setDefault` gives `previous`
Status: accepted, built (M23 T23.6, as D-129 planned; no live default-device switch yet). Supersedes D-117's rule that `$result:` names only a Pick step
- Decision: a device setting (or a value `flow.run` passes) may hold `$result:<id>.<value>` for any Device value of a step
  that always finishes first and whose Library kind fits (D-116 `Fits`); `$result:<id>` alone = its `device` (a Pick's
  keeps D-117's sentences). Filled in `StepValues.Fill` before the lock, so the lock is the filled device's (two keys of one
  Bluetooth device share it). Missing at run time = the step fails (""Set default device" gave no Previous default."),
  never a fallback device; the validator warns ahead when the producer may not give it (non-Pick only, D-117 flows unchanged).
  `audio.setDefault` gives `previous`: the default it replaced (first role it sets), mapped back to the Library audio item
  whose match the endpoint name contains (same direction, longest match), read only when a later step uses it. The device
  picker's group "From earlier steps" (was "Picked earlier in this flow") lists every such value. Dry run: "Would use …".
- Why the Library key and not the endpoint id: device settings, locks and pickers are Library-keyed; a raw endpoint would
  need a second identity everywhere. A previous default outside the Library gives no value (the step says so) rather than
  guessing. Why not a separate "remember default" step: the value is exactly what the step replaced, at that moment.
- Revisit if: users want to put back a device that isn't in the Library (add it silently, as pickers do from Windows).

## D-131 · 2026-11-12 · Text inputs, flow outputs and Make text are single-assignment values (T23.7, DESIGN §9)
Status: accepted, built (M23 T23.7, PR 230). Amends D-064 (text inputs)
- Decision: a flow input may be `kind: text` (`FlowInput.Kind` null): any default (empty allowed, ≤ 1,000 chars), used as
  `{$name}` in text that takes values, never in a device setting, never asked (`ask: true` on text is an error: typing needs
  focus, D-107). A `flow.run` passes it text that takes values, filled in the caller before the call; passing one's own text
  input on (`greeting: $greeting`) passes it escaped, so a value that looks like a reference is never read twice. A flow's
  `outputs:` (name → text that takes values, ≤ 20) are filled once when its run succeeds (fully or partly); a waiting
  `flow.run` gives them as its values (`{call.device}`, Text, title from the name). A call's output the called flow doesn't
  give is a **warning** (run time: fallback, else empty), so renaming or removing an output never stops its callers.
  `text.make` (Make text, Flow group) gives its filled text as `text`; Sensitive when its text uses a Sensitive value, as a
  call's output that holds one, and a text input the caller filled from one is masked in the called run.
- Why: DESIGN §9 accepted; no variable a branch can overwrite, every reference stays checkable before a run. Why the
  outputs lookup is a `Func<string, Flow?>` parameter and not catalog data: a call's values depend on another flow, so the
  validator, runner (`IFlowStarter.Resolve`) and editor pass their own view of the flows; null = unknown, any name is text.
- Why rename in the editor touches only the open flow: callers live in other files' flows (a draft is saved alone, D-047);
  `FlowOutputs.Rename` rewrites callers for a config-level change, the inspector tip names who now shows a fallback.
- Revisit if: users want asking for text at start (needs a no-focus typing prompt), or Make text in a separate palette group.

## D-132 · 2026-11-12 · `notify` shows one notice at a time, each alone for 5 s (fix for live check 5 and 6)
Status: accepted, built. Amends D-029
- Decision: the engine's `notify` goes through `NoticeSpacer`: the first text shows at once, each next one waits 5 s after
  the previous (≤ 20 waiting, oldest dropped); the timer exists only while something waits.
- Why: live acceptance showed a second Show message missing (flows 5 and 6). Not a value bug (Runtime tests cover two
  messages after one Make text, and a callee's output); `notify` is a tray balloon and Windows keeps one per icon, so a
  second posted milliseconds later replaces the first (in flow 5 the callee's message was replaced by the caller's).
- Why not: one balloon per flow (still replaced across flows); a real pop-up window for notices (M5 pop-up is run
  progress, new window code, focus rules); a longer gap (the user waits for text already read).
- Revisit if: notices become their own stack in the run pop-up.


## D-135 · 2026-11-13 · Roadmap M24 to M28 from the gap analysis; M28 rows wait for a yes each
Status: proposed (the user's standing instruction, 2026-11-12: after the analysis the coordinator builds as far as limits allow)
- Decision: the next work is PLAN M24 to M28, ranked in docs/roadmap/GAPS.md. M24 to M27 extend recorded decisions (D-081, D-082,
  D-107, D-118, D-129) and reverse none, so they start without another question. Every M28 row reverses a recorded "No" or "later"
  (REVIEW §3 through D-102, D-029, D-107 / D-131 text prompts) or costs money (signing): none starts before the user says yes to
  that row, and each yes is its own superseding entry.
- Why: against AutoHotkey, Power Automate Desktop, PowerToys, Home Assistant, Stream Deck, Task Scheduler, Keyboard Maestro,
  Hammerspoon and Shortcuts, the product is ahead on devices and failure handling and behind on deciding on a value, conditions on
  automatic starts, events that name their device or program, a recipe gallery and being called from outside. REVIEW §3's "No" rows
  (power, mute toggle, clipboard, keys, windows) were judged before schedules, events and values existed, so they are asked again.
- Why not start with keys, windows and power (the widest expectation): recorded "No"; the hard rule says ask first. Why not
  if / else, changing variables, plugins, UI automation, hotstrings, a web server, other languages: D-082, D-129, D-006, GOALS,
  a resident keyboard hook, D-008 (firewall rule), cost per string change (GAPS §5).
- Revisit if: the user answers M28's rows, or a second user appears (signing, languages and sharing move up together).
- Ref: docs/roadmap/GAPS.md (matrix, sketches, sources).

## D-136 · 2026-11-13 · Which device on events: names read off the callback, `{@event}` run value (T24.6)

- Decision: `device:` on `device.added/removed` (name contains, any case) and `audio.default-changed` (a Library audio key; matched by the item's `match` text and flow against the new default's name). Sources fill `TriggerEvent.Detail` (+ `Kind` input/output for audio); run value `{@event}` = Detail, passed `TriggerService` > `FlowLauncher` > `FlowRunner` > `RunValues.Event` (empty in a called flow).
- How, AOT/idle-zero: names are read on the thread pool (devices: cfgmgr32 property calls) or the lazy COM worker (audio, D-009), never inside the system callback. A removed device is already unreadable, so `DeviceSource` keeps link > name for what it saw while loaded (seeded once on Load, dropped on Unload): no timer, no polling. Unreadable name = event raised without a detail; a trigger with a `device:` filter then skips it, one without works as before.
- Why not fold different names of one device: the USB-device interface raises once per physical device; debounce and cooldown stay per detail (D-107).
- Not verified live by the agent: plug a USB device in and out; switch the default sound device.

## D-133 · 2026-11-13 · `flow.stop`: a quiet stop is "done" for a caller, "As failed" is a failure (T24.1)
Status: accepted, built
- Decision: `flow.stop` (Flow group, last) has `mode` quiet (default) or failed, and `message` (takes values). Quiet throws
  `StepEndsFlowException { IsStop }`: the node ends Cancelled with "Stopped here" (": message" when given), the run is Quiet
  and Cancelled (no pop-up, no tray problem glyph, EngineHost skips Cancelled), parallel branches are cancelled.
  `RunResult.Stopped` = every quiet end of the run was a stop; a waiting caller (`flow.run`) then counts its step as done and
  carries on. A menu closed with nothing picked still ends the caller too (D-054). Failed throws `StepFailedException`: the
  step fails, a failure link or the flow's stop-on-failure rule decides the rest, the caller's step fails "X didn't finish:
  message". Dry run and a step test say "Would stop here" and stop nothing. The validator warns about a step linked after a stop.
- Why: the guard after a check needed a silent end (G-02); a guard inside a building block must not kill its callers (the
  caller asked for a result, and the check found nothing to do), while "As failed" gives the caller a way to react.
- Why not reuse the menu rule for callers: one silent stop would cancel the whole automatic flow, a surprise for a shared flow.
- Known edge: "As failed" with `onFailure: ignore/skip` (step settings) goes on like any failed step; that is the user's choice.

## D-134 · 2026-11-13 · `process.open`: one target handed to the shell, values escaped by where they sit (T24.2)
Status: accepted, built
- Decision: `process.open` "Open file or address" (Programs, after Run program): `target` takes values (new `FillRule.Target`) and goes to ShellExecute as the file name only, no arguments, no wait. Where the written text starts with a scheme (two letters or more: `https:`, `steam:`, `ms-settings:`) a value is percent-encoded like `Url`, so it cannot change the scheme or break out of the address; otherwise it is a path or name and `%` in a value is guarded like `Argument` (a written `%VAR%` still expands, a value's never). The provider refuses an empty target, quotes and line breaks, a path that does not exist (a bare name like `notepad.exe` is left to the shell) and schemes that run script or a diagnostic tool (`javascript`, `vbscript`, `data`, `ms-msdt`, `search-ms`, `ms-officecmd`); the validator errors on a written target it would always refuse. No association: "Nothing opens X".
- Why: a flow should open a game, a page or a folder without a script (G-20); handing one string to the shell, never a command line, keeps a value from adding arguments (D-129).
- Why not: an allow-list of schemes (blocks `spotify:`, `obsidian:` and the other app links); percent-encoding path values (breaks paths with spaces); `process.run` accepting addresses (a program with arguments is a different contract).
- Known edge: a value that is the whole target (`{p.output}`) is opened as given, whatever scheme it holds (the user chose to open that value); only the refused schemes and a missing path stop it.

## D-137 · 2026-11-13 · `value.check`: two texts compared, normalised first, the failure link is the "else" (T24.3)
Status: accepted, built
- Decision: `value.check` "Check value" (Flow group, a check: `dctl check value.check value=5 is=atLeast to=3` works). `value` and `to` take values (plain fill, no escaping: nothing is run, only compared); `is` is one of is, isNot, contains, doesNotContain, startsWith, endsWith, isEmpty, isNotEmpty, atLeast, below; `ignoreCase` on by default (ordinal, never culture). Both sides are trimmed of spaces first. is / isNot compare as numbers when both sides are numbers (`5` is `5.0`), else as text; atLeast / below need two numbers (invariant culture, no NaN or infinity, `3,5` is not a number) and fail with a sentence otherwise, as does contains/startsWith/endsWith with nothing to look for. A missing value follows D-129 (fallback, else empty): empty is not zero, so a number comparison with it fails with the sentence. Gives `result` even on failure (D-117); it quotes both sides, so it is Sensitive when either side uses a Sensitive value (errors and summaries are masked by the runner as for any step). The validator errors on a written comparison it would always refuse (empty `to`, a written non-number for atLeast/below) and leaves text holding a reference to the run.
- Why: every comparable tool has If on a value (G-60); a failing check keeps D-082's two kinds of link, no new edge kind. Why not text compare for atLeast ("10" below "9"): wrong for the common case (battery, status codes). Why not locale numbers: the same flow must behave the same on every PC.
- Revisit if: users compare dates or versions, or want and/or in one step (chain checks instead).

## D-138 · 2026-11-13 · `audio.media`: playback commands as app commands to the taskbar, not key presses or SMTC (T24.5)
Status: accepted, built
- Decision: `audio.media` "Control media" (Audio group): `action` is playPause (default), play, pause, stop, next or previous (captions as `android.media`'s; no fast forward / rewind, the PC's media apps rarely handle them). The provider sends one `WM_APPCOMMAND` (`APPCOMMAND_MEDIA_*`) with `SendMessageTimeout` (2 s, abort if hung) to the `Shell_TrayWnd` window: the message a media key turns into, so Windows hands it to the app that owns media. It fails only when the taskbar is absent or does not answer; it cannot tell whether anything played. The action is a plain choice, so the validator's generic choice check is the whole rule and the provider accepts exactly the same words (exact match, no trimming). Behind `IMediaCommands`; tests use a fake and never send a command.
- Why: no key simulation (nothing can reach the focused window or a held modifier, no focus change), no admin, no resident object, one P/Invoke (`LibraryImport`, AOT-safe). Why not SendInput VK_MEDIA_*: it is a synthetic key, the T28.4 question. Why not WinRT SMTC (`GlobalSystemMediaTransportControlsSessionManager`): needs the WinRT projection (trim / AOT risk) and a session manager held open or created per run; "the current session" is also a guess when several apps play.
- Revisit if: an app ignores the command (then ask for per-app SMTC targeting), or users want the playing title (a value, SMTC read).

## D-139 · 2026-11-13 · `network.wake`: one magic packet per connected IPv4 network, MAC normalised then checked (T24.7)
Status: accepted, built
- Decision: `network.wake` "Wake over network" (Network group, before the checks): `mac` takes values (plain fill, nothing is run). `MacAddress.TryParse` (Core, shared by validator and provider) trims, then accepts `aa:bb:cc:dd:ee:ff`, `aa-bb-...`, `aabb.ccdd.eeff` or 12 bare hex digits in any case; mixed separators, wrong group sizes and any other character are refused, so a value holding a second line or `; calc` is refused whole. The validator errors on a written MAC it would always refuse and leaves text holding a real reference to the run (a stray brace is not a reference). The packet is 6x FF then the MAC 16 times, UDP port 9 only, broadcast on every Up non-loopback, non-tunnel adapter's IPv4 address with a mask that has host bits (link-local 169.254 skipped), each from a socket bound to that adapter's address so it leaves by that network. Sockets are created and closed inside the send (idle = zero, D-009). One network failing does not stop the others; none connected, or all failing, fails with a sentence.
- Why: G-28, "wake the NAS, wait until it answers" with T24.8. Why not port 7 as well: the row says one packet; port 9 is what NICs and routers expect. Why not directed (routed) broadcast or a router relay: needs a known subnet and router support, and the row says each connected network.
- Not verified live: the packet reaching a sleeping PC (tests use a fake sink and send nothing).
