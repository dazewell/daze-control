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
Status: accepted (M13 design; user, 2026-11-03: inputs ship inside P6)
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
  would skip one, it ends **NotNeeded** (new `NodeOutcome`; fires `always` like Skipped). Counting: "n of m" leaves
  fallback steps out of m until one starts. Summary: Succeeded when every unit is Succeeded, NotNeeded, or Failed /
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
Status: accepted (M13 design; user, 2026-11-03: "a layout menu… a couple more strategies… make it wider"); built T13.6
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

